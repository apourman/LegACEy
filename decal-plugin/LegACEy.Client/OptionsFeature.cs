using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The Dereth options window in place of retail's options panel, held as the social panel is: retail's key and toolbar button open and close
/// our window, and its close box closes retail's panel. The Character switches are retail's PlayerModule options, read and set through
/// retail's own GetOption and SetOption; Apply then calls retail's SaveToServer, so the server has them at once. The Gameplay buttons make the calls
/// retail's buttons make (gmGameplayOptionsUI::ListenToElementMessage). Call everything on the game thread.
/// </summary>
internal sealed unsafe class OptionsFeature : IOptionsGame, IDisposable
{
    private const long RefreshMs = 1000;
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb (near build, shifted)";
    // PlayerModule::GetOption / SetOption (ThisCall PlayerModule*, PlayerOption[, bool]); SetOption calls the option's own setter, which
    // sends it at once or marks the module for SaveToServer. GetDefaultOptionValue (ThisCall, PlayerOption) -> bool.
    private const uint GetOption = 0x005D4AF0, SetOption = 0x005D4F20, GetDefaultOption = 0x005D3A30;
    // CPlayerModule::SaveToServer (ThisCall CPlayerModule*, bool force): sends the options when the module is marked changed. CPlayerModule
    // is at CPlayerSystem + 0x30, its PlayerModule base 4 bytes later.
    private const uint SaveToServer = 0x0059B670;
    private const int CPlayerModuleOffset = 0x30;
    // gmGameplayOptionsUI's Exit Game sends global message 1 with 0x10000027, Use Mouse Turning Settings message 0xC with 0 (gmConfigUI
    // answers it with SetMouseTurningDefaults); both through UIElementManager::BroadcastGlobalMessage.
    private const uint ExitGameMessage = 1, ExitGameParameter = 0x10000027, MouseTurningMessage = 0xC;
    // Inside gmGameplayOptionsUI::ListenToElementMessage: the two broadcasts, by button id.
    private const uint GameplayBroadcasts = 0x0049E50C;
    // Urgent Assistance and Report Abuse open this page with ShellExecute "open".
    private const string SupportPage = "http://support.turbine.com/ics/support/ticketnewwizard.asp?style=classic";

    // Each list is checked on its own: a mismatch turns off the switches or the broadcast buttons, not both.
    private static readonly IReadOnlyList<NativeUiEntry> OptionNatives = new[]
    {
        Entry("PlayerModule::GetOption (PlayerOption 0: CharacterOptions1 at +0x8C bit 0x2)", GetOption,
            "8B 44 24 04 85 C0 75 0E 8B 81 8C 00 00 00 D1 E8 83 E0 01 C2 04 00"),
        Entry("PlayerModule::SetOption (PlayerOption 0 sets bit 0x2 and calls vtable +0x14)", SetOption,
            "8B 44 24 04 85 C0 75 47 8B 91 8C 00 00 00 8A 44 24 08 D1 EA 80 E2 01 3A D0"),
        Entry("PlayerModule::GetDefaultOptionValue (table of options 0-0x2A)", GetDefaultOption, "8B 44 24 04 83 F8 2A 77 13 0F B6 80 5C 3A 5D 00"),
        Entry("CPlayerModule::SaveToServer (sends when +0x1A8 is set or forced)", SaveToServer,
            "56 8B F1 8A 86 A8 01 00 00 84 C0 75 08 8A 44 24 08 84 C0 74 0C 8D 46 04 50 E8"),
        Entry("CPlayerSystem getter (0x0087119C)", RetailHudCatalogue.GetPlayerSystem, "A1 9C 11 87 00 C3")
    };

    private static readonly IReadOnlyList<NativeUiEntry> BroadcastNatives = new[]
    {
        Entry("gmGameplayOptionsUI: Use Mouse Turning Settings broadcasts (0xC, 0), Exit Game (1, 0x10000027)", GameplayBroadcasts,
            "2D CC 05 00 10 74 0E 83 E8 4B 75 18 68 27 00 00 10 6A 01 EB 04 6A 00 6A 0C 8B 0D 3C E0 83 00 E8 90 CF FB FF"),
        Entry("UIElementManager::BroadcastGlobalMessage body", RetailHudCatalogue.BroadcastGlobalMessage + 1, "8B 5C 24 08 33 D2 8B C3 F7 B1 8C 01 00 00")
    };

    private readonly ILegACEyPluginHost _host;
    private readonly RetailPanelTakeover? _panel;
    private readonly Func<IReadOnlyDictionary<uint, string>> _strings;
    private readonly Action<string> _log;
    private OptionsWindow? _view;
    private Action? _hide;
    private bool _shown;
    private long _refreshedAt;
    private bool? _optionsValid, _broadcastsValid;

    public OptionsFeature(ILegACEyPluginHost host, IRetailPanelPort? port, Func<IReadOnlyDictionary<uint, string>> strings, Action<string> log)
    {
        _host = host;
        _strings = strings;
        _log = log;
        if (port != null) _panel = new RetailPanelTakeover(port, OpenChanged, log);
    }

    /// <summary>Once a frame in game. With <paramref name="enabled"/> false retail's panel is given back, and our window closes.</summary>
    public void Tick(bool enabled, bool canShow)
    {
        _panel?.Tick(enabled, canShow);
        // Options change with no message (retail's keys, the server), so an open window reads them again about once a second.
        var now = Environment.TickCount64;
        if (_shown && _view != null && now - _refreshedAt >= RefreshMs)
        {
            _refreshedAt = now;
            _view.Refresh();
        }
    }

    /// <summary>Logoff, unload or the windows failing: gives retail's panel back and drops the window.</summary>
    public void EndSession()
    {
        _panel?.EndSession();
        _view = null;
        _hide = null;
        _shown = false;
    }

    public void Dispose() => _panel?.Dispose();

    private void OpenChanged(bool open)
    {
        if (open == _shown) return;
        if (!open)
        {
            Hide();
            return;
        }
        try { Open(); }
        catch (Exception exception)
        {
            _log($"The options window could not open: {exception.Message}");
            _panel?.Close();
        }
    }

    private void Hide()
    {
        _shown = false;
        _view?.Hidden();
        _hide?.Invoke();
    }

    private void Open()
    {
        IGameArtSource? art = null;
        try { art = _host.Art; }
        catch (InvalidOperationException) { }
        // A window the client released is neither open nor hidden: the view is stale, so a new one is built.
        _view ??= new OptionsWindow(this, art);
        var view = _view;
        var place = _panel?.HeldPlace ?? new Point(200, 150);
        var definition = new WindowDefinition(OptionsWindow.WindowId, "Options", OptionsWindow.Width, OptionsWindow.Height,
            DerethWindow.TitleBarHeight, new DerethClientTheme(), OptionsWindow.Sizing);
        var opened = _host.OpenWindow(definition, place, close =>
        {
            _hide = close;
            // The close box closes retail's panel, whose report hides the window; without the takeover it hides the window itself.
            view.Close = () =>
            {
                if (_panel?.Holds == true) _panel.Close();
                if (_shown) Hide();
            };
            return view.Window;
        }, error =>
        {
            _log($"The options window failed: {error.Message}");
            if (ReferenceEquals(_view, view)) _view = null;
            _shown = false;
            _panel?.Close();
        }, ownChrome: true);
        if (!opened)
        {
            _panel?.Close();
            return;
        }
        _shown = true;
        _refreshedAt = Environment.TickCount64;
        view.Refresh();
    }

    // The game, as the window reads it.

    bool? IOptionsGame.Option(int option) =>
        PlayerModule() is { } module ? ((delegate* unmanaged[Thiscall]<IntPtr, int, byte>)GetOption)(module, option) != 0 : null;

    bool IOptionsGame.DefaultOption(int option) =>
        PlayerModule() is { } module && ((delegate* unmanaged[Thiscall]<IntPtr, int, byte>)GetDefaultOption)(module, option) != 0;

    void IOptionsGame.Apply(IReadOnlyDictionary<int, bool> options)
    {
        if (PlayerModule() is not { } module) return;
        foreach (var option in options)
            ((delegate* unmanaged[Thiscall]<IntPtr, int, byte, void>)SetOption)(module, option.Key, option.Value ? (byte)1 : (byte)0);
        // The PlayerModule base sits 4 bytes into CPlayerModule.
        ((delegate* unmanaged[Thiscall]<IntPtr, byte, void>)SaveToServer)(module - (RetailToolbarCatalogue.PlayerModuleOffset - CPlayerModuleOffset), 0);
    }

    string? IOptionsGame.Text(string name) => _strings().TryGetValue(CharacterNames.Hash(name), out var text) ? text : null;

    // Configure Keyboard and In-Game Help Files open through the button's own property, not yet followed; they stay dimmed.
    bool IOptionsGame.CanPress(GameplayButton button) => button switch
    {
        GameplayButton.ConfigureKeyboard or GameplayButton.HelpFiles => false,
        GameplayButton.ExitToCharacterSelection => NativeUi.Ready,
        GameplayButton.ExitGame or GameplayButton.MouseTurning => BroadcastsValid(),
        _ => true
    };

    void IOptionsGame.Press(GameplayButton button)
    {
        switch (button)
        {
            case GameplayButton.ExitToCharacterSelection:
                if (NativeUi.Ready) NativeUi.RequestLogOut();
                break;
            case GameplayButton.ExitGame:
                Broadcast(ExitGameMessage, ExitGameParameter);
                break;
            case GameplayButton.MouseTurning:
                Broadcast(MouseTurningMessage, 0);
                break;
            case GameplayButton.UrgentAssistance:
            case GameplayButton.ReportAbuse:
                try { Process.Start(new ProcessStartInfo(SupportPage) { UseShellExecute = true }); }
                catch (Exception exception) { _log($"Could not open the support page: {exception.Message}"); }
                break;
        }
    }

    private void Broadcast(uint message, uint parameter)
    {
        if (!BroadcastsValid()) return;
        var manager = Marshal.ReadIntPtr(new IntPtr(NativeUi.Manager));
        if (manager != IntPtr.Zero)
            ((delegate* unmanaged[Thiscall]<IntPtr, uint, uint, void>)RetailHudCatalogue.BroadcastGlobalMessage)(manager, message, parameter);
    }

    private bool BroadcastsValid() => _broadcastsValid ??= NativeUi.Ready && NativeUiCatalogue.Validate(NativeUi.ReadMemory, _log, BroadcastNatives,
        "The options window's Exit Game and Use Mouse Turning Settings are off: the running client does not match retail's buttons.");

    /// <summary>PlayerModule, at CPlayerSystem + 0x34, once the option natives have matched; null outside the game or when they did not.</summary>
    private IntPtr? PlayerModule()
    {
        _optionsValid ??= NativeUi.Ready && NativeUiCatalogue.Validate(NativeUi.ReadMemory, _log, OptionNatives,
            "The options window's switches are off: the running client does not match retail's option calls.");
        if (_optionsValid != true) return null;
        var system = Marshal.ReadIntPtr(new IntPtr(RetailToolbarCatalogue.PlayerSystem));
        return system == IntPtr.Zero ? null : system + RetailToolbarCatalogue.PlayerModuleOffset;
    }

    private static NativeUiEntry Entry(string name, uint address, string bytes) =>
        new(name, address, NativeUiCatalogue.Bytes(bytes), Source, "ThisCall");
}

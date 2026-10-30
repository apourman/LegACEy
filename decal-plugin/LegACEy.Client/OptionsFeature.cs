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
/// retail's buttons make (gmGameplayOptionsUI::ListenToElementMessage, UIElement_Button::HandleButtonClick). The Config settings are retail's
/// UserPreferences, read and set through the calls retail's option widgets make. Call everything on the game thread.
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

    // The UserPreferences calls retail's option widgets make (cdecl, PStringBase<char>& name, then the value or where to put it; true when
    // the preference exists): get/set a switch, a whole number, a menu value and a decimal; a slider's range; a preference's label, tooltip
    // and string table; a menu's string ids, values and text. Then CM_UI's change notices, the menu one with the old and new value.
    private const uint GetSwitch = 0x005DEC90, SetSwitch = 0x005DEB90, GetWhole = 0x005DECD0, SetWhole = 0x005DEBD0, GetChoice = 0x005DED10,
        SetChoice = 0x005DEC10, GetDecimal = 0x005DED50, SetDecimal = 0x005DEC50, DecimalRange = 0x005DE9E0, WholeRange = 0x005DEA30,
        Description = 0x005DE940, ChoiceIds = 0x005DEAC0, ChoiceValues = 0x00438750, ChoiceStrings = 0x00438860,
        PreferenceChanged = 0x0047A740, MenuChanged = 0x0047A790;
    // Freeing what the menu calls hand back: operator delete for the id and value arrays, PStringBase<char>'s vector deleting destructor
    // for the text.
    private const uint Free = 0x005DF164, FreeStrings = 0x00407920;
    // UIElement_Button::HandleButtonClick sends a button's property 0x12 as an input action: through UIElementManager's first vtable slot,
    // once the input gate at 0x00837FF4 answers its +0x44 call. Configure Keyboard's action is 0x1000001F, In-Game Help Files' 0x7B.
    private const uint ButtonAction = 0x00472227, InputGate = 0x00837FF4, KeyboardAction = 0x1000001F, HelpAction = 0x7B;

    /// <summary>gmConfigUI's preference names: each a PStringBase&lt;char&gt; global retail builds at start-up, so the calls get retail's own names.</summary>
    private static readonly IReadOnlyDictionary<string, uint> PreferenceNames = new Dictionary<string, uint>
    {
        ["Camera.AlignToSlope"] = 0x0083F59C, ["Camera.Stiffness"] = 0x0083F5A0, ["Camera.AdjustmentSpeed"] = 0x0083F5A4,
        ["Sound.SoundDisabled"] = 0x0083F5A8, ["Sound.SoundVolume"] = 0x0083F5AC, ["Sound.AmbientSoundDisabled"] = 0x0083F5B0,
        ["Sound.AmbientSoundVolume"] = 0x0083F5B4, ["Sound.InterfaceSoundDisabled"] = 0x0083F5B8, ["Sound.InterfaceSoundVolume"] = 0x0083F5BC,
        ["Sound.SoundFeatures"] = 0x0083F5C0, ["Sound.PlaySoundOnlyWhenActive"] = 0x0083F5C4, ["Display.Resolution"] = 0x0083F5D8,
        ["Display.FullScreen"] = 0x0083F5DC, ["Display.SyncToRefresh"] = 0x0083F5E4, ["Render.TextureFiltering"] = 0x0083F5E8,
        ["Render.BuildingDetailTextures"] = 0x0083F5F0, ["Render.FieldOfView"] = 0x0083F5F4, ["Render.LandscapeTextureDetail"] = 0x0083F5F8,
        ["Render.EnvironmentTextureDetail"] = 0x0083F5FC, ["Render.LandscapeDrawDistance"] = 0x0083F604, ["Render.ScreenBrightness"] = 0x0083F608,
        ["Render.AutomaticDegrades"] = 0x0083F618, ["Render.GraphicsPerformance"] = 0x0083F61C, ["Render.DegradeDistance"] = 0x0083F620,
        ["Render.MultiPassAlpha"] = 0x0083F624, ["Input.MouseLookSensitivity"] = 0x0083F690, ["Input.InvertMouseLookYAxis"] = 0x0083F698,
        ["Input.UseMouseTurning"] = 0x0083F69C, ["UI.ChatFontFace"] = 0x0083F6C8, ["UI.ChatFontSize"] = 0x0083F6CC
    };

    private const string Lookup = "8B 44 24 04 83 EC 0C 50 8D 4C 24 04 51 B9 4C 51 82 00 E8";
    private static readonly IReadOnlyList<NativeUiEntry> PreferenceNatives = new[]
    {
        Wrapper("switch", GetSwitch, "59 44 E5 FF", "04"), Wrapper("set switch", SetSwitch, "59 45 E5 FF", "08"),
        Wrapper("whole number", GetWhole, "19 44 E5 FF", "0C"), Wrapper("set whole number", SetWhole, "19 45 E5 FF", "10"),
        Wrapper("menu value", GetChoice, "D9 43 E5 FF", "1C"), Wrapper("set menu value", SetChoice, "D9 44 E5 FF", "20"),
        Wrapper("decimal", GetDecimal, "99 43 E5 FF", "30"), Wrapper("set decimal", SetDecimal, "99 44 E5 FF", "34"),
        Cdecl("UserPreferences: decimal range (vtable +0x38)", DecimalRange, Lookup +
            " 09 47 E5 FF 8B 44 24 08 85 C0 74 1C 8B 48 08 85 C9 B0 01 74 0F 8B 44 24 18 8B 11 50 8B 44 24 18 50 FF 52 38"),
        Cdecl("UserPreferences: whole number range (vtable +0x14)", WholeRange, Lookup +
            " B9 46 E5 FF 8B 44 24 08 85 C0 74 1C 8B 48 08 85 C9 B0 01 74 0F 8B 44 24 18 8B 11 50 8B 44 24 18 50 FF 52 14"),
        Cdecl("UserPreferences: label, tooltip and string table ids", Description, Lookup +
            " A9 47 E5 FF 8B 44 24 08 85 C0 74 2A 8B 48 08 85 C9 B0 01 74 1D 8B 51 0C 8B 44 24 14 89 10 8B 51 10"),
        Cdecl("UserPreferences: menu string ids (vtable +0x24)", ChoiceIds, Lookup +
            " 29 46 E5 FF 8B 44 24 08 85 C0 74 17 8B 48 08 85 C9 B0 01 74 0A 8B 44 24 14 8B 11 50 FF 52 24"),
        Cdecl("UserPreferences::InqChoiceValues", ChoiceValues, "A1 C8 80 83 00 83 EC 0C 85 C0 75 21 68 00 81 00 00 E8 8F 69 1A 00"),
        Cdecl("UserPreferences::InqChoiceStrings", ChoiceStrings, "A1 C8 80 83 00 83 EC 0C 85 C0 75 21 68 00 81 00 00 E8 7F 68 1A 00"),
        Cdecl("CM_UI::SendNotice_UserPreferenceChanged", PreferenceChanged, "E8 CB 00 00 00 8B 10 68 1E D2 4D 00 8B C8 FF 52 10"),
        Cdecl("CM_UI::SendNotice_UserPreferenceChanged_Menu", MenuChanged, "E8 7B 00 00 00 8B 10 68 76 D2 4D 00 8B C8 FF 52 10"),
        Cdecl("operator delete", Free, "FF 25 34 32 79 00"),
        new NativeUiEntry("PStringBase<char> vector deleting destructor", FreeStrings, NativeUiCatalogue.Bytes(
            "51 F6 44 24 08 02 56 57 8B F9 74 64 8B 47 FC 53 8D 5F FC 8D 3C 87"), Source, "ThisCall")
    };

    private static readonly IReadOnlyList<NativeUiEntry> ButtonNatives = new[]
    {
        new NativeUiEntry("UIElement_Button::HandleButtonClick: property 0x12 sent as an input action", ButtonAction, NativeUiCatalogue.Bytes(
            "8B 44 24 08 BB 01 00 00 00 3B C3 74 4F 8B 0D F4 7F 83 00 85 C9 74 45 8B 11 FF 52 44 85 C0 74 3C 8B 35 3C E0 83 00 85 F6 74 32 " +
            "8D 4C 24 0C E8 86 EC FB FF 8B 44 24 08 89 44 24 0C 8D 44 24 0C C7 44 24 28 03 00 00 00 88 5C 24 2C 8B 16 50 8B CE FF 12"),
            Source, "ThisCall")
    };

    private readonly ILegACEyPluginHost _host;
    private readonly RetailPanelTakeover? _panel;
    private readonly Func<IReadOnlyDictionary<uint, string>> _strings;
    private readonly Action<string> _log;
    private OptionsWindow? _view;
    private Action? _hide;
    private bool _shown;
    private long _refreshedAt;
    private bool? _optionsValid, _broadcastsValid, _preferencesValid, _buttonsValid;
    // Whether a number preference is held as a decimal (else a whole number), as each first read finds it.
    private readonly Dictionary<string, bool> _decimal = new();

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
        _decimal.Clear();
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
            DerethWindow.TitleBarHeight, new DerethClientTheme(), OptionsWindow.Sizing,
            sharesPlaceWith: RetailPanelSlot.WindowPlace);
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

    bool IOptionsGame.CanPress(GameplayButton button) => button switch
    {
        GameplayButton.ConfigureKeyboard or GameplayButton.HelpFiles => ButtonsValid(),
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
            case GameplayButton.ConfigureKeyboard:
                SendAction(KeyboardAction);
                break;
            case GameplayButton.HelpFiles:
                SendAction(HelpAction);
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

    // The Config settings.

    double? IOptionsGame.Preference(PreferenceKind kind, string name)
    {
        if (Name(name) is not { } at) return null;
        switch (kind)
        {
            case PreferenceKind.Switch:
            {
                byte on = 0;
                return ((delegate* unmanaged[Cdecl]<IntPtr, byte*, byte>)GetSwitch)(at, &on) != 0 ? on != 0 ? 1 : 0 : null;
            }
            case PreferenceKind.Choice:
            {
                uint value = 0;
                return ((delegate* unmanaged[Cdecl]<IntPtr, uint*, byte>)GetChoice)(at, &value) != 0 ? value : null;
            }
            default:
            {
                // A slider's preference is a decimal or a whole number; retail's slider knows which, here the first read that answers does.
                if (!_decimal.TryGetValue(name, out var isDecimal) || isDecimal)
                {
                    float value = 0;
                    if (((delegate* unmanaged[Cdecl]<IntPtr, float*, byte>)GetDecimal)(at, &value) != 0)
                    {
                        _decimal[name] = true;
                        return value;
                    }
                }
                int whole = 0;
                if (((delegate* unmanaged[Cdecl]<IntPtr, int*, byte>)GetWhole)(at, &whole) == 0) return null;
                _decimal[name] = false;
                return whole;
            }
        }
    }

    (double Low, double High)? IOptionsGame.Range(string name)
    {
        // Which range call fits is learnt by the first read of the value.
        if (!_decimal.ContainsKey(name)) ((IOptionsGame)this).Preference(PreferenceKind.Number, name);
        if (Name(name) is not { } at || !_decimal.TryGetValue(name, out var isDecimal)) return null;
        if (isDecimal)
        {
            float low = 0, high = 0;
            return ((delegate* unmanaged[Cdecl]<IntPtr, float*, float*, byte>)DecimalRange)(at, &low, &high) != 0 ? (low, high) : null;
        }
        int lowWhole = 0, highWhole = 0;
        return ((delegate* unmanaged[Cdecl]<IntPtr, int*, int*, byte>)WholeRange)(at, &lowWhole, &highWhole) != 0 ? (lowWhole, highWhole) : null;
    }

    IReadOnlyList<MenuChoice> IOptionsGame.Choices(string name, bool listed)
    {
        if (Name(name) is not { } at) return Array.Empty<MenuChoice>();
        var values = Fill(ChoiceValues, at, out var valuesData, out var valueCount);
        var choices = new List<MenuChoice>();
        if (listed)
        {
            // Resolution: retail's own words for each choice ("1920x1080").
            var strings = Fill(ChoiceStrings, at, out var stringsData, out var stringCount);
            for (var i = 0; i < Math.Min(stringCount, valueCount); i++)
                choices.Add(new MenuChoice(Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(stringsData, i * 4)) ?? string.Empty, (uint)Marshal.ReadInt32(valuesData, i * 4)));
            if (strings) ((delegate* unmanaged[Thiscall]<IntPtr, uint, IntPtr>)FreeStrings)(stringsData, 3);
        }
        else
        {
            // The others: retail's string ids, with the values in order, or 0, 1, 2, ... when the preference has none.
            var ids = Fill(ChoiceIds, at, out var idsData, out var idCount);
            for (var i = 0; i < idCount; i++)
            {
                var id = (uint)Marshal.ReadInt32(idsData, i * 4);
                var value = valueCount > 0 ? i < valueCount ? (uint)Marshal.ReadInt32(valuesData, i * 4) : (uint?)null : (uint)i;
                if (value is { } v) choices.Add(new MenuChoice(_strings().TryGetValue(id, out var text) ? text : v.ToString(), v));
            }
            if (ids) ((delegate* unmanaged[Cdecl]<IntPtr, void>)Free)(idsData);
        }
        if (values) ((delegate* unmanaged[Cdecl]<IntPtr, void>)Free)(valuesData);
        return choices;
    }

    string? IOptionsGame.PreferenceTip(string name)
    {
        if (Name(name) is not { } at) return null;
        uint label = 0, tip = 0, table = 0;
        return ((delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, uint*, byte>)Description)(at, &label, &tip, &table) != 0
            && _strings().TryGetValue(tip, out var text) ? text : null;
    }

    void IOptionsGame.SetPreferences(IReadOnlyList<PreferenceChange> changes)
    {
        foreach (var change in changes)
        {
            if (Name(change.Name) is not { } at) continue;
            // As retail's option widgets apply: the set, then the change notice (a menu's also with its old and new value).
            var set = change.Kind switch
            {
                PreferenceKind.Switch => ((delegate* unmanaged[Cdecl]<IntPtr, int, byte>)SetSwitch)(at, change.Value != 0 ? 1 : 0),
                PreferenceKind.Choice => ((delegate* unmanaged[Cdecl]<IntPtr, uint, byte>)SetChoice)(at, (uint)change.Value),
                _ => _decimal.TryGetValue(change.Name, out var isDecimal) && !isDecimal
                    ? ((delegate* unmanaged[Cdecl]<IntPtr, int, byte>)SetWhole)(at, (int)Math.Round(change.Value))
                    : ((delegate* unmanaged[Cdecl]<IntPtr, float, byte>)SetDecimal)(at, (float)change.Value)
            };
            if (set == 0) continue;
            ((delegate* unmanaged[Cdecl]<IntPtr, byte>)PreferenceChanged)(at);
            if (change.Kind == PreferenceKind.Choice)
                ((delegate* unmanaged[Cdecl]<IntPtr, uint, uint, byte>)MenuChanged)(at, (uint)change.Old, (uint)change.Value);
        }
    }

    /// <summary>Fills a SmartArray (data, capacity | 0x80000000 when owned, count) through a menu call; true when the caller must free it.</summary>
    private static bool Fill(uint call, IntPtr name, out IntPtr data, out int count)
    {
        var array = stackalloc uint[3];
        array[0] = array[1] = array[2] = 0;
        ((delegate* unmanaged[Cdecl]<IntPtr, uint*, byte>)call)(name, array);
        data = new IntPtr((int)array[0]);
        count = data == IntPtr.Zero ? 0 : (int)array[2];
        return data != IntPtr.Zero && (array[1] & 0x80000000) != 0;
    }

    /// <summary>The address of retail's name for a preference, once the preference calls and every name have matched; null otherwise.</summary>
    private IntPtr? Name(string name)
    {
        _preferencesValid ??= NativeUi.Ready && NativeUiCatalogue.Validate(NativeUi.ReadMemory, _log, PreferenceNatives,
            "The options window's Config tab is off: the running client does not match retail's preference calls.") && NamesMatch();
        return _preferencesValid == true && PreferenceNames.TryGetValue(name, out var at) ? new IntPtr((int)at) : null;
    }

    private bool NamesMatch()
    {
        foreach (var pair in PreferenceNames)
        {
            var (name, at) = (pair.Key, pair.Value);
            var text = Marshal.ReadIntPtr(new IntPtr((int)at));
            var found = text == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(text);
            if (found == name) continue;
            _log($"The options window's Config tab is off: retail's preference name at 0x{at:X8} is \"{found}\", not \"{name}\".");
            return false;
        }
        return true;
    }

    /// <summary>Sends an input action as a click on a retail button with property 0x12 does.</summary>
    private void SendAction(uint action)
    {
        if (!ButtonsValid()) return;
        var gate = Marshal.ReadIntPtr(new IntPtr((int)InputGate));
        if (gate == IntPtr.Zero || ((delegate* unmanaged[Thiscall]<IntPtr, int>)Marshal.ReadIntPtr(Marshal.ReadIntPtr(gate), 0x44))(gate) == 0) return;
        var manager = Marshal.ReadIntPtr(new IntPtr(NativeUi.Manager));
        if (manager == IntPtr.Zero) return;
        // The event retail builds (InputEvent's constructor, 0x00430EE0): zeroes, -1 at +0x08, 0xC0000000 at +0x14; then the action, the
        // type 3 at +0x1C and "pressed" at +0x20.
        var input = stackalloc byte[0x48];
        new Span<byte>(input, 0x48).Clear();
        *(uint*)input = action;
        *(int*)(input + 0x08) = -1;
        *(uint*)(input + 0x14) = 0xC0000000;
        *(int*)(input + 0x1C) = 3;
        input[0x20] = 1;
        ((delegate* unmanaged[Thiscall]<IntPtr, byte*, byte>)Marshal.ReadIntPtr(Marshal.ReadIntPtr(manager)))(manager, input);
    }

    private bool ButtonsValid() => _buttonsValid ??= NativeUi.Ready && NativeUiCatalogue.Validate(NativeUi.ReadMemory, _log, ButtonNatives,
        "The options window's Configure Keyboard and In-Game Help Files are off: the running client does not match retail's buttons.");

    private static NativeUiEntry Cdecl(string name, uint address, string bytes) => new(name, address, NativeUiCatalogue.Bytes(bytes), Source, "Cdecl");

    private static NativeUiEntry Wrapper(string what, uint address, string lookupCall, string slot) =>
        Cdecl($"UserPreferences: {what} (vtable +0x{slot})", address, Lookup + " " + lookupCall +
            " 8B 44 24 08 85 C0 74 07 8B 48 08 B0 01 EB 04 32 C0 33 C9 84 C0 74 0A 8B 44 24 14 8B 11 50 FF 52 " + slot);

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

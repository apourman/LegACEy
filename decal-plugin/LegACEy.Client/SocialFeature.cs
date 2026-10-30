using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The Dereth social window in place of retail's social panel (Allegiance, Fellowship, Friends, Squelch), held as the character panel is:
/// retail's key and toolbar button open and close our window, and its close box closes retail's panel. The allegiance, fellowship, friends
/// and squelches come from the server's messages (<see cref="SocialState"/>); the buttons send retail's own game actions, and the switches
/// are retail's PlayerModule options, set through retail's setters so retail and the server stay in step. Call everything on the game thread.
/// </summary>
internal sealed unsafe class SocialFeature : ISocialGame, IDisposable
{
    private const long RefreshMs = 1000;
    // PlayerModule's option words: CharacterOptions1 at +0x8C, CharacterOptions2 at +0x90 (the getters and setters below read them).
    private const int Options1 = 0x8C, Options2 = 0x90;
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb (near build, +0x1000)";

    // Each option's word, bit and retail setter; the setter sets the bit and sends the change (SetSingleCharacterOption) itself.
    private static readonly Dictionary<SocialOption, (int Word, uint Bit, uint Setter)> OptionBits = new()
    {
        [SocialOption.IgnoreAllegianceRequests] = (Options1, 0x00000004, 0x005D3AF0),
        [SocialOption.IgnoreFellowshipRequests] = (Options1, 0x00000008, 0x005D3B40),
        [SocialOption.ShareFellowshipExperience] = (Options1, 0x00040000, 0x005D3F40),
        [SocialOption.ShareFellowshipLoot] = (Options1, 0x00100000, 0x005D3FE0),
        [SocialOption.AutoAcceptFellowshipRequests] = (Options1, 0x20000000, 0x005D4030),
        [SocialOption.AppearOffline] = (Options2, 0x00001000, 0x005D4790)
    };

    /// <summary>The option setters and the getters that pin the words' offsets. Checked once; a mismatch leaves the switches dim.</summary>
    internal static readonly IReadOnlyList<NativeUiEntry> Options = new[]
    {
        Entry("PlayerModule::IgnoreAllegianceRequests (CharacterOptions1 at +0x8C, bit 0x4)", 0x005D3AE0, "8B 81 8C 00 00 00 C1 E8 02 83 E0 01 C3"),
        Entry("PlayerModule::SetIgnoreAllegianceRequests", 0x005D3AF0, "8B 91 8C 00 00 00 8A 44 24 04 C1 EA 02 80 E2 01 3A D0 74 25"),
        Entry("PlayerModule::SetIgnoreFellowshipRequests (bit 0x8)", 0x005D3B40, "8B 91 8C 00 00 00 8A 44 24 04 C1 EA 03 80 E2 01 3A D0 74 25"),
        Entry("PlayerModule::SetFellowshipShareXP (bit 0x40000)", 0x005D3F40, "8B 91 8C 00 00 00 8A 44 24 04 C1 EA 12 80 E2 01 3A D0 74 29"),
        Entry("PlayerModule::SetFellowshipShareLoot (bit 0x100000)", 0x005D3FE0, "8B 91 8C 00 00 00 8A 44 24 04 C1 EA 14 80 E2 01 3A D0 74 29"),
        Entry("PlayerModule::SetFellowshipAutoAcceptRequests (bit 0x20000000)", 0x005D4030, "8B 91 8C 00 00 00 8A 44 24 04 C1 EA 1D 80 E2 01 3A D0 74 29"),
        Entry("PlayerModule::SetAppearOffline (CharacterOptions2 at +0x90, bit 0x1000)", 0x005D4790, "8B 91 90 00 00 00 8A 44 24 04 C1 EA 0C 80 E2 01 3A D0 74 29"),
        Entry("CPlayerSystem getter (0x0087119C)", RetailHudCatalogue.GetPlayerSystem, "A1 9C 11 87 00 C3")
    };

    private readonly ILegACEyPluginHost _host;
    private readonly RetailPanelTakeover? _panel;
    private readonly Func<CharacterTables> _tables;
    private readonly Func<GameChannelTransport?> _transport;
    private readonly Action<string, uint> _tell;
    private readonly Action<string> _log;
    private SocialWindow? _view;
    private Action? _hide;
    private bool _shown;
    private long _refreshedAt;
    private bool? _optionsValid;

    public SocialFeature(ILegACEyPluginHost host, IRetailPanelPort? port, Func<CharacterTables> tables, Func<GameChannelTransport?> transport,
        Action<string, uint> tell, Action<string> log)
    {
        _host = host;
        _tables = tables;
        _transport = transport;
        _tell = tell;
        _log = log;
        if (port != null) _panel = new RetailPanelTakeover(port, OpenChanged, log);
        State.Changed += () => { if (_shown) _view?.Refresh(); };
    }

    public SocialState State { get; } = new();

    /// <summary>Once a frame in game. With <paramref name="enabled"/> false retail's panel is given back, and our window closes.</summary>
    public void Tick(bool enabled, bool canShow)
    {
        _panel?.Tick(enabled, canShow);
        // The selection and retail's options change with no message the state reads, so an open window reads them again about once a second.
        var now = Environment.TickCount64;
        if (_shown && _view != null && now - _refreshedAt >= RefreshMs)
        {
            _refreshedAt = now;
            _view.Refresh();
        }
    }

    /// <summary>Logoff, unload or the windows failing: gives retail's panel back and drops the window. The state is the caller's to clear.</summary>
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
            _log($"The social window could not open: {exception.Message}");
            _panel?.Close();
        }
    }

    private void Hide()
    {
        _shown = false;
        _hide?.Invoke();
        // The server sends the fellows' vitals only while the panel shows.
        if (State.Fellowship != null) Send(SocialAction.FellowshipPanel(open: false));
    }

    private void Open()
    {
        // A window the client released is neither open nor hidden: the view is stale, so a new one is built.
        var fresh = _view == null;
        _view ??= new SocialWindow(State, this, _tables);
        var view = _view;
        var place = _panel?.HeldPlace ?? new Point(200, 150);
        var definition = new WindowDefinition(SocialWindow.WindowId, "Social", SocialWindow.Width, SocialWindow.Height,
            DerethWindow.TitleBarHeight, new DerethClientTheme(), SocialWindow.Sizing,
            sharesPlaceWith: RetailPanelSlot.WindowPlace, dragsFromContent: true);
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
            _log($"The social window failed: {error.Message}");
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
        // A new window asked for its first tab's latest when it was built; a reopened one asks again.
        if (!fresh) view.ShowTab(view.Tab);
        else view.Refresh();
    }

    // The game, as the window reads it.

    uint ISocialGame.Selection
    {
        get
        {
            try { return (uint)Bridge.Fact(BridgeFact.CurrentSelection); }
            catch (COMException) { return 0; }
        }
    }

    void ISocialGame.Select(uint id)
    {
        try { Bridge.SelectItem(id); }
        catch (COMException exception) { _log($"Could not select 0x{id:X8}: {exception.Message}"); }
    }

    bool? ISocialGame.Option(SocialOption option) =>
        PlayerModule() is { } module && OptionBits.TryGetValue(option, out var bits)
            ? ((uint)Marshal.ReadInt32(module, bits.Word) & bits.Bit) != 0
            : null;

    void ISocialGame.SetOption(SocialOption option, bool on)
    {
        if (PlayerModule() is not { } module || !OptionBits.TryGetValue(option, out var bits)) return;
        ((delegate* unmanaged[Thiscall]<IntPtr, byte, void>)bits.Setter)(module, on ? (byte)1 : (byte)0);
    }

    bool ISocialGame.Send(SocialAction action) => Send(action);

    void ISocialGame.Tell(string name, uint id) => _tell(name, id);

    private bool Send(SocialAction action)
    {
        var sent = _transport()?.SendAction(action.Opcode, action.Payload) == true;
        if (!sent) _log($"Could not send social action 0x{action.Opcode:X4}: the game connection is not ready.");
        return sent;
    }

    /// <summary>PlayerModule, at CPlayerSystem+0x34, once the option natives have matched; null outside the game or when they did not.</summary>
    private IntPtr? PlayerModule()
    {
        _optionsValid ??= NativeUiCatalogue.Validate(NativeUi.ReadMemory, _log, Options,
            "The social window's switches are off: the running client does not match its option setters.");
        if (_optionsValid != true) return null;
        var system = Marshal.ReadIntPtr(new IntPtr(RetailToolbarCatalogue.PlayerSystem));
        return system == IntPtr.Zero ? null : system + RetailToolbarCatalogue.PlayerModuleOffset;
    }

    private static NativeUiEntry Entry(string name, uint address, string bytes) =>
        new(name, address, NativeUiCatalogue.Bytes(bytes), Source, "ThisCall");
}

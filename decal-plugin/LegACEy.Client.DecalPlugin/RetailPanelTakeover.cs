using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace LegACEy.Client.DecalPlugin;

/// <summary>The retail calls the panel takeover makes. ClientUiRuntime implements it over the native UI; tests fake it.</summary>
internal interface IRetailPanelPort
{
    /// <summary>The panel element, or zero when the client has none (between panels, or not logged in).</summary>
    IntPtr Identity { get; }
    /// <summary>Retail's open state: the element and every ancestor are visible.</summary>
    bool IsOpen { get; }
    /// <summary>The element's position on screen.</summary>
    Point Location { get; }
    /// <summary>Whether retail saves the element's position to its layout.</summary>
    bool SaveLocation { get; }
    void SetSaveLocation(bool save);
    /// <summary>Moves the element. Retail saves the position only while save-location is on.</summary>
    void MoveTo(Point location);
    /// <summary>Opens the panel through retail's own panel switch, or shows the element when that route is not available.</summary>
    void OpenPanel();
    /// <summary>Closes the panel through retail's own panel switch, or hides the element when that route is not available.</summary>
    void ClosePanel();
}

/// <summary>
/// Parks retail's inventory panel off-screen while it is open and the takeover is on, and tells a plugin window when that panel
/// opens and closes. Retail keeps its open state on the element it would hide, so the element is parked rather than hidden each frame;
/// a closed panel is given back at once, because its frame also holds retail's other panels.
/// The panel's place and save-location are given back on every exit:
/// switch-off, logoff, a fault, unload and plugin turn-off. While parked, save-location is off, so the parked position never reaches
/// the layout. Call <see cref="Tick"/> on the game thread once a frame.
/// </summary>
internal sealed class RetailPanelTakeover : IDisposable
{
    /// <summary>Where the parked element sits: far off-screen. Retail's open state is kept there.</summary>
    public static readonly Point Parked = new(-10000, -10000);
    /// <summary>Frames a missing or closed panel is tolerated before our window closes: a portal relayout takes a few.</summary>
    public const int TransitionFrames = 10;

    private readonly IRetailPanelPort _port;
    private readonly Action<bool> _openChanged;
    private readonly Action<string> _log;
    // The panel we hold, and the layout's place and save-location for it: kept until the restore succeeds.
    private IntPtr _identity;
    private bool _parked;
    private Point _original;
    private bool _originalSave;
    // Where our last move left the panel. Null before the first move of this hold; Parked after a good one.
    private Point? _parkedAt;
    private bool _reportedOpen;
    private int _closedFrames;
    private bool _restoreFailed;
    private bool _failed;
    private bool _disposed;

    public RetailPanelTakeover(IRetailPanelPort port, Action<bool> openChanged, Action<string> log)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _openChanged = openChanged ?? throw new ArgumentNullException(nameof(openChanged));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Called once a frame in game. With the switch off, or after a fault, a held panel is given back and nothing else happens; a
    /// restore that is still failing is retried here each frame. Parks the panel when it appears, parks it again when retail laid it
    /// out elsewhere (a resize or a portal transition), and reports each change of retail's open state.
    /// <paramref name="canShow"/> is false while our window cannot open yet (portal space at login): an open panel is parked
    /// and reported once it can.
    /// </summary>
    public void Tick(bool enabled, bool canShow = true)
    {
        if (_disposed) return;
        if (!enabled || _failed)
        {
            if (_parked) Release();
            return;
        }
        try
        {
            var identity = _port.Identity;
            if (identity == IntPtr.Zero)
            {
                // Retail is between panels: keep our window through a short gap.
                Closing();
                return;
            }
            if (!_port.IsOpen)
            {
                // The panel frame is shared with retail's other panels (character, spells, options), so it is given back as soon as
                // the inventory closes, and the next panel retail opens shows where it should.
                Restore();
                Closing();
                return;
            }
            if (!_parked || identity != _identity) Park(identity);
            else if (_port.Location != ParkedAt)
            {
                // Retail laid the panel out again: its new place is the one to give back, and the panel is parked again.
                _original = _port.Location;
                Repark();
            }
            _closedFrames = 0;
            if (canShow) Report(true);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /// <summary>The close box: closes retail's panel through its own path, while the takeover holds it.</summary>
    public void Close()
    {
        if (_disposed || !_parked) return;
        try
        {
            if (_port.IsOpen) _port.ClosePanel();
            Report(_port.IsOpen);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /// <summary>The menu's open: retail's panel opens through its own switch, while the takeover holds it.</summary>
    public void Open()
    {
        if (_disposed || !_parked) return;
        try
        {
            if (!_port.IsOpen) _port.OpenPanel();
            Report(_port.IsOpen);
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    /// <summary>Logoff: gives the panel back and forgets the session, so the next login takes the panel over afresh.</summary>
    public void EndSession()
    {
        Release();
        _failed = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
    }

    private Point ParkedAt => _parkedAt ?? Parked;

    /// <summary>True while the takeover holds the panel: parked, and not failed. Open and Close reach retail only then.</summary>
    public bool Holds => _parked && !_failed && !_disposed;

    private void Park(IntPtr identity)
    {
        _identity = identity;
        _original = _port.Location;
        _originalSave = _port.SaveLocation;
        _parked = true;
        _restoreFailed = false;
        Repark();
        _log($"Parked the retail inventory panel off-screen; its place is {_original}.");
    }

    /// <summary>Moves the panel to the parked place. A panel that does not land there (clamped by the client) fails the takeover.</summary>
    private void Repark()
    {
        _parkedAt = null;
        _port.SetSaveLocation(false);
        _port.MoveTo(Parked);
        _parkedAt = _port.Location;
        if (_parkedAt != Parked) throw new InvalidOperationException($"Retail placed the parked panel at {_parkedAt}, not {Parked}.");
    }

    /// <summary>Counts a frame without the panel open, and closes our window once the gap is longer than a transition.</summary>
    private void Closing()
    {
        if (++_closedFrames >= TransitionFrames) Report(false);
    }

    private void Report(bool open)
    {
        if (open == _reportedOpen) return;
        _reportedOpen = open;
        _openChanged(open);
    }

    /// <summary>
    /// Gives the panel back and then tells the window it is no longer held. Until the restore succeeds the panel stays held, our
    /// window stays up, and the next frame tries again; the failure is logged once.
    /// </summary>
    private void Release()
    {
        try
        {
            Restore();
            Report(false);
        }
        catch (Exception exception)
        {
            if (!_restoreFailed)
            {
                _restoreFailed = true;
                _log($"Could not give the retail inventory panel back; retrying each frame: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Restores the layout's place and save-location. The place is moved back only while the panel is where our last move left it:
    /// a panel retail has placed elsewhere keeps its place. The save-location bit is restored whatever happens to the move. The hold
    /// is dropped only after both succeed.
    /// </summary>
    private void Restore()
    {
        if (!_parked) return;
        if (_port.Identity != _identity)
        {
            // The panel is gone: there is nothing left to put back.
            _parked = false;
            _identity = IntPtr.Zero;
            return;
        }
        try
        {
            _port.SetSaveLocation(false);
            if (_port.Location == ParkedAt) _port.MoveTo(_original);
        }
        finally
        {
            _port.SetSaveLocation(_originalSave);
        }
        _parked = false;
        _identity = IntPtr.Zero;
        _parkedAt = null;
        _log($"Gave the retail inventory panel back at {_original}.");
    }

    private void Fail(Exception exception)
    {
        _failed = true;
        _log($"Retail inventory takeover turned off: {exception.Message}");
        Release();
    }
}

/// <summary>
/// The native addresses the panel takeover calls. Its own list, so a mismatch turns only this takeover off: the indicators bar and
/// the Vault drag keep their gates. The shared entries are the ones NativeUiCatalogue already checks; the rest are new.
/// </summary>
internal static class RetailPanelCatalogue
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe";
    /// <summary>gmPanelUI::RecvNotice_SetPanelVisibility, a ThisCall on gmPanelUI+0x5F8 with (panel id, visible).</summary>
    internal const uint RecvNoticeSetPanelVisibility = 0x004BD380;
    /// <summary>gmPanelUI's primary vtable. A panel element whose first dword is this is the gmPanelUI itself.</summary>
    internal const uint GmPanelUIVtable = 0x007B5070;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        Shared("UIElementManager::GetElement"),
        Shared("UIElement::IsVisible"),
        Shared("UIElement::SetVisible"),
        Shared("UIElement::GetCurrentPosition"),
        Shared("UIElement::SetSaveLocation"),
        Shared("UIElement::MoveTo"),
        new NativeUiEntry("gmPanelUI::RecvNotice_SetPanelVisibility", RecvNoticeSetPanelVisibility, NativeUiCatalogue.Bytes(
            "51 8B 54 24 08 53 55 33 DB 3B D3 56 8B F1 0F 84 69 01 00 00"), Source, "ThisCall (gmPanelUI+0x5F8; uint panelId, uint visible), ret 8"),
        // The receiver: gmPanelUI's constructor stores the notice sub-object's vtable (0x007B4DC8) at +0x5F8, so the receiver of
        // RecvNotice is gmPanelUI+0x5F8. There is no direct call that computes it; the notice is called through the vtable below.
        new NativeUiEntry("gmPanelUI constructor: notice sub-object at +0x5F8 (mov [esi+0x5F8], 0x007B4DC8)", 0x004BD536, NativeUiCatalogue.Bytes(
            "C7 86 F8 05 00 00 C8 4D 7B 00"), Source, "instruction reference (receiver offset +0x5F8)"),
        // RecvNotice_SetPanelVisibility is slot +0x260 of the notice vtable 0x007B4DC8.
        new NativeUiEntry("notice vtable 0x007B4DC8 slot +0x260 holds RecvNotice_SetPanelVisibility", 0x007B4DC8 + 0x260, NativeUiCatalogue.Bytes("80 D3 4B 00"),
            Source, "data reference (vtable slot)"),
        // The one call that dispatches it: call [edx+0x260] with the notice object in ecx (mov ecx, esi).
        new NativeUiEntry("call [edx+0x260] dispatching RecvNotice with the notice object", 0x0047A4E1, NativeUiCatalogue.Bytes("FF 92 60 02 00 00"),
            Source, "virtual slot call"),
        // gmPanelUI's primary vtable at 0x007B5070: slot +0x0C is ListenToElementMessage, which identifies the class.
        new NativeUiEntry("gmPanelUI vtable slot +0x0C reference (vtable at 0x007B5070 is gmPanelUI)", GmPanelUIVtable + 0x0C, NativeUiCatalogue.Bytes("00 D3 4B 00"),
            Source, "data reference: ListenToElementMessage 0x004BD300 in the class's vtable"),
        new NativeUiEntry("UIElement::IsAncestorOfMe reference (GetParent through vtable +0xA0)", 0x0045FBB0, NativeUiCatalogue.Bytes("8B 01 FF 90 A0 00 00 00"),
            Source, "virtual slot reference")
    };

    private static NativeUiEntry Shared(string name) => NativeUiCatalogue.Entries.Single(entry => entry.Name == name);

}

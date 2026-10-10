using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace LegACEy.Client.DecalPlugin;

/// <summary>The retail calls the panel takeover makes. ClientUiRuntime implements it over the native UI; tests fake it.</summary>
internal interface IRetailPanelPort
{
    /// <summary>The panel element, or zero when the client has none (not logged in).</summary>
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
    /// <summary>Closes the panel through retail's own panel switch, or hides the element when that route is not available.</summary>
    void Close();
}

/// <summary>
/// Keeps retail's inventory panel open but parked off-screen while the takeover is on, and tells a plugin window when that panel
/// opens and closes. Retail keeps its open state on the element it would hide, so the element is parked, not hidden each frame
/// (research R1, Q1; the user has not yet decided this, decision D1). Its position and save-location are given back on every exit:
/// switch-off, logout, a fault and shutdown. While parked, save-location is off, so the parked position never reaches the layout.
/// Call <see cref="Tick"/> on the game thread once a frame.
/// </summary>
internal sealed class RetailPanelTakeover : IDisposable
{
    /// <summary>Where the parked element sits: far off-screen. Retail's open state is kept there.</summary>
    public static readonly Point Parked = new(-10000, -10000);

    private readonly IRetailPanelPort _port;
    private readonly Action<bool> _openChanged;
    private readonly Action<string> _log;
    private IntPtr _identity;
    private bool _parked;
    private Point _original;
    private bool _originalSave;
    private bool _reportedOpen;
    private bool _failed;
    private bool _disposed;

    public RetailPanelTakeover(IRetailPanelPort port, Action<bool> openChanged, Action<string> log)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _openChanged = openChanged ?? throw new ArgumentNullException(nameof(openChanged));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Whether the takeover holds the panel right now.</summary>
    public bool Holding => _parked;

    /// <summary>
    /// Called once a frame in game. While the switch is off, a held panel is given back and nothing else happens: the panel is not
    /// touched at all. Parks the panel when it appears, parks it again when retail lays it out on-screen (a resize or a portal
    /// transition), and reports each change of retail's open state.
    /// </summary>
    public void Tick(bool enabled)
    {
        if (_disposed) return;
        if (!enabled)
        {
            if (_parked) Release();
            return;
        }
        if (_failed) return;
        try
        {
            var identity = _port.Identity;
            if (identity == IntPtr.Zero)
            {
                // No panel (logged off): nothing to give back, and the window must not stay open for it.
                _parked = false;
                _identity = IntPtr.Zero;
                Report(false);
                return;
            }
            if (!_parked || identity != _identity)
            {
                // A panel that is not the one held (a new session) cannot be given back; park the new one.
                _parked = false;
                Park(identity);
            }
            else if (_port.Location != Parked)
            {
                _port.SetSaveLocation(false);
                _port.MoveTo(Parked);
            }
            Report(_port.IsOpen);
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
            if (_port.IsOpen) _port.Close();
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

    private void Park(IntPtr identity)
    {
        _identity = identity;
        _original = _port.Location;
        _originalSave = _port.SaveLocation;
        _parked = true;
        _reportedOpen = false;
        _port.SetSaveLocation(false);
        _port.MoveTo(Parked);
    }

    private void Report(bool open)
    {
        if (open == _reportedOpen) return;
        _reportedOpen = open;
        _openChanged(open);
    }

    /// <summary>Gives the panel back and tells the window it is no longer held. A restore that fails is logged, not thrown.</summary>
    private void Release()
    {
        try { Restore(); }
        catch (Exception exception) { _log($"Could not give the retail inventory panel back to its place: {exception.Message}"); }
        Report(false);
    }

    private void Restore()
    {
        if (!_parked) return;
        _parked = false;
        var identity = _identity;
        _identity = IntPtr.Zero;
        // The element is gone (logoff): there is nothing left to put back.
        if (identity == IntPtr.Zero || _port.Identity != identity) return;
        _port.SetSaveLocation(false);
        _port.MoveTo(_original);
        _port.SetSaveLocation(_originalSave);
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
/// the Vault drag keep their own gates. The shared entries are the ones NativeUiCatalogue already checks; the rest are new.
/// </summary>
internal static class RetailPanelCatalogue
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe (research R1, Q1)";

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        Shared("UIElementManager::GetElement"),
        Shared("UIElement::IsVisible"),
        Shared("UIElement::SetVisible"),
        Shared("UIElement::GetCurrentPosition"),
        Shared("UIElement::SetSaveLocation"),
        Shared("UIElement::MoveTo"),
        new NativeUiEntry("gmPanelUI::RecvNotice_SetPanelVisibility", 0x004BD380, Bytes(
            "51 8B 54 24 08 53 55 33 DB 3B D3 56 8B F1 0F 84 69 01 00 00"), Source, "ThisCall (gmPanelUI+0x5F8; uint panelId, uint visible), ret 8"),
        new NativeUiEntry("gmPanelUI vtable slot +0x0C reference (vtable at 0x007B5070 is gmPanelUI)", 0x007B5070 + 0x0C, Bytes("00 D3 4B 00"),
            Source, "data reference: ListenToElementMessage 0x004BD300 in the class's vtable"),
        new NativeUiEntry("UIElement::IsAncestorOfMe reference (GetParent through vtable +0xA0)", 0x0045FBB0, Bytes("8B 01 FF 90 A0 00 00 00"),
            Source, "virtual slot reference")
    };

    private static NativeUiEntry Shared(string name) => NativeUiCatalogue.Entries.Single(entry => entry.Name == name);

    private static byte[] Bytes(string bytes) => bytes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(value => Convert.ToByte(value, 16)).ToArray();
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Holds retail windows hidden while LegACEy windows draw in their place (the chat windows; the vitals windows and the radar), each root
/// through its own <see cref="RetailTakeoverLifecycle"/>, by the id it is placed with. Retail's windows keep running out of sight, so Decal
/// and its plugins see what they always saw. The LegACEy windows are windows of their own, so the roots' surfaces are stand-ins that show
/// nothing.
/// </summary>
internal sealed class RetailHeldWindows : IDisposable
{
    private readonly List<RetailTakeoverLifecycle> _roots;

    public RetailHeldWindows(IEnumerable<IRetailTakeoverPort> roots)
    {
        _roots = roots.Select(root => new RetailTakeoverLifecycle(root, HeldSurface.Instance)).ToList();
    }

    /// <summary>Holds retail's windows placed with these ids.</summary>
    public RetailHeldWindows(IEnumerable<uint> placedIds) : this(placedIds.Select(id => (IRetailTakeoverPort)new RootPort(id))) { }

    /// <summary>Whether any retail window is held hidden.</summary>
    public bool Holds => _roots.Any(root => root.Holds);

    /// <summary>Hides any held retail window that shows, and gives a held one back if it is gone. Called every frame.</summary>
    public void Tick(Size viewport)
    {
        foreach (var root in _roots) root.Tick(false, viewport);
    }

    /// <summary>
    /// Gives every held retail window back. A window that cannot be restored is kept, and the others are still restored; the failures are
    /// thrown together so the caller can retry.
    /// </summary>
    public void Release()
    {
        List<Exception>? failures = null;
        foreach (var root in _roots)
        {
            try { root.Dispose(); }
            catch (Exception exception) { (failures ??= new List<Exception>()).Add(exception); }
        }
        if (failures != null) throw new AggregateException("Could not give retail's windows back.", failures);
    }

    public void Dispose() => Release();

    /// <summary>One held retail window's port: its element by placed id, hidden and shown. It is never moved or saved by the takeover.</summary>
    internal sealed class RootPort : IRetailTakeoverPort
    {
        private readonly uint _rootId;

        public RootPort(uint rootId) => _rootId = rootId;

        private IntPtr Element => NativeUi.GetElement(_rootId);

        public bool Exists => Element != IntPtr.Zero;
        public IntPtr ElementIdentity => Element;
        public bool IsVisible { get { var element = Element; return element != IntPtr.Zero && NativeUi.IsVisible(element); } }
        public Rectangle GetBounds()
        {
            var element = Element;
            return element == IntPtr.Zero ? Rectangle.Empty : NativeUi.GetBounds(element);
        }
        public void SetVisible(bool visible)
        {
            var element = Element;
            if (element != IntPtr.Zero) NativeUi.SetVisible(element, visible);
        }
        public void SetSaveLocation(bool save) { }
        public void MoveTo(Point location) { }
        public bool IsUiLocked => NativeUi.IsUiLocked;
    }

    /// <summary>The stand-in surface of a held root: it has no place and draws nothing.</summary>
    private sealed class HeldSurface : IRetailTakeoverSurface
    {
        public static readonly HeldSurface Instance = new();
        public Point Location => Point.Empty;
        public Size Size => Size.Empty;
        public void SetLocation(Point location) { }
        public bool Visible { get; set; }
    }
}

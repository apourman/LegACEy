using System;
using System.Collections.Generic;
using System.Drawing;
using Avalonia.Controls;
using Avalonia.Layout;

namespace LegACEy.Client.Demo;

public sealed class RetailRootDescriptor
{
    public RetailRootDescriptor(string name, uint id) { Name = name; Id = id; }
    public string Name { get; }
    public uint Id { get; }
}

/// <summary>Developer panel for observing and exercising the known retail root elements.</summary>
public sealed class ElementInspectorControl : UserControl, IDisposable
{
    private readonly IReadOnlyList<RetailRootDescriptor> _roots;
    private readonly Func<uint, bool> _isVisible;
    private readonly Func<uint, Rectangle> _getBounds;
    private readonly Func<uint, IDisposable> _hide;
    private readonly Action<uint, Point> _move;
    private readonly Func<uint, IDisposable> _takeOver;
    private readonly StackPanel _rows = new() { Spacing = 6, Margin = new Avalonia.Thickness(10) };
    private readonly List<TakeoverEntry> _takeovers = new();
    private bool _disposed;

    public ElementInspectorControl(IReadOnlyList<RetailRootDescriptor> roots, Func<uint, bool> isVisible,
        Func<uint, Rectangle> getBounds, Func<uint, IDisposable> hide, Action<uint, Point> move, Func<uint, IDisposable> takeOver)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _isVisible = isVisible ?? throw new ArgumentNullException(nameof(isVisible));
        _getBounds = getBounds ?? throw new ArgumentNullException(nameof(getBounds));
        _hide = hide ?? throw new ArgumentNullException(nameof(hide));
        _move = move ?? throw new ArgumentNullException(nameof(move));
        _takeOver = takeOver ?? throw new ArgumentNullException(nameof(takeOver));
        var root = new DockPanel();
        var refresh = new Button { Content = "Refresh root observations", Margin = new Avalonia.Thickness(10) };
        refresh.Click += (_, _) => RefreshRows();
        DockPanel.SetDock(refresh, Dock.Top);
        root.Children.Add(refresh);
        root.Children.Add(new ScrollViewer { Content = _rows });
        Content = root;
        RefreshRows();
    }

    private void RefreshRows()
    {
        _rows.Children.Clear();
        foreach (var item in _roots)
        {
            var bounds = _getBounds(item.Id);
            var row = new StackPanel { Spacing = 3 };
            row.Children.Add(new TextBlock { Text = $"{item.Name} · visible: {_isVisible(item.Id)} · bounds: {bounds.X},{bounds.Y} {bounds.Width}×{bounds.Height}" });
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
            var hide = new Button { Content = "Hide" };
            hide.Click += (_, _) => { Hide(item.Id); RefreshRows(); };
            var restore = new Button { Content = "Restore" };
            restore.Click += (_, _) => { RestoreTakeover(item.Id); RefreshRows(); };
            var move = new Button { Content = "Move +20,+20" };
            move.Click += (_, _) => { _move(item.Id, new Point(bounds.X + 20, bounds.Y + 20)); RefreshRows(); };
            var takeover = new Button { Content = "Replace with placeholder" };
            takeover.Click += (_, _) => TakeOver(item.Id);
            buttons.Children.Add(hide); buttons.Children.Add(restore); buttons.Children.Add(move); buttons.Children.Add(takeover);
            row.Children.Add(buttons);
            _rows.Children.Add(row);
        }
    }

    public void TakeOver(uint rootElementId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ElementInspectorControl));
        if (_takeovers.Exists(entry => entry.RootElementId == rootElementId && entry.IsPlaceholder)) return;
        RestoreTakeover(rootElementId);
        _takeovers.Add(new TakeoverEntry(rootElementId, _takeOver(rootElementId), true));
    }

    public void Hide(uint rootElementId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ElementInspectorControl));
        RestoreTakeover(rootElementId);
        _takeovers.Add(new TakeoverEntry(rootElementId, _hide(rootElementId), false));
    }

    public void RestoreTakeover(uint rootElementId)
    {
        for (var i = _takeovers.Count - 1; i >= 0; i--)
        {
            if (_takeovers[i].RootElementId != rootElementId) continue;
            _takeovers[i].Registration.Dispose();
            _takeovers.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        if (_disposed && _takeovers.Count == 0) return;
        _disposed = true;
        Exception? first = null;
        for (var i = _takeovers.Count - 1; i >= 0; i--)
            try { _takeovers[i].Registration.Dispose(); _takeovers.RemoveAt(i); } catch (Exception error) { first ??= error; }
        if (first != null) throw new InvalidOperationException("The inspector could not restore one or more retail elements.", first);
    }

    private sealed class TakeoverEntry
    {
        public TakeoverEntry(uint rootElementId, IDisposable registration, bool isPlaceholder)
        { RootElementId = rootElementId; Registration = registration; IsPlaceholder = isPlaceholder; }
        public bool IsPlaceholder { get; }
        public uint RootElementId { get; }
        public IDisposable Registration { get; }
    }
}

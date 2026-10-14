using System;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The toolbar's readings as one comparable state: the stance, the open window panels (a bit per panel number), the selection with the
/// kind and meter the plaque shows, and the shortcut list. <see cref="Update"/> keeps the last state, so a reading that matches it reports
/// nothing to redraw.
/// </summary>
internal sealed class RetailToolbarSnapshot
{
    private readonly RetailShortcut[] _shortcuts = new RetailShortcut[RetailToolbarCatalogue.ShortcutSlots];
    private int _count;
    private RetailStance _stance;
    private int _openPanels;
    private RetailSelection? _selection;
    private SelectionKind _kind;
    private float? _meter;
    private bool _recorded;

    /// <summary>The readings of a selection that is the object's own kind and meter: see the full overload.</summary>
    public bool Update(RetailStance stance, int openPanels, RetailSelection? selection, ReadOnlySpan<RetailShortcut> shortcuts) =>
        Update(stance, openPanels, selection, selection?.Kind ?? SelectionKind.Other, selection?.Meter, shortcuts);

    /// <summary>
    /// Records the readings. True when this is the first reading, or when any input differs from the last one recorded: the stance, a
    /// panel's open state, the selection (its name, stack count and the kind and meter the plaque shows) or the shortcut list.
    /// </summary>
    public bool Update(RetailStance stance, int openPanels, RetailSelection? selection, SelectionKind kind, float? meter, ReadOnlySpan<RetailShortcut> shortcuts)
    {
        if (_recorded && stance == _stance && openPanels == _openPanels && selection == _selection && kind == _kind && meter == _meter
            && shortcuts.SequenceEqual(_shortcuts.AsSpan(0, _count)))
            return false;
        _recorded = true;
        _stance = stance;
        _openPanels = openPanels;
        _selection = selection;
        _kind = kind;
        _meter = meter;
        shortcuts.CopyTo(_shortcuts);
        _count = shortcuts.Length;
        return true;
    }
}

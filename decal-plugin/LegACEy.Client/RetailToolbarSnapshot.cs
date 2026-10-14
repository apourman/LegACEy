using System;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The toolbar's readings as one comparable state: the stance, the open window panels (a bit per panel number), the selection and the
/// shortcut list. <see cref="Update"/> keeps the last state, so a reading that matches it reports nothing to redraw.
/// </summary>
internal sealed class RetailToolbarSnapshot
{
    private readonly RetailShortcut[] _shortcuts = new RetailShortcut[RetailToolbarCatalogue.ShortcutSlots];
    private int _count;
    private RetailStance _stance;
    private int _openPanels;
    private RetailSelection? _selection;
    private bool _recorded;

    /// <summary>
    /// Records the readings. True when this is the first reading, or when any input differs from the last one recorded: the stance, a
    /// panel's open state, the selection (its name, kind, stack count and meter) or the shortcut list.
    /// </summary>
    public bool Update(RetailStance stance, int openPanels, RetailSelection? selection, ReadOnlySpan<RetailShortcut> shortcuts)
    {
        if (_recorded && stance == _stance && openPanels == _openPanels && selection == _selection
            && shortcuts.SequenceEqual(_shortcuts.AsSpan(0, _count)))
            return false;
        _recorded = true;
        _stance = stance;
        _openPanels = openPanels;
        _selection = selection;
        shortcuts.CopyTo(_shortcuts);
        _count = shortcuts.Length;
        return true;
    }
}

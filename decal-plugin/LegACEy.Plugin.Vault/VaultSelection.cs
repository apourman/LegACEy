using System;
using System.Collections.Generic;

namespace LegACEy.Plugin.Vault;

/// <summary>
/// The items selected in the page on screen, held by their place in the page's order. A plain click selects one item, or
/// clears it when it is the only one selected; Ctrl toggles one; Shift selects the range from the anchor; Ctrl+Shift adds that range. The anchor is the last item that was
/// plain-clicked or Ctrl-clicked.
/// </summary>
public sealed class VaultSelection
{
    private readonly SortedSet<int> _indices = new();

    /// <summary>The place in the page of the anchor, or -1 when nothing is anchored.</summary>
    public int Anchor { get; private set; } = -1;

    /// <summary>The selected places, in the page's order.</summary>
    public IReadOnlyCollection<int> Indices => _indices;

    public int Count => _indices.Count;

    public bool Contains(int index) => _indices.Contains(index);

    /// <summary>Applies a press on the item at <paramref name="index"/>. Shift or Ctrl+Shift with no anchor acts as a plain or Ctrl click.</summary>
    public void Press(int index, bool ctrl, bool shift)
    {
        if (shift && Anchor >= 0)
        {
            if (!ctrl) _indices.Clear();
            for (var place = Math.Min(Anchor, index); place <= Math.Max(Anchor, index); place++) _indices.Add(place);
            return;
        }
        if (ctrl)
        {
            if (!_indices.Remove(index)) _indices.Add(index);
        }
        else
        {
            var only = _indices.Count == 1 && _indices.Contains(index);
            _indices.Clear();
            if (only)
            {
                Anchor = -1;
                return;
            }
            _indices.Add(index);
        }
        Anchor = index;
    }

    /// <summary>Drops the selection and its anchor.</summary>
    public void Clear()
    {
        _indices.Clear();
        Anchor = -1;
    }
}

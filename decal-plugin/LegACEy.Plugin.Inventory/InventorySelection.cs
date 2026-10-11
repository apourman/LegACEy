using System;
using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Plugin.Inventory;

/// <summary>
/// The items picked in the open pack, held by id, so a change that shifts the pack's items keeps the same items picked. As in the Vault:
/// a plain click picks one item; Ctrl toggles one; Shift picks the range from the anchor, in the pack's order; Ctrl+Shift adds that range.
/// The anchor is the last item plain-clicked or Ctrl-clicked.
/// </summary>
public sealed class InventorySelection
{
    private readonly HashSet<uint> _ids = new();
    private uint _anchor;

    public int Count => _ids.Count;

    public bool Contains(uint id) => _ids.Contains(id);

    /// <summary>Applies a press on the item. <paramref name="order"/> is the open pack's items in slot order. Shift with no anchor in it acts as a plain or Ctrl click.</summary>
    public void Press(uint id, IReadOnlyList<uint> order, bool ctrl, bool shift)
    {
        var from = shift ? IndexOf(order, _anchor) : -1;
        var to = IndexOf(order, id);
        if (from >= 0 && to >= 0)
        {
            if (!ctrl) _ids.Clear();
            for (var place = Math.Min(from, to); place <= Math.Max(from, to); place++) _ids.Add(order[place]);
            return;
        }
        if (ctrl)
        {
            if (!_ids.Remove(id)) _ids.Add(id);
        }
        else
        {
            _ids.Clear();
            _ids.Add(id);
        }
        _anchor = id;
    }

    /// <summary>Drops every picked item that is no longer in the open pack, as after a move, a deposit or a change of pack.</summary>
    public void Keep(IReadOnlyList<uint> order)
    {
        _ids.IntersectWith(order);
        if (IndexOf(order, _anchor) < 0) _anchor = 0;
    }

    /// <summary>The picked items in the pack's order.</summary>
    public IReadOnlyList<uint> InOrder(IReadOnlyList<uint> order) => order.Where(_ids.Contains).ToList();

    public void Clear()
    {
        _ids.Clear();
        _anchor = 0;
    }

    private static int IndexOf(IReadOnlyList<uint> order, uint id)
    {
        if (id == 0) return -1;
        for (var index = 0; index < order.Count; index++)
            if (order[index] == id) return index;
        return -1;
    }
}

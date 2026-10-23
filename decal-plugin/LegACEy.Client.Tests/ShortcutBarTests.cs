using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Xunit;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace LegACEy.Client.Tests;

public sealed class ShortcutBarTests
{
    private const int Width = 400;
    private const int Height = 60;

    [Fact]
    public void Slots_fill_from_the_port_list_and_follow_a_change_including_an_item_that_is_gone() => RenderThread.Run(() =>
    {
        var held = new Dictionary<uint, ShortcutItem> { [0x100] = Potions, [0x200] = Swords };
        var port = new FakeShortcutPort(new[] { new RetailShortcut(0, 0x100), new RetailShortcut(4, 0x200) });
        using var panel = Open(port, held, out _);
        panel.Tick();
        var filled = panel.Frame.Pixels.ToArray();

        // Retail or another session adds a shortcut, and the item of slot 4 is gone from the player's hands: slot 4 shows empty.
        held.Remove(0x200);
        held[0x300] = Potions;
        var changed = new[] { new RetailShortcut(0, 0x100), new RetailShortcut(1, 0x300), new RetailShortcut(4, 0x200) };
        port.Set(changed);
        panel.Tick();

        PanelFrameAssert.AssertDrawnLike(Render(changed, held), panel);
        // A slot holding an item shows it, so the filled frame differs from the one with no shortcuts at all.
        Assert.NotEqual(filled, Render(Array.Empty<RetailShortcut>(), held));
    });

    [Fact]
    public void A_click_selects_and_uses_the_slot_it_lands_on_and_an_empty_slot_does_nothing() => RenderThread.Run(() =>
    {
        var held = new Dictionary<uint, ShortcutItem> { [0x200] = Swords };
        var port = new FakeShortcutPort(new[] { new RetailShortcut(4, 0x200) });
        using var panel = Open(port, held, out var bar);
        panel.Tick();

        Click(panel, bar, bar.SlotAt(1));
        Click(panel, bar, bar.SlotAt(4));

        Assert.Equal(new[] { 0x200u }, port.Selected);
        Assert.Equal(new[] { 4 }, port.Used);
    });

    [Fact]
    public void Row_2_shows_and_uses_slots_9_to_17() => RenderThread.Run(() =>
    {
        var held = new Dictionary<uint, ShortcutItem> { [0x200] = Swords };
        var port = new FakeShortcutPort(new[] { new RetailShortcut(9, 0x200), new RetailShortcut(17, 0x200) });
        using var panel = Open(port, held, out var bar, firstIndex: 9);
        panel.Tick();

        Assert.NotEqual(Render(Array.Empty<RetailShortcut>(), held, firstIndex: 9), panel.Frame.Pixels.ToArray());
        Click(panel, bar, bar.SlotAt(0));
        Click(panel, bar, bar.SlotAt(8));

        Assert.Equal(new[] { 9, 17 }, port.Used);
    });

    [Fact]
    public void Row_2_is_on_for_a_character_with_no_saved_choice() =>
        Assert.True(new ShortcutRowChoice(new MemoryWindowPositionStore()).IsOn("server", "Alice"));

    [Fact]
    public void Turning_row_2_off_and_on_is_kept_per_character()
    {
        var store = new MemoryWindowPositionStore();
        var choice = new ShortcutRowChoice(store);

        choice.Save("server", "Alice", false);
        Assert.False(choice.IsOn("server", "Alice"));
        Assert.True(choice.IsOn("server", "Bob"));
        // After a restart, the same store still has row 2 off for Alice.
        Assert.False(new ShortcutRowChoice(store).IsOn("server", "Alice"));

        choice.Save("server", "Alice", true);
        Assert.True(choice.IsOn("server", "Alice"));
    }

    [Fact]
    public void A_drop_on_an_empty_slot_binds_the_item_at_that_index() => RenderThread.Run(() =>
    {
        var port = new FakeShortcutPort(Array.Empty<RetailShortcut>());
        using var panel = Open(port, new Dictionary<uint, ShortcutItem>(), out var bar);
        panel.Tick();

        Assert.True(bar.RetailDrop(0x100, string.Empty, Centre(bar, bar.SlotAt(3))));

        Assert.Equal(new[] { "add 3 0x100" }, port.Edits);
    });

    [Fact]
    public void A_drop_on_a_bound_slot_replaces_it_and_the_old_shortcut_goes_to_the_first_empty_slot_to_the_right() => RenderThread.Run(() =>
    {
        var port = new FakeShortcutPort(new[] { new RetailShortcut(2, 0x200) });
        using var panel = Open(port, new Dictionary<uint, ShortcutItem>(), out var bar);
        panel.Tick();

        Assert.True(bar.RetailDrop(0x300, string.Empty, Centre(bar, bar.SlotAt(2))));

        Assert.Equal(new[] { "remove 2", "add 2 0x300", "add 3 0x200" }, port.Edits);
    });

    [Fact]
    public void A_shortcut_dragged_within_a_row_or_across_rows_moves_and_the_one_it_lands_on_takes_its_place() => RenderThread.Run(() =>
    {
        var port = new FakeShortcutPort(new[] { new RetailShortcut(0, 0x300), new RetailShortcut(1, 0x100), new RetailShortcut(4, 0x200) });
        var drag = new FakeItemDragHost();
        var source = new ShortcutDragSource();
        using var row1 = Open(port, new Dictionary<uint, ShortcutItem>(), out var first, drag: drag, source: source);
        using var row2 = Open(port, new Dictionary<uint, ShortcutItem>(), out var second, firstIndex: 9, drag: drag, source: source);
        row1.Tick();
        row2.Tick();

        // The shortcut leaves its slot when the drag begins, and lands on release.
        BeginDrag(row1, first, first.SlotAt(1));
        Assert.Equal(new[] { "remove 1" }, port.Edits);
        Release(row1, drag, first, first.SlotAt(4));
        Assert.Equal(new[] { "remove 1", "remove 4", "add 4 0x100", "add 1 0x200" }, port.Edits);
        port.Edits.Clear();

        BeginDrag(row1, first, first.SlotAt(0));
        Release(row1, drag, second, second.SlotAt(0));
        Assert.Equal(new[] { "remove 0", "add 9 0x300" }, port.Edits);
    });

    [Fact]
    public void A_drop_of_the_item_a_slot_already_holds_changes_nothing() => RenderThread.Run(() =>
    {
        var port = new FakeShortcutPort(new[] { new RetailShortcut(3, 0x100) });
        using var panel = Open(port, new Dictionary<uint, ShortcutItem>(), out var bar);
        panel.Tick();

        Assert.True(bar.RetailDrop(0x100, string.Empty, Centre(bar, bar.SlotAt(3))));

        Assert.Empty(port.Edits);
    });

    [Fact]
    public void A_drop_of_an_item_bound_on_another_slot_moves_it_so_it_is_bound_once() => RenderThread.Run(() =>
    {
        var port = new FakeShortcutPort(new[] { new RetailShortcut(2, 0x100) });
        using var panel = Open(port, new Dictionary<uint, ShortcutItem>(), out var bar);
        panel.Tick();

        Assert.True(bar.RetailDrop(0x100, string.Empty, Centre(bar, bar.SlotAt(5))));

        Assert.Equal(new[] { "remove 2", "add 5 0x100" }, port.Edits);
    });

    [Fact]
    public void A_shortcut_dragged_off_the_bars_is_removed() => RenderThread.Run(() =>
    {
        var port = new FakeShortcutPort(new[] { new RetailShortcut(1, 0x100) });
        var drag = new FakeItemDragHost();
        using var panel = Open(port, new Dictionary<uint, ShortcutItem>(), out var bar, drag: drag);
        panel.Tick();

        BeginDrag(panel, bar, bar.SlotAt(1));
        Release(panel, drag);

        Assert.Equal(new[] { "remove 1" }, port.Edits);
    });

    [Fact]
    public void A_shown_shortcut_dragged_to_another_slot_moves_there_and_dragged_off_is_removed() => RenderThread.Run(() =>
    {
        var held = new Dictionary<uint, ShortcutItem> { [0x100] = Potions, [0x200] = Swords };
        var port = new FakeShortcutPort(new[] { new RetailShortcut(1, 0x100), new RetailShortcut(2, 0x200) });
        var drag = new FakeItemDragHost();
        using var panel = Open(port, held, out var bar, drag: drag);
        panel.Tick();

        BeginDrag(panel, bar, bar.SlotAt(1));
        Release(panel, drag, bar, bar.SlotAt(4));
        Assert.Equal(new[] { new RetailShortcut(2, 0x200), new RetailShortcut(4, 0x100) }, port.Shortcuts);

        panel.Tick();
        BeginDrag(panel, bar, bar.SlotAt(2));
        Release(panel, drag);
        Assert.Equal(new[] { new RetailShortcut(4, 0x100) }, port.Shortcuts);
    });

    [Fact]
    public void An_item_dragged_over_the_bar_lights_the_slot_under_it_and_leaving_clears_it() => RenderThread.Run(() =>
    {
        using var panel = Open(new FakeShortcutPort(Array.Empty<RetailShortcut>()), new Dictionary<uint, ShortcutItem>(), out var bar);
        panel.Tick();
        var slot = (DerethSlot)bar.SlotAt(3);
        var centre = slot.TranslatePoint(new Avalonia.Point(slot.Bounds.Width / 2, slot.Bounds.Height / 2), bar)!.Value;

        bar.RetailDragOver(0x100, "Sword", centre);
        Assert.True(slot.Selected);
        Assert.False(((DerethSlot)bar.SlotAt(2)).Selected);

        bar.RetailDragOver(0x100, "Sword", null);
        Assert.False(slot.Selected);
    });

    private static readonly ShortcutItem Potions = new(new ItemVisual(0x06001074, 0, 0, 0), 12);
    private static readonly ShortcutItem Swords = new(new ItemVisual(0x060010DA, 0, 0, 0), 1);

    /// <summary>A bar over a fake port, drawn in a panel of its own. The items it shows are the ones <paramref name="held"/> holds.</summary>
    private static AvaloniaPanel Open(FakeShortcutPort port, IReadOnlyDictionary<uint, ShortcutItem> held, out ShortcutBar bar,
        int firstIndex = 0, FakeItemDragHost? drag = null, ShortcutDragSource? source = null)
    {
        ShortcutBar? created = null;
        var panel = AvaloniaPanel.Create(() => created = new ShortcutBar(firstIndex, port, id => held.TryGetValue(id, out var item) ? item : null, null,
            drag ?? new FakeItemDragHost(), source ?? new ShortcutDragSource()), Width, Height);
        bar = created!;
        return panel;
    }

    /// <summary>A bar in the given state, drawn once: what a player sees after a change must equal this.</summary>
    private static byte[] Render(IReadOnlyList<RetailShortcut> shortcuts, IReadOnlyDictionary<uint, ShortcutItem> held, int firstIndex = 0)
    {
        using var panel = Open(new FakeShortcutPort(shortcuts), held, out _, firstIndex: firstIndex);
        panel.Tick();
        return panel.Frame.Pixels.ToArray();
    }

    /// <summary>A press and release at the middle of a control, where the player clicks it.</summary>
    private static void Click(AvaloniaPanel panel, ShortcutBar bar, Control control)
    {
        Press(panel, bar, control);
        panel.PointerLeave();
    }

    private static void Press(AvaloniaPanel panel, ShortcutBar bar, Control control)
    {
        var centre = control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), bar)!.Value;
        panel.PointerDown(centre.X, centre.Y);
        panel.PointerUp(centre.X, centre.Y);
    }

    /// <summary>A press on a slot and a move 20 px to the right: the drag of its shortcut begins.</summary>
    private static void BeginDrag(AvaloniaPanel panel, ShortcutBar bar, Control source)
    {
        var start = Centre(bar, source);
        panel.PointerDown(start.X, start.Y);
        panel.PointerMove(start.X + 20, start.Y);
    }

    /// <summary>
    /// The release of a drag begun on a slot. The host hands it to <paramref name="target"/> of <paramref name="targetBar"/>, as the client
    /// does for a window under the pointer. With no target the release is over nothing, and no LegACEy window takes it.
    /// </summary>
    private static void Release(AvaloniaPanel panel, FakeItemDragHost drag, ShortcutBar? targetBar = null, Control? target = null)
    {
        drag.Relay = (id, name) =>
        {
            if (targetBar == null || target == null) return false;
            targetBar.RetailDrop(id, name, Centre(targetBar, target));
            return true;
        };
        panel.PointerUp(0, 0);
    }

    /// <summary>The middle of a control, in the bar's coordinates: where the player points at it.</summary>
    private static Avalonia.Point Centre(ShortcutBar bar, Control control) =>
        control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), bar)!.Value;

    /// <summary>A fake of retail's toolbar for the shortcut row: its list, the slots it was asked to use, and each add and remove in order.</summary>
    private sealed class FakeShortcutPort : IRetailToolbarPort
    {
        private List<RetailShortcut> _shortcuts;

        public FakeShortcutPort(IReadOnlyList<RetailShortcut> shortcuts) => _shortcuts = shortcuts.ToList();

        public IReadOnlyList<RetailShortcut> Shortcuts => _shortcuts.OrderBy(shortcut => shortcut.Index).ToArray();
        public List<int> Used { get; } = new();
        public List<string> Edits { get; } = new();

        public RetailStance Stance => RetailStance.Peace;
        public void ToggleStance() { }
        public bool IsPanelOpen(RetailPanel panel) => false;
        public void SetPanelOpen(RetailPanel panel, bool open) { }
        public void UseShortcut(int index) => Used.Add(index);
        public void Select(uint objectId) => Selected.Add(objectId);
        public List<uint> Selected { get; } = new();
        public void AddShortcut(int index, uint objectId)
        {
            Edits.Add($"add {index} 0x{objectId:X}");
            _shortcuts.RemoveAll(shortcut => shortcut.Index == index);
            _shortcuts.Add(new RetailShortcut(index, objectId));
            Changed?.Invoke();
        }
        // Each edit announces the change, as retail's list does, so a bar redraws in the middle of a drag.
        public void RemoveShortcut(int index)
        {
            Edits.Add($"remove {index}");
            _shortcuts.RemoveAll(shortcut => shortcut.Index == index);
            Changed?.Invoke();
        }
        public RetailSelection? Selection => null;
        public int SplitAmount { get; set; } = 1;
        public void UseSelected() { }
        public void ExamineSelected() { }

        public event Action? Changed;

        public void Set(IReadOnlyList<RetailShortcut> shortcuts)
        {
            _shortcuts = shortcuts.ToList();
            Changed?.Invoke();
        }
    }
}

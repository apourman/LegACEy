using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
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
    public void A_click_uses_the_slot_it_lands_on_and_an_empty_slot_uses_nothing() => RenderThread.Run(() =>
    {
        var held = new Dictionary<uint, ShortcutItem> { [0x200] = Swords };
        var port = new FakeShortcutPort(new[] { new RetailShortcut(4, 0x200) });
        using var panel = Open(port, held, out var bar);
        panel.Tick();

        Click(panel, bar, bar.SlotAt(1));
        Click(panel, bar, bar.SlotAt(4));

        Assert.Equal(new[] { 4 }, port.Used);
    });

    [Fact]
    public void The_frame_and_the_tag_drag_the_bar_unless_retail_s_ui_is_locked_and_a_slot_never_does() => RenderThread.Run(() =>
    {
        var locked = true;
        var moves = 0;
        var port = new FakeShortcutPort(Array.Empty<RetailShortcut>());
        using var panel = Open(port, new Dictionary<uint, ShortcutItem>(), out var bar, () => locked, () => moves++);
        panel.Tick();

        Press(panel, bar, bar.RowTag);
        Assert.Equal(0, moves);

        locked = false;
        Press(panel, bar, bar.SlotAt(0));
        Assert.Equal(0, moves);
        Press(panel, bar, bar.RowTag);
        Assert.Equal(1, moves);
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
    public void A_drag_on_the_row_tag_moves_the_bar_and_does_not_click_it() => RenderThread.Run(() =>
    {
        var moves = 0;
        var clicks = 0;
        var pointer = new Point(0, 0);
        var port = new FakeShortcutPort(Array.Empty<RetailShortcut>());
        using var panel = Open(port, new Dictionary<uint, ShortcutItem>(), out var bar, startMove: () => moves++,
            tagClick: () => clicks++, pointer: () => pointer);
        panel.Tick();

        var centre = bar.RowTag.TranslatePoint(new Avalonia.Point(bar.RowTag.Bounds.Width / 2, bar.RowTag.Bounds.Height / 2), bar)!.Value;
        panel.PointerDown(centre.X, centre.Y);
        // The pointer moves 20 px before the button comes up: the window moves with it, and it is a drag.
        pointer = new Point(20, 0);
        panel.PointerUp(centre.X, centre.Y);

        Assert.Equal(1, moves);
        Assert.Equal(0, clicks);
    });

    [Fact]
    public void AboveRow_puts_the_bar_4_DIP_above_with_the_same_left_edge() =>
        Assert.Equal(new Point(724, 856), ShortcutBar.AboveRow(new Point(724, 900), 0.85));

    [Fact]
    public void Row_2_is_on_for_a_character_with_no_saved_choice() =>
        Assert.True(new ShortcutRowChoice(new MemoryWindowPositionStore()).IsOn("server", "Alice"));

    [Fact]
    public void The_row_tag_click_turns_row_2_off_and_on_and_the_choice_is_kept_per_character() => RenderThread.Run(() =>
    {
        var store = new MemoryWindowPositionStore();
        var choice = new ShortcutRowChoice(store);
        var port = new FakeShortcutPort(Array.Empty<RetailShortcut>());
        // The runtime's rule: the tag shows or hides row 2 and saves the new state for the character.
        var on = true;
        Action toggle = () => choice.Save("server", "Alice", on = !on);
        using var panel = Open(port, new Dictionary<uint, ShortcutItem>(), out var bar, tagClick: toggle);
        panel.Tick();

        Click(panel, bar, bar.RowTag);
        Assert.False(choice.IsOn("server", "Alice"));
        Assert.True(choice.IsOn("server", "Bob"));
        // After a restart, the same store still has row 2 off for Alice.
        Assert.False(new ShortcutRowChoice(store).IsOn("server", "Alice"));

        Click(panel, bar, bar.RowTag);
        Assert.True(choice.IsOn("server", "Alice"));
    });

    [Fact]
    public void The_default_location_puts_the_bar_4_DIP_left_of_the_toolbar_with_its_bottom_level_with_it() =>
        Assert.Equal(new Point(724, 907), ShortcutBar.DefaultLocation(new Rectangle(1000, 900, 300, 48), new Size(321, 48), 0.85));

    private static readonly ShortcutItem Potions = new(new ItemVisual(0x06001074, 0, 0, 0), 12);
    private static readonly ShortcutItem Swords = new(new ItemVisual(0x060010DA, 0, 0, 0), 1);

    /// <summary>A bar over a fake port, drawn in a window of its own. The items it shows are the ones <paramref name="held"/> holds.</summary>
    private static AvaloniaPanel Open(FakeShortcutPort port, IReadOnlyDictionary<uint, ShortcutItem> held, out ShortcutBar bar,
        Func<bool>? locked = null, Action? startMove = null, int firstIndex = 0, Action? tagClick = null, Func<Point>? pointer = null)
    {
        ShortcutBar? created = null;
        var panel = AvaloniaPanel.Create(() => created = new ShortcutBar(firstIndex == 0 ? 1 : 2, firstIndex, port,
            id => held.TryGetValue(id, out var item) ? item : null, null, locked ?? (() => false), startMove ?? (() => { }),
            pointer ?? (() => new Point(0, 0)), tagClick), Width, Height);
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

    /// <summary>A fake of retail's toolbar for the shortcut row: its list, and the slots it was asked to use.</summary>
    private sealed class FakeShortcutPort : IRetailToolbarPort
    {
        public FakeShortcutPort(IReadOnlyList<RetailShortcut> shortcuts) => Shortcuts = shortcuts;

        public IReadOnlyList<RetailShortcut> Shortcuts { get; private set; }
        public List<int> Used { get; } = new();

        public RetailStance Stance => RetailStance.Peace;
        public void ToggleStance() { }
        public bool IsPanelOpen(RetailPanel panel) => false;
        public void SetPanelOpen(RetailPanel panel, bool open) { }
        public void UseShortcut(int index) => Used.Add(index);
        public RetailSelection? Selection => null;
        public void UseSelected() { }
        public void ExamineSelected() { }

        public event Action? Changed;

        public void Set(IReadOnlyList<RetailShortcut> shortcuts)
        {
            Shortcuts = shortcuts;
            Changed?.Invoke();
        }
    }
}

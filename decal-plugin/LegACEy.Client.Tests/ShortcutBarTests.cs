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

        AssertDrawn(Render(changed, held), panel);
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
    public void The_default_location_puts_the_bar_4_DIP_left_of_the_toolbar_with_its_bottom_level_with_it() =>
        Assert.Equal(new Point(724, 907), ShortcutBar.DefaultLocation(new Rectangle(1000, 900, 300, 48), new Size(321, 48), 0.85));

    private static readonly ShortcutItem Potions = new(new ItemVisual(0x06001074, 0, 0, 0), 12);
    private static readonly ShortcutItem Swords = new(new ItemVisual(0x060010DA, 0, 0, 0), 1);

    /// <summary>A bar over a fake port, drawn in a window of its own. The items it shows are the ones <paramref name="held"/> holds.</summary>
    private static AvaloniaPanel Open(FakeShortcutPort port, IReadOnlyDictionary<uint, ShortcutItem> held, out ShortcutBar bar,
        Func<bool>? locked = null, Action? startMove = null)
    {
        ShortcutBar? created = null;
        var panel = AvaloniaPanel.Create(() => created = new ShortcutBar(1, 0, port, id => held.TryGetValue(id, out var item) ? item : null, null,
            locked ?? (() => false), startMove ?? (() => { })), Width, Height);
        bar = created!;
        return panel;
    }

    /// <summary>A bar in the given state, drawn once: what a player sees after a change must equal this.</summary>
    private static byte[] Render(IReadOnlyList<RetailShortcut> shortcuts, IReadOnlyDictionary<uint, ShortcutItem> held)
    {
        using var panel = Open(new FakeShortcutPort(shortcuts), held, out _);
        panel.Tick();
        return panel.Frame.Pixels.ToArray();
    }

    /// <summary>
    /// The panel's frame must show what a fresh bar in the same state draws. A mismatch names the pixels that differ. A redrawn edge is
    /// blended over the pixels it replaces, so it may differ from a fresh draw by a level or two; a change of content differs by far more.
    /// </summary>
    private static void AssertDrawn(byte[] fresh, AvaloniaPanel panel)
    {
        var actual = panel.Frame.Pixels.ToArray();
        var stride = panel.Frame.Stride / 4;
        var differing = new List<(int X, int Y)>();
        for (var i = 0; i + 4 <= fresh.Length; i += 4)
            if (Enumerable.Range(0, 4).Any(channel => Math.Abs(fresh[i + channel] - actual[i + channel]) > 2))
                differing.Add(((i / 4) % stride, (i / 4) / stride));
        if (differing.Count == 0) return;
        Assert.Fail($"{differing.Count} pixels differ, from ({differing.Min(p => p.X)},{differing.Min(p => p.Y)}) to ({differing.Max(p => p.X)},{differing.Max(p => p.Y)}).");
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

        public event Action? Changed;

        public void Set(IReadOnlyList<RetailShortcut> shortcuts)
        {
            Shortcuts = shortcuts;
            Changed?.Invoke();
        }
    }
}

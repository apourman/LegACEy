using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Tests;

public sealed class RetailToolbarTests
{
    private const int Width = 600;
    private const int Height = 90;

    [Fact]
    public void Stance_shows_the_port_stance_and_a_click_asks_the_port_to_toggle() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort();
        using var panel = Open(port, out var toolbar);
        panel.Tick();

        // Behaviour correction (spec story 14: the click changes stance as retail's button does): a click toggles, not cycles.
        Click(panel, toolbar, toolbar.StanceButton);
        Assert.Equal(1, port.StanceToggles);
        Assert.Equal(RetailStance.Melee, port.Stance);

        // A stance change made by a key or by the game shows on the next frame, as a fresh toolbar in that stance draws it.
        port.Stance = RetailStance.Missile;
        port.Raise();
        panel.Tick();
        AssertDrawn(Render(p => p.Stance = RetailStance.Missile), panel);
    });

    [Fact]
    public void Each_window_button_and_the_backpack_asks_for_its_panel_number_and_toggles_it() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort();
        using var panel = Open(port, out var toolbar);
        panel.Tick();
        var buttons = new[] { RetailPanel.Social, RetailPanel.Magic, RetailPanel.Skills, RetailPanel.Quests, RetailPanel.WorldMap, RetailPanel.Options, RetailPanel.Inventory };

        foreach (var button in buttons)
        {
            Click(panel, toolbar, toolbar.ButtonFor(button));
            Click(panel, toolbar, toolbar.ButtonFor(button));
        }

        Assert.Equal(new[] { (12, true), (12, false), (13, true), (13, false), (11, true), (11, false), (25, true), (25, false),
            (16, true), (16, false), (10, true), (10, false), (7, true), (7, false) }, port.PanelRequests.Select(r => ((int)r.Panel, r.Open)));
    });

    [Fact]
    public void A_window_ring_follows_the_open_state_however_the_window_changed() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort();
        using var panel = Open(port, out _);
        panel.Tick();

        // Opened by a key, or by the window's own button: the ring shows as a fresh toolbar with it open draws it.
        port.Open(RetailPanel.Skills, true);
        panel.Tick();
        AssertDrawn(Render(p => p.Open(RetailPanel.Skills, true)), panel);

        // Closed by its own close box: the ring goes.
        port.Open(RetailPanel.Skills, false);
        panel.Tick();
        AssertDrawn(Render(), panel);
    });

    [Fact]
    public void The_menu_button_opens_the_menu_and_rings_while_the_menu_is_open() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort();
        var menuClicks = 0;
        RetailToolbar? toolbar = null;
        using var panel = AvaloniaPanel.Create(() => toolbar = new RetailToolbar(port, () =>
        {
            menuClicks++;
            // The client opens its menu, then tells the toolbar it is open.
            toolbar!.SetMenuOpen(true);
        }, _ => true), Width, Height);
        panel.Tick();

        Click(panel, toolbar!, toolbar!.MenuButton);
        panel.Tick();

        Assert.Equal(1, menuClicks);
        AssertDrawn(Render(arrangeToolbar: t => t.SetMenuOpen(true)), panel);
    });

    [Fact]
    public void A_second_click_asks_to_close_a_window_the_port_only_knows_it_was_asked_to_open() => RenderThread.Run(() =>
    {
        var port = new RequestedOnlyPort();
        using var panel = Open(port, out var toolbar);
        panel.Tick();

        Click(panel, toolbar, toolbar.ButtonFor(RetailPanel.Skills));
        Click(panel, toolbar, toolbar.ButtonFor(RetailPanel.Skills));

        Assert.Equal(new[] { (11, true), (11, false) }, port.Requests.Select(r => ((int)r.Panel, r.Open)));
    });

    [Fact]
    public void With_nothing_selected_use_and_examine_do_nothing() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort();
        using var panel = Open(port, out var toolbar);
        panel.Tick();

        Click(panel, toolbar, toolbar.UseButton);
        Click(panel, toolbar, toolbar.ExamineButton);

        Assert.Equal(0, port.UseCount);
        Assert.Equal(0, port.ExamineCount);
    });

    [Fact]
    public void Use_and_examine_act_on_the_selected_object() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort { Selection = new RetailSelection(0x50000001, "Ivory Quarter Staff", SelectionKind.ItemWithMana, 0.82f) };
        using var panel = Open(port, out var toolbar);
        panel.Tick();

        Click(panel, toolbar, toolbar.UseButton);
        Click(panel, toolbar, toolbar.ExamineButton);

        Assert.Equal(1, port.UseCount);
        Assert.Equal(1, port.ExamineCount);
    });

    [Fact]
    public void The_plaque_follows_the_selection_through_nothing_creature_mana_item_and_stack() => RenderThread.Run(() =>
    {
        var creature = new RetailSelection(0x80000001, "Drudge Skulker", SelectionKind.Creature, 0.64f);
        var hurt = creature with { Meter = 0.3f };
        var staff = new RetailSelection(0x50000002, "Ivory Quarter Staff", SelectionKind.ItemWithMana, 0.82f);
        var pyreals = Pyreals(12500);
        var port = new FakeToolbarPort();
        using var panel = Open(port, out _);
        panel.Tick();

        // Each state is shown as a fresh toolbar in that state draws it, so a change made anywhere reaches the plaque on the next frame.
        foreach (var selection in new RetailSelection?[] { creature, hurt, staff, pyreals, null })
        {
            port.Selection = selection;
            port.Raise();
            panel.Tick();
            AssertDrawn(Render(p => p.Selection = selection), panel);
        }
    });

    [Fact]
    public void A_drop_on_the_backpack_moves_the_items_to_the_main_pack_and_a_drop_elsewhere_or_with_no_room_is_not_taken() => RenderThread.Run(() =>
    {
        var moved = new List<uint>();
        bool MoveToMainPack(IReadOnlyList<uint> ids)
        {
            moved.AddRange(ids);
            return true;
        }
        using var panel = Open(new FakeToolbarPort(), out var toolbar, MoveToMainPack);
        panel.Tick();

        var backpack = toolbar.ButtonFor(RetailPanel.Inventory);
        var backpackCentre = backpack.TranslatePoint(new Point(backpack.Bounds.Width / 2, backpack.Bounds.Height / 2), toolbar)!.Value;
        var social = toolbar.ButtonFor(RetailPanel.Social);
        var socialCentre = social.TranslatePoint(new Point(social.Bounds.Width / 2, social.Bounds.Height / 2), toolbar)!.Value;

        Assert.True(toolbar.RetailDrop(0x500, string.Empty, backpackCentre));
        Assert.True(toolbar.DropMany(new uint[] { 0x501, 0x502 }, backpackCentre));
        Assert.False(toolbar.RetailDrop(0x500, string.Empty, socialCentre));
        Assert.Equal(new uint[] { 0x500, 0x501, 0x502 }, moved);

        // A pack with no room for the item takes nothing, and the drop is not taken.
        using var full = Open(new FakeToolbarPort(), out var fullToolbar, _ => false);
        full.Tick();
        var fullBackpack = fullToolbar.ButtonFor(RetailPanel.Inventory);
        Assert.False(fullToolbar.RetailDrop(0x500, string.Empty,
            fullBackpack.TranslatePoint(new Point(fullBackpack.Bounds.Width / 2, fullBackpack.Bounds.Height / 2), fullToolbar)!.Value));
    });

    [Fact]
    public void The_shortcut_rows_and_the_buttons_above_them_are_one_width() => RenderThread.Run(() =>
    {
        RetailToolbar? toolbar = null;
        ShortcutBar row1 = Row(0, new ShortcutDragSource()), row2 = Row(9, new ShortcutDragSource());
        // Laid out at its own size, as the client sizes the toolbar's panel: the rows set it.
        using var panel = AvaloniaPanel.Create(() => toolbar = new RetailToolbar(new FakeToolbarPort(), () => { }, _ => true), 1, 1);
        toolbar!.SetShortcutRows(new[] { row1, row2 });
        toolbar.Measure(Size.Infinity);
        panel.Resize((int)Math.Ceiling(toolbar!.DesiredSize.Width), (int)Math.Ceiling(toolbar.DesiredSize.Height));
        panel.Tick();

        double Left(Control control) => control.TranslatePoint(default, toolbar)!.Value.X;
        double Right(Control control) => Left(control) + control.Bounds.Width;
        Assert.InRange(Left(toolbar.StanceButton) - Left(row1.RowTag), -1, 1);
        Assert.InRange(Right(toolbar.ButtonFor(RetailPanel.Inventory)) - Right(row2.SlotAt(ShortcutBar.SlotCount - 1)), -1, 1);
        Assert.True(row2.SlotAt(0).TranslatePoint(default, toolbar)!.Value.Y > row1.SlotAt(0).TranslatePoint(default, toolbar)!.Value.Y);
    });

    [Fact]
    public void A_shortcut_released_on_a_row_lands_there_and_on_the_backpack_is_not_moved_to_the_main_pack() => RenderThread.Run(() =>
    {
        var moved = new List<uint>();
        var source = new ShortcutDragSource();
        var row = Row(0, source);
        using var panel = Open(new FakeToolbarPort(), out var toolbar, ids => { moved.AddRange(ids); return true; });
        toolbar.SetShortcutRows(new[] { row });
        panel.Resize(Width, 160);
        panel.Tick();
        Point Centre(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), toolbar)!.Value;

        // A shortcut in hand released on the backpack only comes off its row.
        source.Slot = 2;
        source.ObjectId = 0x100;
        Assert.False(toolbar.RetailDrop(0x100, string.Empty, Centre(toolbar.ButtonFor(RetailPanel.Inventory))));
        Assert.Empty(moved);

        // Released on a slot, the row takes it.
        Assert.True(toolbar.RetailDrop(0x100, string.Empty, Centre(row.SlotAt(4))));
        Assert.Null(source.Slot);
    });

    [Fact]
    public void Selecting_a_stack_shows_the_split_tab_at_retails_amount() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort { Selection = Pyreals(12500), SplitAmount = 10 };
        using var panel = OpenTab(port, out var tab);
        panel.Tick();
        var ten = panel.Frame.Pixels.ToArray();

        Assert.True(tab.IsShown);
        Assert.Equal("10", tab.AmountBox!.Text);
        // The box draws the figure: another amount draws differently, so the box is not a fixed picture.
        port.SplitAmount = 11;
        port.Raise();
        panel.Tick();
        Assert.NotEqual(ten, panel.Frame.Pixels.ToArray());
    });

    [Fact]
    public void A_stack_selected_once_the_tab_is_up_writes_no_amount() => RenderThread.Run(() =>
    {
        // As the client makes it: an empty 1 × 1 panel under the Dereth theme, sized to the tab once a stack is selected.
        var port = new FakeToolbarPort();
        StackSplitTab? created = null;
        using var panel = AvaloniaPanel.Create(() => created = new StackSplitTab(port), 1, 1);
        panel.ApplyTheme(new DerethClientTheme());
        panel.Tick();
        var tab = created!;

        port.Selection = Pyreals(50);
        port.SplitAmount = 50;
        port.SplitWrites.Clear();
        port.Raise();
        tab.Measure(Size.Infinity);
        panel.Resize((int)Math.Ceiling(tab.DesiredSize.Width), (int)Math.Ceiling(tab.DesiredSize.Height));
        panel.Tick();
        panel.Tick();

        Assert.Empty(port.SplitWrites);
        Assert.Equal("50", tab.AmountBox!.Text);
    });

    [Fact]
    public void Typing_sliding_and_the_buttons_write_the_amount_clamped_to_one_and_the_stack() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort { Selection = Pyreals(50), SplitAmount = 10 };
        using var panel = OpenTab(port, out var tab);
        panel.Tick();

        Click(panel, tab, tab.AmountBox!);
        // Keys typed in the box go to the box, which holds the keyboard (retail text mode follows that).
        Assert.True(panel.WantsKeyboard);
        TypeReplacing(panel, "99999");
        Assert.Equal(50, port.SplitAmount);
        TypeReplacing(panel, "0");
        Assert.Equal(1, port.SplitAmount);
        TypeReplacing(panel, "7");
        Assert.Equal(7, port.SplitAmount);

        Slide(panel, tab, tab.AmountSlider!, 1.0);
        Assert.Equal(50, port.SplitAmount);
        Slide(panel, tab, tab.AmountSlider!, 0.0);
        Assert.Equal(1, port.SplitAmount);

        Click(panel, tab, tab.FewerButton!);
        Assert.Equal(1, port.SplitAmount);
        Click(panel, tab, tab.MoreButton!);
        Assert.Equal(2, port.SplitAmount);
        Click(panel, tab, tab.HalfButton!);
        Assert.Equal(25, port.SplitAmount);
        Assert.Equal("25", tab.AmountBox!.Text);
        Click(panel, tab, tab.AllButton!);
        Assert.Equal(50, port.SplitAmount);
        Click(panel, tab, tab.MoreButton!);
        Assert.Equal(50, port.SplitAmount);
        Click(panel, tab, tab.OneButton!);
        Assert.Equal(1, port.SplitAmount);
    });

    [Fact]
    public void A_refresh_keeps_the_typed_amount_and_the_focus() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort { Selection = Pyreals(500), SplitAmount = 10 };
        using var panel = OpenTab(port, out var tab);
        panel.Tick();

        Click(panel, tab, tab.AmountBox!);
        TypeReplacing(panel, "12");
        // The port changes something else (the stance, say) while the box is being typed in: the tab is refreshed.
        port.Raise();
        panel.Tick();

        Assert.True(panel.WantsKeyboard);
        Assert.Equal("12", tab.AmountBox!.Text);
        panel.TextInput("3");
        Assert.Equal(123, port.SplitAmount);
    });

    [Fact]
    public void A_split_typed_for_one_stack_is_not_written_once_another_stack_is_selected() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort { Selection = Pyreals(500), SplitAmount = 10 };
        using var panel = OpenTab(port, out var tab);
        panel.Tick();

        Click(panel, tab, tab.AmountBox!);
        TypeReplacing(panel, "5");
        Assert.Equal(5, port.SplitAmount);
        var writesBeforeSwitch = port.SplitWrites.Count;

        // The player selects a smaller stack while the box still holds the first stack's figure. Retiring that box can
        // move the focus, and a settle from it must not write the first stack's figure to the second.
        port.Selection = new RetailSelection(0x50000007, "Arrows", SelectionKind.Stack, StackCount: 20);
        port.Raise();
        panel.Tick();
        panel.ClearFocus();
        panel.Tick();

        Assert.DoesNotContain(5, port.SplitWrites.Skip(writesBeforeSwitch));
    });

    [Fact]
    public void Only_a_stack_shows_the_split_tab() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort { Selection = new RetailSelection(0x50000004, "Rat Tail", SelectionKind.Other) };
        using var panel = OpenTab(port, out var tab);
        panel.Tick();
        Assert.False(tab.IsShown);

        port.Selection = Pyreals(12500);
        port.Raise();
        panel.Tick();
        Assert.True(tab.IsShown);

        // The stack is down to one: the game's kind is Other now, and the tab goes.
        port.Selection = new RetailSelection(0x50000003, "Pyreals", SelectionKind.Other, StackCount: 1);
        port.Raise();
        panel.Tick();
        Assert.False(tab.IsShown);
        Assert.Null(tab.AmountBox);
    });

    [Fact]
    public void Health_and_mana_are_read_from_the_server_events_by_object_id()
    {
        var meters = new SelectionMeters();

        meters.Receive(0xF7B0, GameEvent(0x01C0, 0x80000001, 0.25f));
        meters.Receive(0xF7B0, GameEvent(0x0264, 0x50000002, 0.82f, success: 1));

        Assert.Equal(0.25f, meters.Health(0x80000001));
        Assert.Null(meters.Health(0x50000002));
        Assert.Equal(0.82f, meters.Mana(0x50000002));
        Assert.Null(meters.Mana(0x80000001));

        // An item with no mana answers with success 0: no reading, not an empty meter.
        meters.Receive(0xF7B0, GameEvent(0x0264, 0x50000003, 0f, success: 0));
        Assert.Null(meters.Mana(0x50000003));
    }

    [Fact]
    public void Another_objects_health_answer_leaves_the_selected_creatures_meter_alone()
    {
        var meters = new SelectionMeters();

        meters.Receive(0xF7B0, GameEvent(0x01C0, 0x80000001, 0.25f));
        // Another query (a fellowship member's, say) answers after it: the selected creature's answer stays.
        meters.Receive(0xF7B0, GameEvent(0x01C0, 0x80000002, 0.9f));

        Assert.Equal(0.25f, meters.Health(0x80000001));
        Assert.Equal(0.9f, meters.Health(0x80000002));
    }

    [Fact]
    public void Only_the_selected_objects_answers_are_kept()
    {
        var selected = 0x80000001u;
        var meters = new SelectionMeters(() => selected);

        meters.Receive(0xF7B0, GameEvent(0x01C0, 0x80000001, 0.25f));
        meters.Receive(0xF7B0, GameEvent(0x01C0, 0x80000002, 0.9f));
        Assert.Equal(0.25f, meters.Health(0x80000001));
        Assert.Null(meters.Health(0x80000002));

        // The selection moves on: the old object's answer is dropped with it.
        selected = 0x80000002;
        meters.KeepOnly(0x80000002);
        Assert.Null(meters.Health(0x80000001));
    }

    [Fact]
    public void The_snapshot_reports_a_change_in_each_input_and_nothing_when_none_moved()
    {
        var snapshot = new RetailToolbarSnapshot();
        var selection = new RetailSelection(0x50000001, "Ivory Quarter Staff", SelectionKind.ItemWithMana, 0.82f);
        var shortcuts = new[] { new RetailShortcut(0, 0x50000001) };
        var none = ReadOnlySpan<RetailShortcut>.Empty;

        Assert.True(snapshot.Update(RetailStance.Peace, 0, null, none));
        Assert.False(snapshot.Update(RetailStance.Peace, 0, null, none));
        Assert.True(snapshot.Update(RetailStance.Melee, 0, null, none));
        Assert.True(snapshot.Update(RetailStance.Melee, 1 << (int)RetailPanel.Skills, null, none));
        Assert.True(snapshot.Update(RetailStance.Melee, 1 << (int)RetailPanel.Skills, selection, none));
        Assert.True(snapshot.Update(RetailStance.Melee, 1 << (int)RetailPanel.Skills, selection, shortcuts));
        Assert.False(snapshot.Update(RetailStance.Melee, 1 << (int)RetailPanel.Skills, selection, shortcuts));
    }

    /// <summary>A game event as the server sends it: the 0xF7B0 header, the character, a sequence number, the event type, then the body.</summary>
    private static byte[] GameEvent(uint eventType, uint objectId, float value, uint? success = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0xF7B0u);
            writer.Write(0x50000001u);
            writer.Write(0u);
            writer.Write(eventType);
            writer.Write(objectId);
            writer.Write(value);
            if (success is { } flag) writer.Write(flag);
        }
        return stream.ToArray();
    }

    private static RetailSelection Pyreals(int count) => new(0x50000003, "Pyreals", SelectionKind.Stack, StackCount: count);

    /// <summary>A port that reads a panel as open only after it was asked to open it, as the native port does for the panels it cannot read yet.</summary>
    private sealed class RequestedOnlyPort : IRetailToolbarPort
    {
        private readonly HashSet<RetailPanel> _open = new();

        public RetailStance Stance => RetailStance.Peace;
        public RetailSelection? Selection => null;
        public int SplitAmount { get; set; } = 1;
        public List<(RetailPanel Panel, bool Open)> Requests { get; } = new();

        public event Action? Changed;

        public void ToggleStance() { }

        public void UseSelected() { }

        public void ExamineSelected() { }

        public bool IsPanelOpen(RetailPanel panel) => _open.Contains(panel);

        public void SetPanelOpen(RetailPanel panel, bool open)
        {
            Requests.Add((panel, open));
            if (open) _open.Add(panel);
            else _open.Remove(panel);
            Changed?.Invoke();
        }

        public IReadOnlyList<RetailShortcut> Shortcuts => Array.Empty<RetailShortcut>();
        public void UseShortcut(int index) { }
        public void AddShortcut(int index, uint objectId) { }
        public void RemoveShortcut(int index) { }
    }

    /// <summary>A fake of retail's toolbar: it records what the toolbar asks, and applies a panel change at once, as retail does.</summary>
    private sealed class FakeToolbarPort : IRetailToolbarPort
    {
        private readonly HashSet<RetailPanel> _open = new();

        public RetailStance Stance { get; set; } = RetailStance.Peace;
        public int StanceToggles { get; private set; }
        public List<(RetailPanel Panel, bool Open)> PanelRequests { get; } = new();
        public RetailSelection? Selection { get; set; }
        private int _splitAmount = 1;
        // Every write, in order, so a test can see what the plaque asked retail for.
        public List<int> SplitWrites { get; } = new();
        public int SplitAmount
        {
            get => _splitAmount;
            set
            {
                _splitAmount = value;
                SplitWrites.Add(value);
            }
        }
        public int UseCount { get; private set; }
        public int ExamineCount { get; private set; }

        public event Action? Changed;

        // Retail's toggle: peace goes to the default stance (melee here) and anything else to peace.
        public void ToggleStance()
        {
            StanceToggles++;
            Stance = Stance == RetailStance.Peace ? RetailStance.Melee : RetailStance.Peace;
        }

        public void UseSelected() => UseCount++;

        public void ExamineSelected() => ExamineCount++;

        public bool IsPanelOpen(RetailPanel panel) => _open.Contains(panel);

        public void SetPanelOpen(RetailPanel panel, bool open)
        {
            PanelRequests.Add((panel, open));
            Open(panel, open);
        }

        /// <summary>Retail opens or closes a panel, by a click, a key or a close box, and tells the toolbar.</summary>
        public void Open(RetailPanel panel, bool open)
        {
            if (open) _open.Add(panel);
            else _open.Remove(panel);
            Raise();
        }

        public void Raise() => Changed?.Invoke();

        public IReadOnlyList<RetailShortcut> Shortcuts => Array.Empty<RetailShortcut>();
        public void UseShortcut(int index) { }
        public void AddShortcut(int index, uint objectId) { }
        public void RemoveShortcut(int index) { }
    }

    /// <summary>A toolbar in the given state, drawn once: what a player sees after a change must equal this.</summary>
    private static byte[] Render(Action<FakeToolbarPort>? arrange = null, Action<RetailToolbar>? arrangeToolbar = null)
    {
        var port = new FakeToolbarPort();
        arrange?.Invoke(port);
        using var panel = Open(port, out var toolbar);
        arrangeToolbar?.Invoke(toolbar);
        panel.Tick();
        return panel.Frame.Pixels.ToArray();
    }

    /// <summary>The panel's frame must show what a fresh toolbar in the same state draws. A mismatch names the pixels that differ.</summary>
    private static void AssertDrawn(byte[] fresh, AvaloniaPanel panel)
    {
        // Drawn whole, as a fresh toolbar is: a redraw clipped to what changed blends the soft edges it cuts through a shade differently.
        foreach (var visual in panel.Content.GetSelfAndVisualDescendants()) visual.InvalidateVisual();
        panel.Tick();
        var actual = panel.Frame.Pixels;
        if (fresh.AsSpan().SequenceEqual(actual)) return;
        var stride = panel.Frame.Stride / 4;
        var differing = new List<(int X, int Y)>();
        for (var i = 0; i + 4 <= fresh.Length; i += 4)
            if (!fresh.AsSpan(i, 4).SequenceEqual(actual.AsSpan(i, 4))) differing.Add(((i / 4) % stride, (i / 4) / stride));
        Assert.Fail($"{differing.Count} pixels differ, from ({differing.Min(p => p.X)},{differing.Min(p => p.Y)}) to ({differing.Max(p => p.X)},{differing.Max(p => p.Y)}).");
    }

    private static AvaloniaPanel Open(IRetailToolbarPort port, out RetailToolbar toolbar, Func<IReadOnlyList<uint>, bool>? moveToMainPack = null)
    {
        RetailToolbar? created = null;
        var panel = AvaloniaPanel.Create(() => created = new RetailToolbar(port, () => { }, moveToMainPack ?? (_ => true)), Width, Height);
        toolbar = created!;
        return panel;
    }

    /// <summary>A shortcut row over an empty shortcut list, for the toolbar to hold.</summary>
    private static ShortcutBar Row(int firstIndex, ShortcutDragSource source) =>
        new(firstIndex == 0 ? 1 : 2, firstIndex, new FakeToolbarPort(), _ => null, null, () => default, null, new FakeItemDragHost(), source);

    private static AvaloniaPanel OpenTab(IRetailToolbarPort port, out StackSplitTab tab)
    {
        StackSplitTab? created = null;
        var panel = AvaloniaPanel.Create(() => created = new StackSplitTab(port), (int)StackSplitTab.TabWidth, 64);
        tab = created!;
        return panel;
    }

    /// <summary>A press and release at the middle of a control, where the player clicks.</summary>
    private static void Click(AvaloniaPanel panel, Control root, Control button)
    {
        var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), root)!.Value;
        panel.PointerDown(centre.X, centre.Y);
        panel.PointerUp(centre.X, centre.Y);
        // The pointer moves on, as a player's does, so the button is not left in its hover art.
        panel.PointerLeave();
    }

    /// <summary>A player's drag of a slider's thumb, from where it sits to a fraction of the track (0 the left end, 1 the right).</summary>
    private static void Slide(AvaloniaPanel panel, Control root, Slider slider, double toFraction)
    {
        // The thumb is laid out again after the last change, so the press finds it where the player sees it.
        panel.Tick();
        // The thumb is 9 DIP wide, so its centre runs from 4.5 to the width less 4.5. Its value sets where it sits.
        double Centre(double fraction) => 4.5 + fraction * (slider.Bounds.Width - 9);
        var fromFraction = (slider.Value - slider.Minimum) / (slider.Maximum - slider.Minimum);
        var from = slider.TranslatePoint(new Point(Centre(fromFraction), slider.Bounds.Height / 2), root)!.Value;
        var to = slider.TranslatePoint(new Point(Centre(toFraction), slider.Bounds.Height / 2), root)!.Value;
        // The pointer leaves the slider between drags, as a player's does; without that the next press does not grab the thumb.
        panel.PointerLeave();
        panel.PointerDown(from.X, from.Y);
        panel.PointerMove(to.X, to.Y);
        panel.PointerUp(to.X, to.Y);
        panel.PointerLeave();
    }

    /// <summary>Selects the box's text with Ctrl+A and types over it.</summary>
    private static void TypeReplacing(AvaloniaPanel panel, string text)
    {
        panel.KeyDown(Key.A, KeyModifiers.Control);
        panel.TextInput(text);
    }
}

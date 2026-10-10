using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Inventory;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>The inventory window in a headless panel, fed by a <see cref="FakeInventoryPort"/>.</summary>
public sealed class InventoryWindowTests
{
    private static readonly PaperdollSlot[] ArmourSlots =
    {
        PaperdollSlot.Head, PaperdollSlot.UpperArms, PaperdollSlot.Chest, PaperdollSlot.LowerArms, PaperdollSlot.Abdomen,
        PaperdollSlot.UpperLegs, PaperdollSlot.Hands, PaperdollSlot.LowerLegs, PaperdollSlot.Feet,
    };

    [Fact]
    public void Each_wielded_item_shows_in_its_slot_and_a_multi_slot_item_in_every_slot_it_covers() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);

        Assert.Equal(InventorySample.Helm, InventoryDriver.Id(Slot(host, PaperdollSlot.Head)).ItemId);
        // The coat is one item in three slots.
        Assert.Equal(InventorySample.Coat, InventoryDriver.Id(Slot(host, PaperdollSlot.Chest)).ItemId);
        Assert.Equal(InventorySample.Coat, InventoryDriver.Id(Slot(host, PaperdollSlot.UpperArms)).ItemId);
        Assert.Equal(InventorySample.Coat, InventoryDriver.Id(Slot(host, PaperdollSlot.LowerArms)).ItemId);
        // A dual-wielded off-hand weapon is in the shield slot.
        Assert.Equal(InventorySample.Dagger, InventoryDriver.Id(Slot(host, PaperdollSlot.Shield)).ItemId);
        // An empty slot shows its glyph and no item.
        Assert.Equal(0u, InventoryDriver.Id(Slot(host, PaperdollSlot.Neck)).ItemId);
        Assert.Null(host.Host.LastError);

        // Selecting the coat from outside lights every slot it covers, and nothing else.
        port.Push(InventorySample.Snapshot(selected: InventorySample.Coat));
        InventoryDriver.Tick(host.Host);
        Assert.True(Slot(host, PaperdollSlot.Chest).Selected);
        Assert.True(Slot(host, PaperdollSlot.UpperArms).Selected);
        Assert.True(Slot(host, PaperdollSlot.LowerArms).Selected);
        Assert.False(Slot(host, PaperdollSlot.Head).Selected);

        // Over the limit the reading and the meter's fill both turn to the invalid colour.
        port.Push(InventorySample.Snapshot(burden: 6000));
        InventoryDriver.Tick(host.Host);
        var reading = Assert.Single(host.Host.Content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "111%");
        Assert.Same(DerethPalette.InvalidBrush, reading.Foreground);
        Assert.Single(host.Host.Content.GetVisualDescendants().OfType<Border>(), border => ReferenceEquals(border.Background, DerethPalette.InvalidBrush));
    });

    [Fact]
    public void The_slots_toggle_hides_and_shows_the_armour_slots_and_reports_the_choice() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var reported = new List<InventorySettings>();
        host.Window.SettingsChanged += reported.Add;

        InventoryDriver.PressSlots(host.Host);
        Assert.All(ArmourSlots, slot => Assert.Null(InventoryDriver.SlotOrNull(host.Host, slot)));
        Assert.NotNull(InventoryDriver.SlotOrNull(host.Host, PaperdollSlot.Neck));
        Assert.False(reported.Single().ShowSlots);

        InventoryDriver.PressSlots(host.Host);
        Assert.All(ArmourSlots, slot => Assert.NotNull(InventoryDriver.SlotOrNull(host.Host, slot)));
        Assert.True(reported[^1].ShowSlots);
    });

    [Fact]
    public void An_open_container_changed_from_outside_switches_the_grid_and_the_grid_has_one_cell_per_slot() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot(openContainer: InventorySample.Character));
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        Assert.Equal(96, host.Cells.Length);
        Assert.Equal(InventorySample.Apple, InventoryDriver.Id(host.Cells[0]).ItemId);

        // Something else opens a side pack. The grid follows: its own capacity, empties included, in slot-index order.
        port.Push(InventorySample.Snapshot(openContainer: InventorySample.Potions));
        InventoryDriver.Tick(host.Host);
        var cells = host.Cells;
        Assert.Equal(24, cells.Length);
        Assert.Equal(Enumerable.Range(0, 24), cells.Select(cell => InventoryDriver.Id(cell).SlotIndex));
        Assert.All(cells, cell => Assert.Equal(InventorySample.Potions, InventoryDriver.Id(cell).Container));
        Assert.Equal(InventorySample.BluePotion, InventoryDriver.Id(cells[0]).ItemId);
        Assert.Equal(InventorySample.YellowPotion, InventoryDriver.Id(cells[5]).ItemId);
        Assert.Equal(0u, InventoryDriver.Id(cells[20]).ItemId);
        // The header reads "Contents of Potions" and its count, "17 / 24".
        var texts = host.Texts();
        Assert.Contains("Contents of Potions", texts);
        Assert.Contains("17 / 24", texts);
    });

    [Fact]
    public void The_layout_toggle_asks_for_the_other_layout_and_keeps_the_slots_choice() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: false);
        var reported = new List<InventorySettings>();
        host.Window.SettingsChanged += reported.Add;

        InventoryDriver.PressLayout(host.Host, InventoryLayout.Vertical);
        Assert.Empty(reported);
        InventoryDriver.PressLayout(host.Host, InventoryLayout.Horizontal);
        var choice = Assert.Single(reported);
        Assert.Equal(InventoryLayout.Horizontal, choice.Layout);
        Assert.False(choice.ShowSlots);
    });

    [Fact]
    public void Clicking_a_pack_shows_its_contents_without_asking_the_server() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);

        // The server answers a use of a carried pack with only "use done" (a busy cursor); retail switches packs locally.
        InventoryDriver.Press(host.Host, InventoryDriver.PackSlot(host.Host, InventorySample.Potions));
        Assert.Contains("Contents of Potions", host.Texts());
        Assert.Equal(InventorySample.BluePotion, InventoryDriver.Id(host.Cells[0]).ItemId);

        InventoryDriver.Press(host.Host, InventoryDriver.PackSlot(host.Host, InventorySample.Character));
        Assert.Contains("Contents of Main Pack", host.Texts());
        Assert.Empty(port.Commands);
    });

    [Fact]
    public void An_item_released_over_the_world_is_dropped_and_one_released_over_another_retail_window_is_not() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var apple = host.Cells[0];
        var outside = new Avalonia.Point(-50, -50);

        host.Drag.Target = ItemDropTarget.Elsewhere;
        DragOut(host.Host, apple, outside);
        Assert.Empty(port.Commands);

        host.Drag.Target = ItemDropTarget.World;
        DragOut(host.Host, apple, outside);
        Assert.Equal(new[] { $"drop 0x{InventorySample.Apple:X8}" }, port.Commands);
    });

    [Fact]
    public void A_drag_that_leaves_the_window_goes_to_retail_when_retail_takes_it() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        host.Drag.Target = ItemDropTarget.World;
        host.Drag.TakeHandOff = true;

        DragOut(host.Host, host.Cells[0], new Avalonia.Point(-50, -50));

        // Other LegACEy windows (the Vault) were told which item the drag carried, so they could show where it would go.
        Assert.Equal(new uint[] { InventorySample.Apple }, host.Drag.ItemsShown);
        // Retail drags the item from there on and drops it; our icon is gone and our release sends nothing.
        Assert.Equal(new[] { InventorySample.Apple }, host.Drag.HandedOff);
        Assert.Equal(0, host.Drag.IconsOpen);
        Assert.Empty(port.Commands);
    });

    private static void DragOut(AvaloniaPanel host, Control from, Avalonia.Point to)
    {
        var start = InventoryDriver.Centre(host, from);
        host.PointerDown(start.X, start.Y, Avalonia.Input.KeyModifiers.None);
        host.PointerMove(start.X + 10, start.Y);
        host.PointerMove(to.X, to.Y);
        host.PointerUp(to.X, to.Y);
        InventoryDriver.Tick(host);
    }

    [Fact]
    public void Clicking_an_item_selects_it_and_a_second_click_on_it_in_time_uses_it() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var apple = host.Cells[0];
        var select = $"select 0x{InventorySample.Apple:X8}";

        InventoryDriver.Press(host.Host, apple);
        Assert.Equal(new[] { select }, port.Commands);
        InventoryDriver.Press(host.Host, apple);
        Assert.Equal(new[] { select, $"use 0x{InventorySample.Apple:X8}" }, port.Commands);
    });

    [Fact]
    public void Dropping_on_a_grid_cell_moves_the_item_to_that_container_and_slot() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var cells = host.Cells;

        // The scroll sits in slot 4 of the main pack; it goes to the empty slot 10. Its icon rides the pointer until the release.
        InventoryDriver.DragTo(host.Host, cells[4], cells[10]);
        Assert.Equal(1, host.Drag.IconsOpen);
        InventoryDriver.Release(host.Host, cells[10]);

        Assert.Equal(0, host.Drag.IconsOpen);
        Assert.Equal(new[] { $"move 0x{InventorySample.Scroll:X8} to 0x{InventorySample.Character:X8} slot 10" }, port.Commands);
    });

    [Fact]
    public void Dropping_on_a_pack_moves_the_item_into_its_first_free_slot() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var potions = InventoryDriver.PackSlot(host.Host, InventorySample.Potions);

        // The potions pack holds seventeen items in slots 0 to 16, so the scroll goes to slot 17.
        InventoryDriver.Drop(host.Host, host.Cells[4], potions);

        Assert.Equal(new[] { $"move 0x{InventorySample.Scroll:X8} to 0x{InventorySample.Potions:X8} slot 17" }, port.Commands);
    });

    [Fact]
    public void Dropping_on_an_equipment_slot_wields_the_item_there() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var head = Slot(host, PaperdollSlot.Head);

        InventoryDriver.Drop(host.Host, host.Cells[9], head);

        Assert.Equal(new[] { $"wield 0x{InventorySample.Cap:X8} to Head" }, port.Commands);
    });

    [Fact]
    public void A_drag_from_our_inventory_shows_its_icon_without_retail_s_drop_indicator() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);

        InventoryDriver.DragTo(host.Host, host.Cells[0], host.Cells[10]);

        // Retail's panel accepts nothing from our drags, so its indicator must not light for them.
        Assert.Equal(new[] { false }, host.Drag.RetailIndicators);
        InventoryDriver.Release(host.Host, host.Cells[10]);
    });

    [Fact]
    public void A_refused_move_leaves_the_window_showing_the_ports_unchanged_state() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var cells = host.Cells;

        // The move is sent, and the port pushes no change: the scroll stays where it was.
        InventoryDriver.Drop(host.Host, cells[4], cells[10]);

        Assert.Single(port.Commands);
        Assert.Equal(InventorySample.Scroll, InventoryDriver.Id(host.Cells[4]).ItemId);
        Assert.Equal(0u, InventoryDriver.Id(host.Cells[10]).ItemId);
    });

    [Fact]
    public void An_illegal_equipment_drop_shows_red_and_sends_nothing() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var weapon = Slot(host, PaperdollSlot.Weapon);

        // A cap can't go in the weapon slot.
        InventoryDriver.DragTo(host.Host, host.Cells[9], weapon);
        var indicator = Assert.Single(host.Host.Content.GetVisualDescendants().OfType<Border>(), border => border.Name == "DropIndicator");
        Assert.True(indicator.IsVisible);
        Assert.Same(DerethPalette.InvalidBrush, indicator.BorderBrush);
        InventoryDriver.Release(host.Host, weapon);

        Assert.Empty(port.Commands);
    });

    [Fact]
    public void A_side_pack_dropped_on_another_side_pack_is_reordered_to_its_position() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);

        // Side packs are numbered on their own: the potions pack is the second side pack, so the sack moves to position 1.
        InventoryDriver.Drop(host.Host, InventoryDriver.PackSlot(host.Host, InventorySample.Sack), InventoryDriver.PackSlot(host.Host, InventorySample.Potions));

        Assert.Equal(new[] { $"move 0x{InventorySample.Sack:X8} to 0x{InventorySample.Character:X8} slot 1" }, port.Commands);
    });

    [Fact]
    public void An_equipped_item_dropped_on_a_grid_cell_is_unequipped_into_that_cell() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);

        InventoryDriver.Drop(host.Host, Slot(host, PaperdollSlot.Head), host.Cells[10]);

        Assert.Equal(new[] { $"move 0x{InventorySample.Helm:X8} to 0x{InventorySample.Character:X8} slot 10" }, port.Commands);
    });

    [Fact]
    public void A_stack_dropped_on_a_stack_of_another_kind_is_moved_into_its_cell_not_merged() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);

        // An apple on a scroll: both stack, but they are different kinds, so the server places the apple in the scroll's cell.
        InventoryDriver.Drop(host.Host, host.Cells[0], host.Cells[4]);

        Assert.Equal(new[] { $"move 0x{InventorySample.Apple:X8} to 0x{InventorySample.Character:X8} slot 4" }, port.Commands);
    });

    [Fact]
    public void A_stack_dropped_on_a_stack_of_the_same_kind_merges_into_it() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        // A second apple, in slot 5: the same name and type as the sample's apple in slot 0, so the drop merges the two.
        port.Push(InventorySample.Snapshot(extra: new[] { InventorySample.AppleStack(0x80000200, slot: 5, stack: 20) }));
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);

        InventoryDriver.Drop(host.Host, host.Cells[0], host.Cells[5]);

        Assert.Equal(new[] { $"merge 0x{InventorySample.Apple:X8} into 0x{0x80000200u:X8}" }, port.Commands);
    });

    [Fact]
    public void A_retail_item_from_another_window_shows_the_indicator_and_dropped_on_a_cell_moves_into_it() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        const uint CorpseItem = 0x90000001; // not in our snapshot: it lies in another retail window
        var target = (IRetailItemDropTarget)host.Window;
        var empty = host.Cells[10];
        var over = InventoryDriver.Centre(host.Host, empty);

        target.RetailDragOver(CorpseItem, "Bow", over);
        var indicator = host.Host.Content.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DropIndicator");
        Assert.True(indicator.IsVisible);

        Assert.True(target.RetailDrop(CorpseItem, "Bow", over));
        Assert.Equal(new[] { $"move 0x{CorpseItem:X8} to 0x{InventorySample.Character:X8} slot 10" }, port.Commands);
    });

    [Fact]
    public void A_vault_withdraw_over_the_window_lights_the_cell_or_pack_under_it_but_not_a_paperdoll_slot() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var zone = (IInventoryDropZone)host.Window;
        var indicator = host.Host.Content.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DropIndicator");

        zone.WithdrawDragOver(InventoryDriver.Centre(host.Host, host.Cells[10]));
        Assert.True(indicator.IsVisible);
        Assert.Same(DerethPalette.GoldBrush, indicator.BorderBrush);

        zone.WithdrawDragOver(InventoryDriver.Centre(host.Host, Slot(host, PaperdollSlot.Head)));
        Assert.False(indicator.IsVisible);

        zone.WithdrawDragOver(InventoryDriver.Centre(host.Host, host.Cells[10]));
        zone.WithdrawDragOver(null);
        Assert.False(indicator.IsVisible);

        // released there, the withdrawal goes to that cell of the open pack; over the paperdoll, wherever the server puts it
        Assert.Equal((InventorySample.Character, 10), zone.WithdrawPlaceAt(InventoryDriver.Centre(host.Host, host.Cells[10])));
        Assert.Null(zone.WithdrawPlaceAt(InventoryDriver.Centre(host.Host, Slot(host, PaperdollSlot.Head))));
    });

    [Fact]
    public void A_retail_item_from_another_window_is_wielded_where_it_fits_and_refused_where_it_does_not() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        // A helm lying in another window: not in our snapshot, so its locations come from the world, as the port reads them.
        const uint CorpseHelm = 0x90000002;
        port.WorldLocations[CorpseHelm] = 0x00000001; // HeadWear
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var target = (IRetailItemDropTarget)host.Window;
        var indicator = host.Host.Content.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DropIndicator");
        var overHead = InventoryDriver.Centre(host.Host, Slot(host, PaperdollSlot.Head));
        var overShield = InventoryDriver.Centre(host.Host, Slot(host, PaperdollSlot.Shield));

        target.RetailDragOver(CorpseHelm, "Helm", overHead);
        Assert.Same(DerethPalette.GoldBrush, indicator.BorderBrush);
        Assert.True(target.RetailDrop(CorpseHelm, "Helm", overHead));
        Assert.Equal(new[] { $"wield 0x{CorpseHelm:X8} to Head" }, port.Commands);

        // A helm cannot go in the shield slot: the indicator is red, and the drop sends nothing.
        target.RetailDragOver(CorpseHelm, "Helm", overShield);
        Assert.Same(DerethPalette.InvalidBrush, indicator.BorderBrush);
        Assert.False(target.RetailDrop(CorpseHelm, "Helm", overShield));
        Assert.Single(port.Commands);
    });

    [Fact]
    public void A_dragged_item_fades_in_its_slot_until_it_is_released() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var start = InventoryDriver.Centre(host.Host, host.Cells[0]);
        var other = host.Cells[1].Opacity;

        host.Host.PointerDown(start.X, start.Y);
        host.Host.PointerMove(start.X + 10, start.Y);
        Assert.True(host.Cells[0].Opacity < 1);
        Assert.Equal(other, host.Cells[1].Opacity);

        host.Host.PointerUp(start.X + 10, start.Y);
        Assert.Equal(1, host.Cells[0].Opacity);
    });

    [Fact]
    public void A_press_that_moves_past_the_threshold_and_is_lost_sends_nothing_and_drops_its_icon() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var start = InventoryDriver.Centre(host.Host, host.Cells[0]);

        // The focus is lost mid-drag: the release comes from nowhere in the window.
        host.Host.PointerDown(start.X, start.Y);
        host.Host.PointerMove(start.X + 10, start.Y);
        Assert.Equal(1, host.Drag.IconsOpen);
        host.Host.PointerUp(-1, -1);

        Assert.Empty(port.Commands);
        Assert.Equal(0, host.Drag.IconsOpen);
    });

    [Fact]
    public void A_press_released_away_from_where_it_began_without_a_drag_is_not_a_click() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, InventoryLayout.Vertical, showSlots: true);
        var start = InventoryDriver.Centre(host.Host, host.Cells[0]);

        host.Host.PointerDown(start.X, start.Y);
        host.Host.PointerUp(-1, -1);

        Assert.Empty(port.Commands);
    });

    private static DerethSlot Slot(InventoryHost host, PaperdollSlot slot) =>
        InventoryDriver.SlotOrNull(host.Host, slot) ?? throw new InvalidOperationException($"No {slot} slot is drawn.");

    /// <summary>The window in a headless panel of the given size, in the Dereth theme, with the port's current snapshot drawn.</summary>
    private sealed class InventoryHost : IDisposable
    {
        private readonly InventoryWindow _window;

        public InventoryHost(FakeInventoryPort port, InventoryLayout layout, bool showSlots, int width = 360, int height = 530)
        {
            InventoryWindow? window = null;
            Host = AvaloniaPanel.Create(() => window = new InventoryWindow(port, new InventorySample.NoArt(), new InventorySettings(layout, showSlots), Drag), width, height);
            Host.ApplyTheme(new DerethClientTheme());
            _window = window!;
            InventoryDriver.Tick(Host);
        }

        public AvaloniaPanel Host { get; }
        public InventoryWindow Window => _window;
        /// <summary>The drag host the window shows its icons through: <see cref="FakeItemDragHost.IconsOpen"/> counts the icons up.</summary>
        public FakeItemDragHost Drag { get; } = new();

        /// <summary>The cells of the open container's grid, in reading order.</summary>
        public DerethSlot[] Cells => InventoryDriver.Slots(Host).Where(slot => InventoryDriver.Id(slot).Place == SlotPlace.Cell).ToArray();

        public string[] Texts() => Host.Content.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray();

        public void Dispose()
        {
            _window.Dispose();
            Host.Dispose();
        }
    }
}

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
        using var host = new InventoryHost(port, showSlots: true);

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

        // Over the limit the reading keeps its plain colour, as retail's does; only the meter's bands colour the load.
        port.Push(InventorySample.Snapshot(burden: 6000));
        InventoryDriver.Tick(host.Host);
        var reading = Assert.Single(host.Host.Content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "111%");
        Assert.Same(DerethPalette.TextBrush, reading.Foreground);
    });

    [Fact]
    public void The_slots_toggle_hides_and_shows_the_armour_slots_and_reports_the_choice() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
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
        using var host = new InventoryHost(port, showSlots: true);
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
    public void Clicking_a_pack_shows_its_contents_without_asking_the_server() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);

        // The server answers a use of a carried pack with only "use done" (a busy cursor); retail switches packs locally.
        InventoryDriver.Press(host.Host, InventoryDriver.PackSlot(host.Host, InventorySample.Potions));
        Assert.Contains("Contents of Potions", host.Texts());
        Assert.Equal(InventorySample.BluePotion, InventoryDriver.Id(host.Cells[0]).ItemId);

        InventoryDriver.Press(host.Host, InventoryDriver.PackSlot(host.Host, InventorySample.Character));
        Assert.Contains("Contents of Main Pack", host.Texts());
        // The main pack is the character: clicking it selects them, as retail's does, so an item in use can target them.
        Assert.Equal(new[] { $"select 0x{InventorySample.Character:X8}" }, port.Commands);
    });

    [Fact]
    public void Clicking_a_focus_selects_it_and_outlines_it_without_opening_it() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot(focus: true));
        using var host = new InventoryHost(port, showSlots: true);

        InventoryDriver.Press(host.Host, InventoryDriver.PackSlot(host.Host, InventorySample.Focus));
        Assert.Equal(new[] { $"select 0x{InventorySample.Focus:X8}" }, port.Commands);
        Assert.Contains("Contents of Main Pack", host.Texts());

        port.Push(InventorySample.Snapshot(focus: true, selected: InventorySample.Focus));
        InventoryDriver.Tick(host.Host);
        Assert.True(InventoryDriver.PackSlot(host.Host, InventorySample.Focus).Selected);
    });

    [Fact]
    public void Only_a_container_holding_something_has_a_fill_line() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot(focus: true));
        using var host = new InventoryHost(port, showSlots: true);
        InventoryDriver.Tick(host.Host);

        bool HasLine(uint pack) => ((Panel)InventoryDriver.PackSlot(host.Host, pack).Parent!).Children[1] is Border;
        Assert.True(HasLine(InventorySample.Potions));
        Assert.False(HasLine(InventorySample.Sack));
        Assert.False(HasLine(InventorySample.Focus));
    });

    [Fact]
    public void The_main_pack_drags_out_to_retail_as_the_character_and_never_moves_inside_the_window() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        var main = InventoryDriver.PackSlot(host.Host, InventorySample.Character);

        InventoryDriver.Drop(host.Host, main, InventoryDriver.PackSlot(host.Host, InventorySample.Potions));
        Assert.Empty(InventoryDriver.Sent(port));

        // Retail takes it (a shortcut on the bar selects the character).
        host.Drag.Target = ItemDropTarget.World;
        host.Drag.TakeHandOff = true;
        DragOut(host.Host, main, new Avalonia.Point(-50, -50));
        Assert.Equal(new[] { InventorySample.Character }, host.Drag.HandedOff);
        Assert.Empty(InventoryDriver.Sent(port));
    });

    [Fact]
    public void An_item_released_over_the_world_is_dropped_and_one_released_over_another_retail_window_is_not() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        var apple = host.Cells[0];
        var outside = new Avalonia.Point(-50, -50);

        host.Drag.Target = ItemDropTarget.Elsewhere;
        DragOut(host.Host, apple, outside);
        Assert.Empty(InventoryDriver.Sent(port));

        host.Drag.Target = ItemDropTarget.World;
        DragOut(host.Host, apple, outside);
        Assert.Equal(new[] { $"drop 0x{InventorySample.Apple:X8}" }, InventoryDriver.Sent(port));
    });

    [Fact]
    public void A_side_pack_released_over_the_world_is_dropped_but_the_main_pack_is_not() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        var outside = new Avalonia.Point(-50, -50);
        host.Drag.Target = ItemDropTarget.World;

        DragOut(host.Host, InventoryDriver.PackSlot(host.Host, InventorySample.Sack), outside);
        Assert.Equal(new[] { $"drop 0x{InventorySample.Sack:X8}" }, InventoryDriver.Sent(port));

        DragOut(host.Host, InventoryDriver.PackSlot(host.Host, InventorySample.Character), outside);
        Assert.Single(InventoryDriver.Sent(port));
    });

    [Fact]
    public void A_side_pack_dropped_on_the_ground_goes_back_into_an_empty_side_slot() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        host.Drag.Target = ItemDropTarget.World;
        DragOut(host.Host, InventoryDriver.PackSlot(host.Host, InventorySample.Sack), new Avalonia.Point(-50, -50));

        // The pack lies on the ground now: it is no longer in the snapshot, and its side slot is empty.
        port.Push(InventorySample.Snapshot(sackOnGround: true));
        var empty = InventoryDriver.Slots(host.Host).Single(slot =>
        {
            var id = InventoryDriver.Id(slot);
            return id.Place == SlotPlace.Pack && id.Container == 0 && id.SlotIndex == 0;
        });
        var target = (IRetailItemDropTarget)host.Window;

        Assert.True(target.RetailDrop(InventorySample.Sack, "Sack", InventoryDriver.Centre(host.Host, empty)));
        Assert.Equal($"move 0x{InventorySample.Sack:X8} to 0x{InventorySample.Character:X8} slot 0", InventoryDriver.Sent(port).Last());
    });

    [Fact]
    public void A_drag_that_leaves_the_window_goes_to_retail_when_retail_takes_it() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        host.Drag.Target = ItemDropTarget.World;
        host.Drag.TakeHandOff = true;

        DragOut(host.Host, host.Cells[0], new Avalonia.Point(-50, -50));

        // Other LegACEy windows (the Vault) were told which item the drag carried, so they could show where it would go.
        Assert.Equal(new uint[] { InventorySample.Apple }, host.Drag.ItemsShown);
        // Retail drags the item from there on and drops it; our icon is gone and our release sends nothing.
        Assert.Equal(new[] { InventorySample.Apple }, host.Drag.HandedOff);
        Assert.Equal(0, host.Drag.IconsOpen);
        Assert.Empty(InventoryDriver.Sent(port));
    });

    [Fact]
    public void Ctrl_and_shift_clicks_pick_a_set_that_drags_into_a_pack_but_never_to_retail() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        host.Drag.Target = ItemDropTarget.World;
        host.Drag.TakeHandOff = true;
        // The main pack's cells are its slots: apple 0, scroll 4, sword 7, cap 9.
        var apple = host.Cells[0];

        Click(host.Host, apple, Avalonia.Input.KeyModifiers.None);
        Click(host.Host, host.Cells[7], Avalonia.Input.KeyModifiers.Shift);
        Click(host.Host, host.Cells[4], Avalonia.Input.KeyModifiers.Control);

        // Apple to sword, less the scroll: picked cells are outlined, the rest fade.
        Assert.Equal(new[] { true, false, true, false }, new[] { 0, 4, 7, 9 }.Select(slot => host.Cells[slot].Selected));
        Assert.True(host.Cells[9].Opacity < 1);

        InventoryDriver.Drop(host.Host, host.Cells[0], InventoryDriver.PackSlot(host.Host, InventorySample.Sack));
        Assert.Equal(new[] { $"move 0x{InventorySample.Apple:X8} to 0x{InventorySample.Sack:X8} slot 0", $"move 0x{InventorySample.Sword:X8} to 0x{InventorySample.Sack:X8} slot 1" },
            InventoryDriver.Sent(port));

        // Out over the world, the set is offered only to a LegACEy window: retail never takes it and nothing drops.
        DragOut(host.Host, host.Cells[7], new Avalonia.Point(-50, -50));
        Assert.Empty(host.Drag.HandedOff);
        Assert.Equal(new[] { InventorySample.Apple, InventorySample.Sword }, Assert.Single(host.Drag.DeliveredSets));
        Assert.Equal(2, InventoryDriver.Sent(port).Length);
        Assert.Null(host.Host.LastError);
    });

    [Fact]
    public void A_set_picked_in_a_side_pack_drags_back_into_the_main_pack() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot(openContainer: InventorySample.Potions));
        using var host = new InventoryHost(port, showSlots: true);

        // The potions pack's cells: blue potion 0, yellow potion 5.
        Click(host.Host, host.Cells[0], Avalonia.Input.KeyModifiers.Control);
        Click(host.Host, host.Cells[5], Avalonia.Input.KeyModifiers.Control);

        // The main pack holds slots 0, 4, 7 and 9, so the set takes its first free slots, 1 and 2.
        InventoryDriver.Drop(host.Host, host.Cells[0], InventoryDriver.PackSlot(host.Host, InventorySample.Character));
        Assert.Equal(new[] { $"move 0x{InventorySample.BluePotion:X8} to 0x{InventorySample.Character:X8} slot 1", $"move 0x{InventorySample.YellowPotion:X8} to 0x{InventorySample.Character:X8} slot 2" },
            InventoryDriver.Sent(port));
        Assert.Null(host.Host.LastError);
    });

    private static void Click(AvaloniaPanel host, Control control, Avalonia.Input.KeyModifiers modifiers)
    {
        var point = InventoryDriver.Centre(host, control);
        host.PointerDown(point.X, point.Y, modifiers);
        host.PointerUp(point.X, point.Y);
        InventoryDriver.Tick(host);
    }

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
    public void The_item_under_the_pointer_is_reported_for_retails_target_cursor() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        var apple = InventoryDriver.Centre(host.Host, host.Cells[0]);

        host.Host.PointerMove(apple.X, apple.Y);
        Assert.Equal(InventorySample.Apple, port.Hovered);
        // Over no item, the pointer reports none; a hover is never a retail command.
        host.Host.PointerMove(-50, -50);
        Assert.Equal(0u, port.Hovered);
        Assert.Empty(port.Commands);
    });

    [Fact]
    public void Clicking_an_item_selects_it_and_a_second_click_on_it_in_time_uses_it() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        var apple = host.Cells[0];
        var select = $"select 0x{InventorySample.Apple:X8}";

        InventoryDriver.Press(host.Host, apple);
        Assert.Equal(new[] { select }, port.Commands);
        InventoryDriver.Press(host.Host, apple);
        Assert.Equal(new[] { select, $"use 0x{InventorySample.Apple:X8}" }, port.Commands);
    });

    [Fact]
    public void Right_clicking_an_item_assesses_it_and_an_empty_cell_sends_nothing() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);

        InventoryDriver.RightClick(host.Host, host.Cells[0]);
        InventoryDriver.RightClick(host.Host, host.Cells[10]);

        Assert.Equal(new[] { $"assess 0x{InventorySample.Apple:X8}" }, port.Commands);
    });

    [Fact]
    public void Dropping_on_a_grid_cell_moves_the_item_to_that_container_and_slot() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        var cells = host.Cells;

        // The scroll sits in slot 4 of the main pack; it goes to the empty slot 10. Its icon rides the pointer until the release.
        InventoryDriver.DragTo(host.Host, cells[4], cells[10]);
        Assert.Equal(1, host.Drag.IconsOpen);
        InventoryDriver.Release(host.Host, cells[10]);

        Assert.Equal(0, host.Drag.IconsOpen);
        Assert.Equal(new[] { $"move 0x{InventorySample.Scroll:X8} to 0x{InventorySample.Character:X8} slot 10" }, InventoryDriver.Sent(port));
    });

    [Fact]
    public void A_lifted_item_fades_and_is_outlined_as_selected_until_it_is_put_down() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        Assert.False(host.Cells[4].Selected);
        var other = host.Cells[5].Opacity;

        InventoryDriver.DragTo(host.Host, host.Cells[4], host.Cells[4]);
        Assert.True(host.Cells[4].Selected);
        Assert.True(host.Cells[4].Opacity < 1);
        Assert.Equal(other, host.Cells[5].Opacity);
        // Lifting it selects it, so the item selected before loses its outline.
        Assert.Equal(new[] { $"select 0x{InventorySample.Scroll:X8}" }, port.Commands);
        InventoryDriver.Release(host.Host, host.Cells[4]);

        Assert.False(host.Cells[4].Selected);
        Assert.Equal(1, host.Cells[4].Opacity);
    });

    [Fact]
    public void Dropping_on_a_pack_moves_the_item_into_its_first_free_slot() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        var potions = InventoryDriver.PackSlot(host.Host, InventorySample.Potions);

        // The potions pack holds seventeen items in slots 0 to 16, so the scroll goes to slot 17.
        InventoryDriver.Drop(host.Host, host.Cells[4], potions);

        Assert.Equal(new[] { $"move 0x{InventorySample.Scroll:X8} to 0x{InventorySample.Potions:X8} slot 17" }, InventoryDriver.Sent(port));
    });

    // Armour doesn't swap: the worn helm stays, the drop sends only the wield, and the server refuses it until the helm comes off.
    [Fact]
    public void Dropping_on_an_equipment_slot_wields_the_item_there() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        var head = Slot(host, PaperdollSlot.Head);

        InventoryDriver.Drop(host.Host, host.Cells[9], head);

        Assert.Equal(new[] { $"wield 0x{InventorySample.Cap:X8} to Head" }, InventoryDriver.Sent(port));
    });

    [Fact]
    public void A_bow_dropped_on_the_weapon_slot_puts_the_worn_sword_in_the_main_pack_first() => RenderThread.Run(() =>
    {
        const uint bow = 0x90000003;
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        port.WorldLocations[bow] = 0x00400000; // MissileWeapon: not the sword's MeleeWeapon location, but the same slot
        using var host = new InventoryHost(port, showSlots: true);
        var target = (IRetailItemDropTarget)host.Window;

        Assert.True(target.RetailDrop(bow, "Bow", InventoryDriver.Centre(host.Host, Slot(host, PaperdollSlot.Weapon))));

        Assert.Equal(new[] { $"move 0x{InventorySample.WieldedSword:X8} to 0x{InventorySample.Character:X8} slot 1", $"wield 0x{bow:X8} to Weapon" },
            port.Commands);
    });

    [Fact]
    public void Dropping_on_the_doll_wields_the_item_in_its_slot_even_while_that_slot_is_hidden() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: false);
        var doll = host.Host.Content.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DollArea");

        InventoryDriver.Drop(host.Host, host.Cells[9], doll);

        Assert.Equal(new[] { $"wield 0x{InventorySample.Cap:X8} to Head" }, InventoryDriver.Sent(port));
    });

    [Fact]
    public void Dragging_an_item_over_the_doll_lights_the_doll() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: false);
        var doll = host.Host.Content.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DollArea");
        var indicator = host.Host.Content.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DropIndicator");

        InventoryDriver.DragTo(host.Host, host.Cells[9], doll);

        Assert.True(indicator.IsVisible);
        Assert.Equal(Avalonia.VisualExtensions.TranslatePoint(doll, default, host.Host.Content), Avalonia.VisualExtensions.TranslatePoint(indicator, default, host.Host.Content));
        Assert.Equal(doll.Bounds.Size, indicator.Bounds.Size);
        InventoryDriver.Release(host.Host, doll);
    });

    [Fact]
    public void Dragging_an_item_over_the_3D_doll_outlines_the_body_parts_it_would_cover_instead() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: false);
        host.Window.SetDoll(new PaperdollView(new SilentLooks(), string.Empty) { Still = true });
        InventoryDriver.Tick(host.Host);
        var doll = host.Host.Content.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DollArea");
        var model = host.Host.Content.GetVisualDescendants().OfType<ModelView>().Single();

        InventoryDriver.DragTo(host.Host, host.Cells[9], doll);

        // The cap goes on the head (part 16), in gold; no frame round the doll.
        Assert.Equal(new[] { 16 }, model.Outlined);
        // What the world says it covers counts over where it is wielded: a hood that reaches the chest outlines the chest too.
        InventoryDriver.Release(host.Host, host.Cells[9]);
        port.Coverage[InventorySample.Cap] = 0x4000 | 0x0400;
        InventoryDriver.DragTo(host.Host, host.Cells[9], doll);
        Assert.Equal(new[] { 9, 16 }, model.Outlined);
        Assert.Equal(DerethPalette.Gold.ToUInt32(), model.OutlineColour);
        Assert.False(host.Host.Content.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "DropIndicator").IsVisible);
        InventoryDriver.Release(host.Host, doll);
        Assert.Empty(model.Outlined);
    });

    /// <summary>A look source that never has a look: the doll stays without a model, which is all an outline needs.</summary>
    private sealed class SilentLooks : ILookSource
    {
        public bool Knows(uint objectId) => true;
        public void Request(uint objectId, Action<LegACEy.Client.GameArt.CharacterAppearance?, string> reply) { }
        public IDisposable WhenChanged(uint objectId, Action changed) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public void A_drag_from_our_inventory_shows_its_icon_without_retail_s_drop_indicator() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);

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
        using var host = new InventoryHost(port, showSlots: true);
        var cells = host.Cells;

        // The move is sent, and the port pushes no change: the scroll stays where it was.
        InventoryDriver.Drop(host.Host, cells[4], cells[10]);

        Assert.Single(InventoryDriver.Sent(port));
        Assert.Equal(InventorySample.Scroll, InventoryDriver.Id(host.Cells[4]).ItemId);
        Assert.Equal(0u, InventoryDriver.Id(host.Cells[10]).ItemId);
    });

    [Fact]
    public void An_illegal_equipment_drop_shows_red_and_sends_nothing() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        var weapon = Slot(host, PaperdollSlot.Weapon);

        // A cap can't go in the weapon slot.
        InventoryDriver.DragTo(host.Host, host.Cells[9], weapon);
        var indicator = Assert.Single(host.Host.Content.GetVisualDescendants().OfType<Border>(), border => border.Name == "DropIndicator");
        Assert.True(indicator.IsVisible);
        Assert.Same(DerethPalette.InvalidBrush, indicator.BorderBrush);
        InventoryDriver.Release(host.Host, weapon);

        Assert.Empty(InventoryDriver.Sent(port));
    });

    [Fact]
    public void A_side_pack_dropped_on_another_side_pack_is_reordered_to_its_position() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);

        // Side packs are numbered on their own: the potions pack is the second side pack, so the sack moves to position 1.
        InventoryDriver.Drop(host.Host, InventoryDriver.PackSlot(host.Host, InventorySample.Sack), InventoryDriver.PackSlot(host.Host, InventorySample.Potions));

        Assert.Equal(new[] { $"move 0x{InventorySample.Sack:X8} to 0x{InventorySample.Character:X8} slot 1" }, port.Commands);
    });

    [Fact]
    public void An_equipped_item_dropped_on_a_grid_cell_is_unequipped_into_that_cell() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);

        InventoryDriver.Drop(host.Host, Slot(host, PaperdollSlot.Head), host.Cells[10]);

        Assert.Equal(new[] { $"move 0x{InventorySample.Helm:X8} to 0x{InventorySample.Character:X8} slot 10" }, InventoryDriver.Sent(port));
    });

    [Fact]
    public void A_stack_dropped_on_a_stack_of_another_kind_is_moved_into_its_cell_not_merged() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);

        // An apple on a scroll: both stack, but they are different kinds, so the server places the apple in the scroll's cell.
        InventoryDriver.Drop(host.Host, host.Cells[0], host.Cells[4]);

        Assert.Equal(new[] { $"move 0x{InventorySample.Apple:X8} to 0x{InventorySample.Character:X8} slot 4" }, InventoryDriver.Sent(port));
    });

    [Fact]
    public void A_stack_dropped_on_a_stack_of_the_same_kind_merges_into_it() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        // A second apple, in slot 5: the same name and type as the sample's apple in slot 0, so the drop merges the two.
        port.Push(InventorySample.Snapshot(extra: new[] { InventorySample.AppleStack(0x80000200, slot: 5, stack: 20) }));
        using var host = new InventoryHost(port, showSlots: true);

        InventoryDriver.Drop(host.Host, host.Cells[0], host.Cells[5]);

        Assert.Equal(new[] { $"merge 0x{InventorySample.Apple:X8} into 0x{0x80000200u:X8}" }, InventoryDriver.Sent(port));
    });

    [Fact]
    public void A_retail_item_from_another_window_shows_the_indicator_and_dropped_on_a_cell_moves_into_it() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
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
        using var host = new InventoryHost(port, showSlots: true);
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
        using var host = new InventoryHost(port, showSlots: true);
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
    public void A_press_that_moves_past_the_threshold_and_is_lost_sends_nothing_and_drops_its_icon() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
        var start = InventoryDriver.Centre(host.Host, host.Cells[0]);

        // The focus is lost mid-drag: the release comes from nowhere in the window.
        host.Host.PointerDown(start.X, start.Y);
        host.Host.PointerMove(start.X + 10, start.Y);
        Assert.Equal(1, host.Drag.IconsOpen);
        host.Host.PointerUp(-1, -1);

        Assert.Empty(InventoryDriver.Sent(port));
        Assert.Equal(0, host.Drag.IconsOpen);
    });

    [Fact]
    public void A_press_released_away_from_where_it_began_without_a_drag_is_not_a_click() => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var host = new InventoryHost(port, showSlots: true);
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

        /// <param name="width">The panel's width; by default the window's fixed width.</param>
        /// <param name="height">The panel's height; by default the window's default height.</param>
        public InventoryHost(FakeInventoryPort port, bool showSlots, int? width = null, int? height = null)
        {
            InventoryWindow? window = null;
            Host = AvaloniaPanel.Create(() => window = new InventoryWindow(port, new InventorySample.NoArt(), new InventorySettings(showSlots), Drag),
                width ?? 300, height ?? 794);
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

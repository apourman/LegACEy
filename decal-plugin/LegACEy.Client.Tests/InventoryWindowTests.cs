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

    private static DerethSlot Slot(InventoryHost host, PaperdollSlot slot) =>
        InventoryDriver.SlotOrNull(host.Host, slot) ?? throw new InvalidOperationException($"No {slot} slot is drawn.");

    /// <summary>The window in a headless panel of the given size, in the Dereth theme, with the port's current snapshot drawn.</summary>
    private sealed class InventoryHost : IDisposable
    {
        private readonly InventoryWindow _window;

        public InventoryHost(FakeInventoryPort port, InventoryLayout layout, bool showSlots, int width = 360, int height = 530)
        {
            InventoryWindow? window = null;
            Host = AvaloniaPanel.Create(() => window = new InventoryWindow(port, new InventorySample.NoArt(), new InventorySettings(layout, showSlots)), width, height);
            Host.ApplyTheme(new DerethClientTheme());
            _window = window!;
            InventoryDriver.Tick(Host);
        }

        public AvaloniaPanel Host { get; }
        public InventoryWindow Window => _window;

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

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
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

        Assert.Equal(InventorySample.Helm, host.Slot(PaperdollSlot.Head).ItemId);
        // The coat is one item in three slots.
        Assert.Equal(InventorySample.Coat, host.Slot(PaperdollSlot.Chest).ItemId);
        Assert.Equal(InventorySample.Coat, host.Slot(PaperdollSlot.UpperArms).ItemId);
        Assert.Equal(InventorySample.Coat, host.Slot(PaperdollSlot.LowerArms).ItemId);
        // A dual-wielded off-hand weapon is in the shield slot.
        Assert.Equal(InventorySample.Dagger, host.Slot(PaperdollSlot.Shield).ItemId);
        // An empty slot shows its glyph and no item.
        Assert.Equal(0u, host.Slot(PaperdollSlot.Neck).ItemId);
        Assert.Null(host.Host.LastError);
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
        Assert.Equal(InventorySample.Apple, host.Cells[0].ItemId);

        // Something else opens a side pack. The grid follows: its own capacity, empties included, in slot-index order.
        port.Push(InventorySample.Snapshot(openContainer: InventorySample.Potions));
        InventoryDriver.Tick(host.Host);
        var cells = host.Cells;
        Assert.Equal(24, cells.Length);
        Assert.Equal(Enumerable.Range(0, 24), cells.Select(cell => cell.SlotIndex));
        Assert.All(cells, cell => Assert.Equal(InventorySample.Potions, cell.Container));
        Assert.Equal(InventorySample.BluePotion, cells[0].ItemId);
        Assert.Equal(InventorySample.YellowPotion, cells[5].ItemId);
        Assert.Equal(0u, cells[1].ItemId);
        Assert.Contains("Contents of Potions", host.Texts());
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

    /// <summary>
    /// Every visible control lies inside the window at the minimum and default sizes of each layout, with the full sample drawn,
    /// and the pack grid shows at least one row. Run it again with the Windows fonts (FONTCONFIG_FILE) to check the taller text.
    /// </summary>
    [Theory]
    [InlineData(InventoryLayout.Vertical, 330, 420)]
    [InlineData(InventoryLayout.Vertical, 360, 530)]
    [InlineData(InventoryLayout.Horizontal, 560, 330)]
    [InlineData(InventoryLayout.Horizontal, 640, 400)]
    public void Nothing_spills_outside_the_window_at_the_minimum_and_default_sizes_in_both_layouts(InventoryLayout layout, int width, int height) => RenderThread.Run(() =>
    {
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot(openContainer: InventorySample.Potions, selected: InventorySample.BluePotion));
        using var host = new InventoryHost(port, layout, showSlots: true, width, height);

        Assert.Null(host.Host.LastError);
        AssertNothingOutsideFrame(host.Host);
        var grid = Assert.Single(host.Host.Content.GetVisualDescendants().OfType<DerethSlotGrid>());
        Assert.True(grid.Bounds.Height >= DerethSlotGrid.CellSize, $"The grid is {grid.Bounds.Height} px tall, less than one row.");
    });

    /// <summary>Every visible control lies inside the window, except scrolled-out cells, which the grid's viewport clips.</summary>
    private static void AssertNothingOutsideFrame(AvaloniaPanel host)
    {
        var window = new Rect(host.Content.Bounds.Size);
        foreach (var visual in host.Content.GetVisualDescendants().OfType<Visual>())
        {
            if (!visual.IsEffectivelyVisible || visual.GetVisualAncestors().OfType<ScrollViewer>().Any()) continue;
            var origin = visual.TranslatePoint(default, host.Content);
            if (origin == null) continue;
            var bounds = new Rect(origin.Value, visual.Bounds.Size);
            Assert.True(window.Contains(bounds), $"{visual.GetType().Name} at {bounds} lies outside the window {window}");
        }
    }

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
        public InventorySlot[] Cells => Host.Content.GetVisualDescendants().OfType<InventorySlot>().Where(slot => slot.Place == SlotPlace.Cell).ToArray();

        public InventorySlot Slot(PaperdollSlot slot) => InventoryDriver.SlotOrNull(Host, slot) ?? throw new InvalidOperationException($"No {slot} slot is drawn.");

        public string[] Texts() => Host.Content.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray();

        public void Dispose()
        {
            _window.Dispose();
            Host.Dispose();
        }
    }
}

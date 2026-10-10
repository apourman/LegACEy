using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Inventory;
using LegACEy.Plugin.Vault;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// Item drags between a LegACEy inventory window and the Vault window, through the real windows. The host's routing is the
/// <see cref="ItemDropRouting"/> the client uses over its window bounds: the inventory sits at the origin, the Vault to its right.
/// </summary>
public sealed class InventoryVaultDropTests
{
    private static readonly System.Drawing.Point VaultOrigin = new(380, 0);

    [Fact]
    public void An_inventory_item_released_over_the_vault_is_deposited_as_a_retail_drop_would_be() => RenderThread.Run(() =>
    {
        var relay = new FakeItemDropRelay();
        using var vault = new VaultFixture(null, VaultShellPanel.WindowHeight, new FakeItemDragHost());
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var inventory = AvaloniaPanel.Create(() => new InventoryWindow(port, new InventorySample.NoArt(),
            new InventorySettings(InventoryLayout.Vertical, showSlots: true), new FakeItemDragHost(), relay), 360, 530);
        inventory.ApplyTheme(new DerethClientTheme());
        relay.Areas = new[]
        {
            new DropArea("inventory", inventory.Content, new System.Drawing.Rectangle(0, 0, 360, 530)),
            new DropArea("vault", vault.Window, new System.Drawing.Rectangle(VaultOrigin, new System.Drawing.Size(VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight))),
        };
        InventoryDriver.Tick(inventory);
        var scroll = InventoryDriver.Slots(inventory).Single(slot => InventoryDriver.Id(slot).ItemId == InventorySample.Scroll);
        var over = vault.Center(vault.Cells[10]);
        var release = new System.Drawing.Point(VaultOrigin.X + (int)over.X, VaultOrigin.Y + (int)over.Y);

        // The drag leaves the inventory: the release lands in the Vault's cell, outside the inventory's own panel.
        var start = InventoryDriver.Centre(inventory, scroll);
        inventory.PointerDown(start.X, start.Y, KeyModifiers.None);
        inventory.PointerMove(start.X + 10, start.Y);
        relay.Pointer = release;
        inventory.PointerMove(release.X, release.Y);
        inventory.PointerUp(release.X, release.Y);
        vault.Step(TimeSpan.FromMilliseconds(30));

        Assert.Equal(new[] { (InventorySample.Scroll, "Scroll") }, relay.Delivered);
        Assert.Equal(VaultProtocol.Deposit, vault.Server.Received.Last());
        Assert.True(vault.Client.TransferPending);
        // The inventory sends no command of its own for a drop on another window.
        Assert.Empty(port.Commands);
        Assert.Null(inventory.LastError);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void A_vault_item_released_over_the_inventory_window_withdraws_it_as_over_the_inventory() => RenderThread.Run(() =>
    {
        var drag = new RoutedDragHost();
        using var vault = new VaultFixture(null, VaultShellPanel.WindowHeight, drag);
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var inventory = AvaloniaPanel.Create(() => new InventoryWindow(port, new InventorySample.NoArt(),
            new InventorySettings(InventoryLayout.Vertical, showSlots: true)), 360, 530);
        inventory.ApplyTheme(new DerethClientTheme());
        drag.Areas = new[]
        {
            new DropArea("inventory", inventory.Content, new System.Drawing.Rectangle(VaultOrigin, new System.Drawing.Size(360, 530))),
            new DropArea("vault", vault.Window, new System.Drawing.Rectangle(0, 0, VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight)),
        };
        var start = vault.Center(vault.Cells[1]);
        var overInventory = new System.Drawing.Point(VaultOrigin.X + 180, 265);

        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 30, start.Y);
        drag.Pointer = overInventory;
        vault.Host.PointerMove(overInventory.X, overInventory.Y);
        vault.Host.PointerUp(overInventory.X, overInventory.Y);
        vault.Step(TimeSpan.FromMilliseconds(30));

        Assert.Equal(VaultProtocol.Withdraw, vault.Server.Received.Last());
        Assert.Empty(port.Commands);
        Assert.Null(vault.Host.LastError);
    });

    /// <summary>Like the plugin's drag host: over a LegACEy inventory window, the drop target is the inventory.</summary>
    private sealed class RoutedDragHost : IItemDragHost
    {
        private readonly FakeItemDragHost _icons = new();
        public System.Drawing.Point Pointer { get; set; }
        public IEnumerable<DropArea> Areas { get; set; } = Array.Empty<DropArea>();
        public IDisposable ShowDragIcon(GameImage? icon, int count) => _icons.ShowDragIcon(icon, count);
        public ItemDropTarget DropTargetAtPointer() => ItemDropRouting.InventoryAt(Pointer, Areas) ?? ItemDropTarget.Elsewhere;
    }
}

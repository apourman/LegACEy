using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Inventory;
using LegACEy.Plugin.Vault;
using Xunit;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace LegACEy.Client.Tests;

/// <summary>
/// Item drags between a LegACEy inventory window and the Vault window, through the real windows and the real window manager's hit test.
/// The inventory sits at the origin and the Vault to its right, so a drop on one is outside the other.
/// </summary>
public sealed class InventoryVaultDropTests
{
    private static readonly Point VaultOrigin = new(380, 0);

    [Fact]
    public void An_inventory_item_released_over_the_vault_is_deposited_as_a_retail_drop_would_be() => RenderThread.Run(() =>
    {
        var windows = new WindowManager(new Size(1920, 1080), new MemoryWindowPositionStore(), "Server", "Character");
        var contents = new Dictionary<string, object>();
        var pointer = default(Point);
        var drag = new FakeItemDragHost { Relay = (id, name) => windows.HitTest(pointer) is { } window && ItemDropRouting.DeliverTo(window, Content, pointer, id, name, windows.Scale) };
        using var vault = new VaultFixture(null, VaultShellPanel.WindowHeight, new FakeItemDragHost());
        windows.Open(new WindowDefinition("vault", "Vault", VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight), VaultOrigin);
        contents["vault"] = vault.Window;

        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var inventory = AvaloniaPanel.Create(() => new InventoryWindow(port, new InventorySample.NoArt(),
            new InventorySettings(InventoryLayout.Vertical, showSlots: true), drag), 360, 530);
        inventory.ApplyTheme(new DerethClientTheme());
        windows.Open(new WindowDefinition("inventory", "Inventory", 360, 530), new Point(0, 0));
        contents["inventory"] = inventory.Content;
        InventoryDriver.Tick(inventory);
        var scroll = InventoryDriver.Slots(inventory).Single(slot => InventoryDriver.Id(slot).ItemId == InventorySample.Scroll);
        var over = vault.Center(vault.Cells[10]);
        var release = new Point(VaultOrigin.X + (int)over.X, VaultOrigin.Y + (int)over.Y);

        // The drag leaves the inventory: the release lands in the Vault's cell, outside the inventory's own panel.
        var start = InventoryDriver.Centre(inventory, scroll);
        inventory.PointerDown(start.X, start.Y, KeyModifiers.None);
        inventory.PointerMove(start.X + 10, start.Y);
        pointer = release;
        inventory.PointerMove(release.X, release.Y);
        inventory.PointerUp(release.X, release.Y);
        vault.Step(TimeSpan.FromMilliseconds(30));

        Assert.Equal(new[] { (InventorySample.Scroll, "Scroll") }, drag.Delivered);
        Assert.Equal(VaultProtocol.Deposit, vault.Server.Received.Last());
        // The inventory sends no command of its own for a drop on another window.
        Assert.Empty(InventoryDriver.Sent(port));
        Assert.Null(inventory.LastError);
        Assert.Null(vault.Host.LastError);

        object? Content(string id) => contents.TryGetValue(id, out var content) ? content : null;
    });

    [Fact]
    public void A_vault_item_released_over_the_inventory_window_withdraws_it_as_over_the_inventory() => RenderThread.Run(() =>
    {
        var windows = new WindowManager(new Size(1920, 1080), new MemoryWindowPositionStore(), "Server", "Character");
        var contents = new Dictionary<string, object>();
        var drag = new FakeItemDragHost();
        using var vault = new VaultFixture(null, VaultShellPanel.WindowHeight, drag);
        windows.Open(new WindowDefinition("vault", "Vault", VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight), new Point(0, 0));
        contents["vault"] = vault.Window;
        var port = new FakeInventoryPort();
        port.Push(InventorySample.Snapshot());
        using var inventory = AvaloniaPanel.Create(() => new InventoryWindow(port, new InventorySample.NoArt(),
            new InventorySettings(InventoryLayout.Vertical, showSlots: true)), 360, 530);
        inventory.ApplyTheme(new DerethClientTheme());
        windows.Open(new WindowDefinition("inventory", "Inventory", 360, 530), VaultOrigin);
        contents["inventory"] = inventory.Content;

        // The host answers the drop target from the window under the pointer.
        var overInventory = new Point(VaultOrigin.X + 180, 265);
        drag.Target = ItemDropRouting.InventoryAt(windows, Content, overInventory) ?? ItemDropTarget.Elsewhere;
        var start = vault.Center(vault.Cells[1]);
        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 30, start.Y);
        vault.Host.PointerMove(overInventory.X, overInventory.Y);
        vault.Host.PointerUp(overInventory.X, overInventory.Y);
        vault.Step(TimeSpan.FromMilliseconds(30));

        Assert.Equal(VaultProtocol.Withdraw, vault.Server.Received.Last());
        Assert.Empty(port.Commands);
        Assert.Null(vault.Host.LastError);

        object? Content(string id) => contents.TryGetValue(id, out var content) ? content : null;
    });
}

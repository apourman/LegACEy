using System;
using System.IO;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Plugin.Market;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// Draws the Market window's screens to PNGs for comparing with the mockups, when LEGACEY_MARKET_RENDER names a folder. Nothing is drawn otherwise.
/// </summary>
public sealed class MarketRenderTests
{
    [Fact]
    public void Renders_the_market_screens_when_asked() => RenderThread.Run(() =>
    {
        var folder = Environment.GetEnvironmentVariable("LEGACEY_MARKET_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);

        // the mockup's sizes: wide, medium and narrow
        foreach (var (width, height) in new[] { (1280, 720), (900, 620), (520, 680) })
        {
            using var market = new MarketFixture(width, server =>
            {
                // something to sell, and listings on each tab
                var plain = new ItemVisual(0x06001234, 0, 0, 0, 0x060011D4);
                foreach (var (guid, name) in new[] { (0x5001u, "Ring of Rejuvenation"), (0x5002u, "Necklace of Lost Light"), (0x5003u, "Frost Spear"), (0x5004u, "Atlan Sword") })
                    server.Pack[guid] = new SellVaultItem(guid, name, 1, "held", plain, 0);
                server.Vault.Add(new SellVaultItem(0x6001, "Atlan Sword", 1, "listed", plain, 0));
                server.Vault.Add(new SellVaultItem(0x6002, "Necklace of Lost Light", 1, "listed", plain, 0));
                // a week of ledger lines for Activity
                var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
                var balance = 9000L;
                var seq = 1L;
                foreach (var (hours, amount, text) in new[] { (150, 1200L, "Sold Amuli Coat to Saro"), (120, -400L, "Bought Ring from Kaelin"),
                    (96, 2600L, "Sold Atlan Sword to Eldwyn"), (70, 1800L, "Sold Frost Spear to Brannoch"), (40, -1500L, "Withdrew trade notes"),
                    (20, 640L, "Sold Shimmering Necklace to Eldwyn"), (3, -1200L, "Bought Nariyid Breastplate from Kaelin"), (1, 85L, "Sold Imbued Ruby to Saro") })
                {
                    balance += amount;
                    server.Ledger.Add(new LedgerLine(seq++, amount, balance, now.AddHours(-hours), $"{text} · {(amount > 0 ? "+" : "−")}{Math.Abs(amount):N0} MMD"));
                }
                server.EarnedThisWeek = 725;
                server.SpentThisWeek = 1200;
                server.ItemsSold = 2;
                server.ItemsBought = 1;
            });
            market.Server.Seed(0x6001, "Atlan Sword", 3500, created: market.Now.AddDays(-8));
            market.Server.Seed(0x6002, "Necklace of Lost Light", 1800, created: market.Now.AddDays(-13).AddHours(-12));
            var main = new InventoryPack(0x50000001, "Main", 0, 102);
            var items = market.Server.Pack.Keys.Select((guid, slot) => new InventoryItem(guid, market.Server.Pack[guid].Name, main.Id, slot,
                market.Server.Pack[guid].Visual, 1, 1, 1, 0)).ToList();
            market.Inventory.Push(new InventorySnapshot(main, Array.Empty<InventoryPack>(), items, Array.Empty<WieldedItem>(), 0, 0, 0, 0, 0));
            market.Host.Resize(width, height);
            void Save(string name)
            {
                market.Settle();
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(width, height));
                bitmap.Render(market.Window);
                bitmap.Save(Path.Combine(folder, $"{width}-{name}.png"));
            }

            market.Browse.Select(market.Server.Catalog.First().Id);
            Save("browse");
            market.Browse.SetView(BrowseView.Table);
            Save("table");
            market.Browse.SetView(BrowseView.Cards);
            market.Browse.OpenBuy(market.Server.Catalog.First().Id);
            Save("buy");
            market.Browse.CloseBuy();
            market.Panel.Show(MarketScreen.Sell);
            market.Settle();
            market.Sell.Model.Pick(0x5001);
            market.Sell.Model.SetPrice("975");
            Save("sell");
            market.Panel.Show(MarketScreen.Activity);
            market.Settle();
            Save("activity");
        }
    });
}

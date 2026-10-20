using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Plugin.Market;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// A market.sold push from the fake server with the Market window open: a toast, a green [Market] chat line, and a count on the Sell entries
/// until the player looks at the Sold tab. With the window closed, see PluginRegistryTests.
/// </summary>
public sealed class MarketSaleNoticeTests
{
    private static readonly SaleNotice Ruby = new(41, "Imbued Ruby", "Saro", 85, 12_450);

    [Fact]
    public void Each_sale_counts_on_the_sell_badge_and_looking_at_sold_clears_it() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1100);
        Assert.Equal(string.Empty, market.Panel.SellBadgeText);

        market.Server.Push(MarketProtocol.Sold, MarketProtocol.WriteSold(Ruby));
        market.Server.Push(MarketProtocol.Sold, MarketProtocol.WriteSold(Ruby with { ListingId = 42, Item = "Atlan Sword", Price = 3_500 }));
        market.Settle();

        Assert.Equal(2, market.Toasts.Count);
        Assert.Equal("[Market] Your Atlan Sword sold to Saro for 3,500 MMD.", market.ChatLines.Last());
        Assert.Equal("2", market.Panel.SellBadgeText);

        // the Sell screen opens on Active: the count stays until the player looks at Sold
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();
        Assert.Equal("2", market.Panel.SellBadgeText);

        var sold = market.Window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content is TextBlock { Text: "Sold" });
        sold.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        market.Settle();

        Assert.Equal(string.Empty, market.Panel.SellBadgeText);
        Assert.Equal(0, market.Sales.Unseen);
    });

    [Fact]
    public void A_sale_while_the_sold_tab_is_showing_is_seen_at_once_and_the_tab_shows_it() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1100);
        market.Panel.Show(MarketScreen.Sell);
        market.Sell.Model.ShowListingTab(SellListingTab.Sold);
        market.Settle();
        Assert.DoesNotContain(market.Texts(), text => text.Contains("Imbued Ruby"));

        market.Server.Seed(0x7001, "Imbued Ruby", 85, FakeMarketServer.ListingStatus.Sold, closed: market.Now);
        market.Server.Push(MarketProtocol.Sold, MarketProtocol.WriteSold(Ruby));
        market.Settle();

        Assert.Single(market.Toasts);
        Assert.Contains(market.Texts(), text => text.Contains("Imbued Ruby"));
        Assert.Equal(string.Empty, market.Panel.SellBadgeText);
        Assert.Equal(0, market.Sales.Unseen);
    });
}

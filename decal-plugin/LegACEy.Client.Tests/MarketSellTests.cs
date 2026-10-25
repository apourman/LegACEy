using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Plugin.Market;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// The Sell screen over the fake market server: listing from the pack and the Vault, delisting, the listings tabs, the greyed-out reason, the
/// listing limit, a refused listing, and the layout at each width. The fake server decodes each request and answers with a real reply.
/// </summary>
public sealed class MarketSellTests
{
    private static readonly ItemVisual Plain = new(0x06001234, 0, 0, 0, 0x060011D4);

    [Fact]
    public void A_pack_item_is_deposited_and_listed_at_the_typed_price_and_then_shows_the_gold_dot() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        PutInPack(market, 0x5001, "Ring of Rejuvenation");
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        market.Sell.Model.Pick(0x5001);
        market.Sell.Model.SetPrice("975");
        market.Settle();
        Click(market, "List for 975");
        market.Settle();

        // the request carried the item and the whole-stack price; the server moved the item into the Vault and listed it
        Assert.Equal(0x5001u, market.Server.LastList!.Value.Guid);
        Assert.Equal(975L, market.Server.LastList!.Value.Price);
        Assert.True(Shows(market, "Ring of Rejuvenation is listed for 975 MMD for 14 days."));
        Assert.Equal(MarketInk.LitTextBrush, market.Window.GetVisualDescendants().OfType<TextBlock>()
            .First(text => (text.Text ?? string.Empty).Contains("is listed for 975 MMD", StringComparison.Ordinal)).Foreground);
        Assert.Empty(market.Server.Pack);

        // the pack no longer holds it, and the Vault shows it with the gold dot
        SetPack(market);
        market.Settle();
        market.Sell.Model.ShowTab(SellTab.Vault);
        market.Settle();
        var cell = Cell(market, 0x5001);
        Assert.True(cell.GetVisualDescendants().OfType<Ellipse>().Any(dot => dot.Name == "GoldDot" && dot.IsVisible), "the gold dot");
        Assert.False(market.Sell.Model.Items.Single().Greyed, "a listed item is not greyed out");
    });

    [Fact]
    public void A_listed_item_delisted_goes_back_to_the_vault_and_leaves_the_active_tab() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        market.Server.Vault.Add(new SellVaultItem(0x6001, "Atlan Sword", 1, "listed", Plain, 0));
        var listing = market.Server.Seed(0x6001, "Atlan Sword", 3500);
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();
        Assert.True(Shows(market, "Atlan Sword"));

        Click(market, "Delist");
        market.Settle();

        Assert.Equal(listing.Id, market.Server.LastDelist!.Value);
        Assert.True(Shows(market, "Atlan Sword is back in your Vault."));
        Assert.Equal("held", market.Server.Vault.Single().State);
        Assert.False(Shows(market, "3,500"), "its listing is no longer on the Active tab");
    });

    [Fact]
    public void A_listing_with_minutes_left_is_tagged_ending() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        market.Server.Vault.Add(new SellVaultItem(0x6001, "Atlan Sword", 1, "listed", Plain, 0));
        market.Server.Seed(0x6001, "Atlan Sword", 3500, created: market.Now.AddDays(-14).AddMinutes(30));
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        Assert.True(Shows(market, "min left"));
        Assert.True(IsShown(market, "ENDING"));
    });

    [Fact]
    public void The_vault_tab_shows_every_item_past_the_first_page() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        for (uint i = 0; i < 150; i++) market.Server.Vault.Add(new SellVaultItem(0x7000 + i, $"Pyreal {i}", 1, "held", Plain, 0));
        market.Panel.Show(MarketScreen.Sell);
        market.Sell.Model.ShowTab(SellTab.Vault);
        market.Settle();

        Assert.Equal(150, market.Sell.Model.Items.Count);
    });

    [Fact]
    public void Change_price_sets_the_listing_to_the_typed_price_and_the_row_shows_it() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        market.Server.Vault.Add(new SellVaultItem(0x6001, "Atlan Sword", 1, "listed", Plain, 0));
        var listing = market.Server.Seed(0x6001, "Atlan Sword", 3500);
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        Click(market, "Change price");
        market.Settle();
        PriceBox(market).Text = "3200";
        market.Settle();
        Click(market, "Set price");
        market.Settle();

        // the request carried the listing and the new price; the listing kept its id and is still on the Active tab
        Assert.Equal((listing.Id, 3200L), market.Server.LastReprice);
        Assert.True(Shows(market, "Atlan Sword is now listed for 3,200 MMD."));
        Assert.True(IsShown(market, "3,200"), "the row shows the new price");
        Assert.False(IsShown(market, "3,500"), "the old price is gone");
        Assert.Null(market.Sell.Model.RepriceListingId);
    });

    [Fact]
    public void A_refused_change_price_shows_the_servers_message_and_the_row_keeps_its_price() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        market.Server.Vault.Add(new SellVaultItem(0x6001, "Atlan Sword", 1, "listed", Plain, 0));
        market.Server.Seed(0x6001, "Atlan Sword", 3500);
        market.Server.RepriceRefusal = "Atlan Sword can't be repriced while its sale is pending.";
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        Click(market, "Change price");
        market.Settle();
        PriceBox(market).Text = "3200";
        market.Settle();
        Click(market, "Set price");
        market.Settle();

        Assert.True(Shows(market, "Atlan Sword can't be repriced while its sale is pending."));
        Assert.True(IsShown(market, "3,500"), "the row keeps its price");
        Assert.NotNull(market.Sell.Model.RepriceListingId);
        Assert.Equal("3200", PriceBox(market).Text);
    });

    [Fact]
    public void Change_price_checks_the_typed_price_before_it_is_sent() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        market.Server.Vault.Add(new SellVaultItem(0x6001, "Atlan Sword", 1, "listed", Plain, 0));
        market.Server.Seed(0x6001, "Atlan Sword", 3500);
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        Click(market, "Change price");
        market.Settle();
        PriceBox(market).Text = "3x";
        market.Settle();
        Click(market, "Set price");
        market.Settle();
        Assert.True(Shows(market, "Type a whole number of MMD."));

        PriceBox(market).Text = "0";
        market.Settle();
        Click(market, "Set price");
        market.Settle();
        Assert.True(Shows(market, "Set a whole price of at least 1 MMD."));

        Assert.Null(market.Server.LastReprice);
    });

    [Fact]
    public void The_listings_tabs_show_active_sold_and_expired_and_the_time_left_comes_from_the_server_lifetime() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        market.Server.LifetimeDays = 10;
        market.Server.Vault.Add(new SellVaultItem(0x6001, "Atlan Sword", 1, "listed", Plain, 0));
        market.Server.Seed(0x6001, "Atlan Sword", 3500, created: market.Now.AddDays(-1));
        market.Server.Seed(0x6002, "Shimmering Necklace", 640, FakeMarketServer.ListingStatus.Sold, closed: market.Now.AddDays(-2));
        market.Server.Seed(0x6003, "Scroll of Strength Self VI", 30, FakeMarketServer.ListingStatus.Expired, closed: market.Now.AddDays(-3));
        market.Server.Vault.Add(new SellVaultItem(0x6004, "Imbued Ruby", 4, "held", Plain, 0));
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        // the Active tab: the time left is counted from the lifetime the server sent, 10 days, not a fixed figure
        Assert.True(Shows(market, "Atlan Sword"));
        Assert.True(Shows(market, "9 days left"));
        Assert.False(Shows(market, "Shimmering Necklace"));

        Click(market, "Sold");
        market.Settle();
        Assert.True(Shows(market, "Shimmering Necklace"));
        Assert.False(Shows(market, "Atlan Sword"));
        Assert.Equal(MarketProtocol.SoldTab, market.Server.LastListings!.Value.Tab);

        Click(market, "Expired");
        market.Settle();
        Assert.True(Shows(market, "Scroll of Strength Self VI"));
        Assert.False(Shows(market, "Shimmering Necklace"));

        // the price panel's lifetime line is the same setting
        market.Sell.Model.ShowListingTab(SellListingTab.Active);
        market.Settle();
        market.Sell.Model.ShowTab(SellTab.Vault);
        market.Settle();
        market.Sell.Model.Pick(0x6004);
        market.Settle();
        Assert.True(Shows(market, "Listed for 10 days, then back to your Vault."));
    });

    [Fact]
    public void An_item_that_cant_be_listed_is_greyed_out_with_its_reason_and_its_list_button_is_off() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        PutInPack(market, 0x5002, "Attuned Blade");
        market.Server.Unsellable[0x5002] = "Attuned Blade is attuned and cannot go in the Vault.";
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        var cell = Cell(market, 0x5002);
        Assert.True(cell.Opacity < 1, "greyed out");
        Assert.Equal("Attuned Blade is attuned and cannot go in the Vault.", ToolTip.GetTip(cell));

        market.Sell.Model.Pick(0x5002);
        market.Sell.Model.SetPrice("100");
        market.Settle();
        Assert.True(Shows(market, "Attuned Blade is attuned and cannot go in the Vault."));
        Assert.False(FindButton(market, "List for 100").IsEnabled);
    });

    [Fact]
    public void At_the_listing_limit_the_list_button_says_so_and_stays_off() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980, server => server.Account = new MarketAccount(1250, false, false, 12, 1000, 2, 2, "Arwic Wanderer"));
        market.Server.Seed(0x6101, "Old Blade", 10);
        market.Server.Seed(0x6102, "Older Blade", 10);
        PutInPack(market, 0x5003, "New Blade");
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        market.Sell.Model.Pick(0x5003);
        market.Sell.Model.SetPrice("100");
        market.Settle();

        Assert.True(Shows(market, "You have 2 active listings, the most. Delist one first."));
        Assert.False(FindButton(market, "List for 100").IsEnabled);
    });

    [Fact]
    public void A_listing_the_server_refuses_leaves_the_item_in_the_vault_and_says_so() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        PutInPack(market, 0x5004, "Spare Shield");
        market.Server.ListRefusal = "You already have 200 active listings, the most your account can have. Delist one first.";
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        market.Sell.Model.Pick(0x5004);
        market.Sell.Model.SetPrice("100");
        market.Settle();
        Click(market, "List for 100");
        market.Settle();

        Assert.True(Shows(market, "Spare Shield is in your Vault."));
        Assert.Equal("held", VaultStateOf(market, 0x5004));

        // the window moves to the Vault tab and picks the item, so the reason is shown beside it
        Assert.Equal(SellTab.Vault, market.Sell.Model.Tab);
        Assert.Equal(0x5004u, market.Sell.Model.Selected?.Guid);
    });

    [Theory]
    [InlineData(980)]
    [InlineData(1200)]
    public void A_medium_or_wide_window_splits_the_items_and_price_from_the_listings_running_the_full_height(int width) => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(width);
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        Assert.True(TopOnScreen(market, "Your items") < TopOnScreen(market, "PRICE, WHOLE STACK"), "the price panel is under the items");
        Assert.True(LeftOnScreen(market, "Your listings") > LeftOnScreen(market, "PRICE, WHOLE STACK"), "the listings are beside them, on the right");
        Assert.True(Math.Abs(TopOnScreen(market, "Your items") - TopOnScreen(market, "Your listings")) < 10, "both start at the top");
    });

    [Fact]
    public void A_narrow_window_keeps_the_items_in_view_while_one_is_picked_and_the_listings_on_their_own_tab() => RenderThread.Run(() =>
    {
        using var narrow = new MarketFixture(520);
        narrow.Panel.Show(MarketScreen.Sell);
        narrow.Settle();
        Assert.True(IsShown(narrow, "Your items"), "the narrow window opens on the items");
        Assert.False(IsShown(narrow, "PRICE, WHOLE STACK"), "the price panel waits for a pick");
        Assert.False(IsShown(narrow, "Your listings"), "the listings wait on their tab");

        PutInPack(narrow, 0x5005, "Ring of Rejuvenation");
        narrow.Settle();
        narrow.Sell.Model.Pick(0x5005);
        narrow.Settle();
        Assert.True(IsShown(narrow, "PRICE, WHOLE STACK"), "a pick opens the price panel");
        Assert.True(IsShown(narrow, "Your items"), "and the items stay, so another can be picked");
        Assert.True(TopOnScreen(narrow, "Your items") < TopOnScreen(narrow, "PRICE, WHOLE STACK"), "the price panel sits under the items");

        Click(narrow, "Listings");
        narrow.Settle();
        Assert.True(IsShown(narrow, "Your listings"), "the Listings tab shows the listings");
        Assert.False(IsShown(narrow, "Your items"));
        Assert.False(IsShown(narrow, "PRICE, WHOLE STACK"));
    });

    [Fact]
    public void A_listing_with_an_hour_left_says_1_hour_in_the_singular() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980, server => server.LifetimeDays = 1);
        market.Server.Vault.Add(new SellVaultItem(0x6101, "Short Blade", 1, "listed", Plain, 0));
        market.Server.Seed(0x6101, "Short Blade", 50, created: market.Now.AddHours(-23));
        market.Panel.Show(MarketScreen.Sell);
        market.Settle();

        Assert.True(Shows(market, "1 hour left"));
        Assert.False(Shows(market, "1 hours left"));
    });

    /// <summary>Puts an item in the pack: on the server, and in the client's snapshot.</summary>
    private static void PutInPack(MarketFixture market, uint guid, string name)
    {
        market.Server.Pack[guid] = new SellVaultItem(guid, name, 1, "held", Plain, 0);
        SetPack(market);
    }

    /// <summary>Makes the client's pack the server's pack, as the game's inventory events would.</summary>
    private static void SetPack(MarketFixture market)
    {
        var main = new InventoryPack(0x50000001, "Main", 0, 102);
        var items = market.Server.Pack.Keys.Select((guid, slot) => new InventoryItem(guid, market.Server.Pack[guid].Name, main.Id, slot, Plain, 1, 1, 1, 0)).ToList();
        market.Inventory.Push(new InventorySnapshot(main, Array.Empty<InventoryPack>(), items, Array.Empty<WieldedItem>(), 0, 0, 0, 0));
    }

    /// <summary>The change-price field: the one with the price watermark, not the Sell screen's own price box.</summary>
    private static TextBox PriceBox(MarketFixture market) => market.Window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Watermark == "MMD");

    private static string VaultStateOf(MarketFixture market, uint guid) => market.Server.Vault.FirstOrDefault(item => item.Guid == guid)?.State ?? "pack";

    /// <summary>Whether a text the window shows contains the fragment. A text under a hidden panel is not shown.</summary>
    private static bool Shows(MarketFixture market, string fragment) => market.Window.GetVisualDescendants().OfType<TextBlock>()
        .Any(text => text.IsEffectivelyVisible && (text.Text ?? string.Empty).Contains(fragment, StringComparison.Ordinal));

    private static bool IsShown(MarketFixture market, string text) => market.Window.GetVisualDescendants().OfType<TextBlock>()
        .Any(block => block.Text == text && block.IsEffectivelyVisible);

    /// <summary>Where a text is drawn, from the top of the window.</summary>
    private static double TopOnScreen(MarketFixture market, string text) => Text(market, text).TranslatePoint(new Point(0, 0), market.Window)!.Value.Y;

    private static double LeftOnScreen(MarketFixture market, string text) => Text(market, text).TranslatePoint(new Point(0, 0), market.Window)!.Value.X;

    /// <summary>How far a text's box reaches below its top.</summary>
    private static double BottomOffset(MarketFixture market, string text) => Text(market, text).Bounds.Height;

    private static TextBlock Text(MarketFixture market, string text) =>
        market.Window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text == text);

    private static Border Cell(MarketFixture market, uint guid) => market.Window.GetVisualDescendants().OfType<Border>()
        .First(cell => cell.Tag is uint tag && tag == guid);

    private static Button FindButton(MarketFixture market, string prefix) => market.Window.GetVisualDescendants().OfType<Button>()
        .First(button => button.Content is TextBlock text && (text.Text ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Presses a button as the mouse does: its Click is raised.</summary>
    private static void Click(MarketFixture market, string prefix)
    {
        var button = FindButton(market, prefix);
        Assert.True(button.IsEffectivelyVisible, $"'{prefix}' is shown");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}

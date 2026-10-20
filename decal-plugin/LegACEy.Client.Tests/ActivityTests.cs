using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LegACEy.Plugin.Market;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// The Activity screen over a <see cref="FakeMarketServer"/>: the tiles, the ledger's tabs and older pages, the balance chart, and trade notes
/// withdrawn from it. The fake answers history with the server's paging, and the screen is driven the way a player drives it: buttons pressed,
/// the ledger scrolled, the window resized.
/// </summary>
public sealed class ActivityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_ledger_tabs_show_every_mmd_and_item_event_newest_first() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980, server =>
        {
            server.Ledger.Add(new LedgerLine(1, 100, 100, Start.AddHours(-3), "Admin adjustment: welcome · +100 MMD"));
            server.Ledger.Add(new LedgerLine(2, 50, 150, Start.AddHours(-1), "Market fee on Sword · +50 MMD"));
            server.Movements.Add(new MovementLine(1, Start.AddHours(-2), "list", "Listed Sword for 50 MMD"));
            server.Movements.Add(new MovementLine(2, Start.AddMinutes(-30), "deposit", "Deposited Sword"));
        });
        var activity = Open(market);

        Assert.Equal(new[] { "Deposited Sword", "Market fee on Sword · +50 MMD", "Listed Sword for 50 MMD", "Admin adjustment: welcome · +100 MMD" }, activity.LedgerRowTexts);

        Press(market, "MMD");
        Assert.Equal(new[] { "Market fee on Sword · +50 MMD", "Admin adjustment: welcome · +100 MMD" }, activity.LedgerRowTexts);

        Press(market, "Items");
        Assert.Equal(new[] { "Deposited Sword", "Listed Sword for 50 MMD" }, activity.LedgerRowTexts);
        Assert.Null(market.Host.LastError);
    });

    [Fact]
    public void A_sale_is_one_row_on_the_All_tab_and_its_movement_is_on_the_Items_tab() => RenderThread.Run(() =>
    {
        // as the purchase writes them: the seller's line and the Sold movement, the buyer's line and the Bought movement, each at the same time
        using var market = new MarketFixture(980, server =>
        {
            server.Ledger.Add(new LedgerLine(2, 50, 1300, Start.AddMinutes(-10), "Sold Sword to Bob · +50 MMD"));
            server.Movements.Add(new MovementLine(2, Start.AddMinutes(-10), MarketProtocol.SoldMovement, "Sold Sword to Bob"));
            server.Ledger.Add(new LedgerLine(1, -40, 1250, Start.AddMinutes(-20), "Bought Shield from Kaelin · −40 MMD"));
            server.Movements.Add(new MovementLine(1, Start.AddMinutes(-20), MarketProtocol.BoughtMovement, "Bought Shield from Kaelin"));
        });
        var activity = Open(market);

        Assert.Equal(new[] { "Sold Sword to Bob · +50 MMD", "Bought Shield from Kaelin · −40 MMD" }, activity.LedgerRowTexts);

        Press(market, "Items");
        Assert.Equal(new[] { "Sold Sword to Bob", "Bought Shield from Kaelin" }, activity.LedgerRowTexts);

        Press(market, "MMD");
        Assert.Equal(new[] { "Sold Sword to Bob · +50 MMD", "Bought Shield from Kaelin · −40 MMD" }, activity.LedgerRowTexts);
        Assert.Null(market.Host.LastError);
    });

    [Fact]
    public void Scrolling_to_the_end_of_the_ledger_loads_older_pages_and_stops_when_there_are_none() => RenderThread.Run(() =>
    {
        // Sixty of each, interleaved two minutes apart: a page is fifty, so each stream takes two
        using var market = new MarketFixture(980, server => Seed(server, 60));
        var activity = Open(market);

        Assert.Equal((MarketProtocol.Newest, MarketProtocol.Newest), market.Server.HistoryRequests[0]);
        Assert.Equal(MarketProtocol.HistoryPageSize, Chart(activity).Points.Count);

        // the All tab shows only what is complete back to the oldest entry both streams have loaded, so nothing is out of order
        Assert.True(activity.LedgerRowTexts.Count < 120);

        Scroll(activity, market);

        Assert.Equal(2, market.Server.HistoryRequests.Count);
        Assert.Equal((11L, 11L), market.Server.HistoryRequests[1]);
        Assert.Equal(120, activity.LedgerRowTexts.Count);

        // nothing older: scrolling again asks for nothing
        Scroll(activity, market);
        Assert.Equal(2, market.Server.HistoryRequests.Count);

        Press(market, "MMD");
        Assert.Equal(60, activity.LedgerRowTexts.Count);
        Assert.Equal("Sold 60", activity.LedgerRowTexts[0]);
        Assert.Equal("Sold 1", activity.LedgerRowTexts[59]);

        Press(market, "Items");
        Assert.Equal(60, activity.LedgerRowTexts.Count);
        Assert.Null(market.Host.LastError);
    });

    [Fact]
    public void The_chart_places_each_line_by_its_time_under_the_range_of_dates() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980, server => Seed(server, 4));
        var activity = Open(market);

        var points = Chart(activity).Points;
        Assert.Equal(4, points.Count);
        Assert.Equal(0, points[0].X, 1);
        Assert.Equal(340, points[^1].X, 1);

        var day = Start.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);
        Assert.Contains($"Balance, {day}", market.Texts());
    });

    [Fact]
    public void The_tiles_show_the_balance_the_weekly_sales_and_purchases_and_the_listings_against_the_cap() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200, server =>
        {
            server.EarnedThisWeek = 725;
            server.SpentThisWeek = 1200;
            server.ItemsSold = 2;
            server.ItemsBought = 1;
        });
        Open(market);

        var texts = market.Texts();
        Assert.Contains("1,250", texts);
        Assert.Contains("+725", texts);
        Assert.Contains("−1,200", texts);
        Assert.Contains("2 items sold", texts);
        Assert.Contains("1 item bought", texts);
        Assert.Contains("3 / 200", texts);
    });

    [Fact]
    public void Withdrawing_trade_notes_shows_the_servers_message_and_moves_the_balance() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        Open(market);

        Withdraw(market, "20");
        market.Settle();

        Assert.Equal(new[] { 20L }, market.Server.Withdrawals);
        Assert.Contains("You withdraw 20 trade notes. Your balance is 1,230 MMD.", market.Texts());
        Assert.Equal("1,230 MMD", market.Panel.BalanceText);
        Assert.Null(market.Host.LastError);
    });

    [Fact]
    public void A_withdrawal_the_pack_cannot_take_says_why_and_moves_nothing() => RenderThread.Run(() =>
    {
        const string refusal = "You do not have room in your pack for 20 trade notes. Nothing was withdrawn. Free some pack space and try again.";
        using var market = new MarketFixture(980, server => server.WithdrawRefusal = refusal);
        Open(market);

        Withdraw(market, "20");
        market.Settle();

        Assert.Contains(refusal, market.Texts());
        Assert.Equal("1,250 MMD", market.Panel.BalanceText);
        Assert.Null(market.Host.LastError);
    });

    [Fact]
    public void A_second_press_while_the_withdrawal_is_waiting_sends_nothing() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        var activity = Open(market);

        Withdraw(market, "20");
        Withdraw(market, "20");
        Assert.False(WithdrawButton(activity).IsEnabled, "held while the server answers");

        market.Settle();

        Assert.Equal(new[] { 20L }, market.Server.Withdrawals);
        Assert.True(WithdrawButton(activity).IsEnabled);
        Assert.Null(market.Host.LastError);
    });

    [Theory]
    [InlineData(520, true)]
    [InlineData(980, false)]
    public void A_narrow_window_wraps_the_tiles_two_by_two_and_puts_the_ledger_and_chart_on_tabs(int width, bool narrow) => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(width, server => Seed(server, 4));
        var activity = Open(market);

        var tiles = activity.GetVisualDescendants().OfType<UniformGrid>().Single().Children.Where(tile => tile.IsEffectivelyVisible).ToList();
        Assert.Equal(4, tiles.Count);
        Assert.Equal(narrow ? 2 : 4, tiles.Select(tile => tile.Bounds.X).Distinct().Count());

        var labels = new[] { "Balance", "Earned this week", "Spent this week", "Listed now" };
        var order = market.Texts().Where(text => labels.Contains(text)).ToArray();
        Assert.Equal(narrow ? new[] { "Balance", "Listed now", "Earned this week", "Spent this week" } : new[] { "Balance", "Earned this week", "Spent this week", "Listed now" }, order);

        Assert.Equal(narrow, TabButton(activity, "Balance chart").IsEffectivelyVisible);
        Assert.True(Ledger(activity).IsEffectivelyVisible);
        Assert.Equal(!narrow, Chart(activity).IsEffectivelyVisible);

        if (narrow)
        {
            Press(market, "Balance chart");
            Assert.False(Ledger(activity).IsEffectivelyVisible);
            Assert.True(Chart(activity).IsEffectivelyVisible);
        }
        Assert.Null(market.Host.LastError);
    });

    /// <summary>Shows the Activity screen and waits for its first page.</summary>
    private static ActivityScreen Open(MarketFixture market)
    {
        market.Panel.Show(MarketScreen.Activity);
        market.Settle();
        return market.Host.Content.GetVisualDescendants().OfType<ActivityScreen>().Single();
    }

    /// <summary>Types the amount and presses Withdraw trade notes, as a player does.</summary>
    private static void Withdraw(MarketFixture market, string amount)
    {
        var activity = market.Host.Content.GetVisualDescendants().OfType<ActivityScreen>().Single();
        activity.GetVisualDescendants().OfType<TextBox>().Single().Text = amount;
        WithdrawButton(activity).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Presses the button with this text, as a player does.</summary>
    private static void Press(MarketFixture market, string text)
    {
        TabButton(market.Host.Content.GetVisualDescendants().OfType<ActivityScreen>().Single(), text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        market.Settle();
    }

    /// <summary>Scrolls the ledger to its end, and lets the older page come back.</summary>
    private static void Scroll(ActivityScreen activity, MarketFixture market)
    {
        var ledger = Ledger(activity);
        ledger.Offset = new Vector(0, ledger.Extent.Height);
        market.Settle();
    }

    private static Button WithdrawButton(ActivityScreen activity) => TabButton(activity, "Withdraw trade notes");

    private static Button TabButton(ActivityScreen activity, string text) =>
        activity.GetVisualDescendants().OfType<Button>().Single(button => (button.Content as TextBlock)?.Text == text);

    /// <summary>The ledger's scroll viewer: the one whose content is the rows</summary>
    private static ScrollViewer Ledger(ActivityScreen activity) =>
        activity.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer => viewer.Content is StackPanel);

    private static Polyline Chart(ActivityScreen activity) => activity.GetVisualDescendants().OfType<Polyline>().Single();

    /// <summary>
    /// <paramref name="count"/> ledger lines and as many movements, interleaved two minutes apart, the newest last.
    /// Ledger line n is "Sold n" and movement n is "Listed n".
    /// </summary>
    private static void Seed(FakeMarketServer server, int count)
    {
        for (var number = 1; number <= count; number++)
        {
            server.Ledger.Add(new LedgerLine(number, number, 1250, Start.AddMinutes(-2 * (count + 1 - number)), $"Sold {number}"));
            server.Movements.Add(new MovementLine(number, Start.AddMinutes(-2 * (count + 1 - number) - 1), "list", $"Listed {number}"));
        }
    }
}

using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Market;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// Buying from the Browse screen over a <see cref="FakeMarketServer"/>: the confirm dialog, its delivery choice, each refusal's message, the toast,
/// and one key per attempt. Driven by clicking the window's buttons, as a player buys.
/// </summary>
public sealed class MarketBuyTests
{
    [Fact]
    public void Buy_opens_a_dialog_with_the_price_the_balance_after_and_the_seller_and_delivers_to_the_pack_by_default() => RenderThread.Run(() =>
    {
        using var market = OpenBuy(fixture => { });

        Assert.True(market.Panel.BuyDialog.IsVisible);
        var texts = DialogTexts(market);
        Assert.Contains("Buy Nariyid Breastplate?", texts);
        Assert.Contains("1,200 MMD", texts);
        Assert.Contains("50 MMD", texts);
        Assert.Contains("Kaelin", texts);
        Assert.Contains("Arwic Wanderer's pack", texts);
        Assert.Contains("Right away. 38 of 102 slots free.", texts);
        Assert.True(market.Browse.Buying!.ToPack);
        Assert.True(DialogButton(market, "pack").IsEnabled);

        // the Vault can be chosen at any time
        Click(DialogButton(market, "vault"));
        Click(DialogButton(market, "confirm-buy"));
        market.Settle();

        Assert.False(Assert.Single(market.Server.BuyRequests).ToPack);
    });

    [Fact]
    public void When_the_server_says_the_item_wont_fit_the_pack_is_greyed_out_and_the_vault_chosen() => RenderThread.Run(() =>
    {
        const string reason = "You do not have room in your pack for Nariyid Breastplate.";
        using var market = OpenBuy(server => server.Fit = new PackFit(false, 0, 102, reason));

        Assert.False(DialogButton(market, "pack").IsEnabled);
        Assert.False(market.Browse.Buying!.ToPack);
        Assert.Contains(reason, DialogTexts(market));

        market.Browse.ChooseDelivery(true);
        Assert.False(market.Browse.Buying!.ToPack, "the pack can't be chosen");
    });

    [Fact]
    public void A_purchase_shows_a_toast_reads_the_balance_again_and_leaves_the_results() => RenderThread.Run(() =>
    {
        using var market = OpenBuy(server => { });

        Click(DialogButton(market, "confirm-buy"));
        market.Settle();

        Assert.False(market.Panel.BuyDialog.IsVisible);
        Assert.Equal("Bought Nariyid Breastplate Nariyid Breastplate is in your pack. 1,200 MMD paid to Kaelin.", market.Panel.ToastText);
        Assert.Equal("50 MMD", market.Panel.BalanceText);
        Assert.DoesNotContain(market.Browse.Results, card => card.Id == 1);
    });

    [Fact]
    public void A_toast_hides_at_the_first_frame_after_its_time_and_a_new_one_starts_the_time_again() => RenderThread.Run(() =>
    {
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var toast = new DerethToast(() => now);
        var hidden = 0;
        toast.Hidden += () => hidden++;

        toast.Show("Sold Imbued Ruby", "Saro paid 85 MMD.");
        now += DerethToast.ShownFor - TimeSpan.FromSeconds(1);
        toast.Tick();
        Assert.True(toast.IsVisible);

        toast.Show("Sold Atlan Sword", "Saro paid 3,500 MMD.");
        now += TimeSpan.FromSeconds(2);
        toast.Tick();
        Assert.True(toast.IsVisible, "the second toast has its own time");

        now += DerethToast.ShownFor;
        toast.Tick();
        Assert.False(toast.IsVisible);
        Assert.Equal(1, hidden);
    });

    [Fact]
    public void A_purchase_whose_move_into_the_pack_is_refused_says_the_item_is_in_the_vault() => RenderThread.Run(() =>
    {
        using var market = OpenBuy(server => server.PackMoveRefusal = "You do not have room in your pack for Nariyid Breastplate.");

        Click(DialogButton(market, "confirm-buy"));
        market.Settle();

        Assert.Equal("Bought Nariyid Breastplate You do not have room in your pack for Nariyid Breastplate. Nariyid Breastplate is in your Vault. 1,200 MMD paid to Kaelin.",
            market.Panel.ToastText);
        Assert.Equal("50 MMD", market.Panel.BalanceText); // the purchase stands
    });

    [Fact]
    public void A_refusal_keeps_the_dialog_open_with_the_servers_message_and_charges_nothing() => RenderThread.Run(() =>
    {
        const string message = "You do not have enough MMD for this purchase.";
        using var market = OpenBuy(server => server.BuyRefusal = new BuyReply("insufficient_funds", message, 0, 0, false));

        Click(DialogButton(market, "confirm-buy"));
        market.Settle();

        Assert.True(market.Panel.BuyDialog.IsVisible, "the dialog stays to say why");
        Assert.Contains(message, DialogTexts(market));
        Assert.DoesNotContain(DialogButtons(market), b => b.Tag as string == "confirm-buy");
        Assert.Equal("1,250 MMD", market.Panel.BalanceText);
        Assert.Equal(string.Empty, market.Panel.ToastText);
        Assert.Contains(market.Browse.Results, card => card.Id == 1);
    });

    [Fact]
    public void A_listing_that_has_gone_leaves_the_results() => RenderThread.Run(() =>
    {
        using var market = OpenBuy(server => server.BuyRefusal = new BuyReply("gone", "That listing is no longer for sale.", 0, 0, false));

        Click(DialogButton(market, "confirm-buy"));
        market.Settle();

        Assert.Contains("That listing is no longer for sale.", DialogTexts(market));
        Assert.DoesNotContain(market.Browse.Results, card => card.Id == 1);
        Assert.Equal("1,250 MMD", market.Panel.BalanceText);
    });

    [Fact]
    public void A_price_change_shows_the_new_price_and_buying_at_it_is_a_new_attempt() => RenderThread.Run(() =>
    {
        using var market = OpenBuy(server => server.BuyRefusal = new BuyReply("price_changed", "The price has changed to 1,300 MMD. Nothing was charged.", 1300, 0, false));

        Click(DialogButton(market, "confirm-buy"));
        market.Settle();
        Assert.Contains("The price has changed to 1,300 MMD. Nothing was charged.", DialogTexts(market));
        Assert.Equal("1,250 MMD", market.Panel.BalanceText);

        market.Server.BuyRefusal = null;
        Click(DialogButton(market, "new-price"));
        market.Settle();
        Assert.Contains("Buy for 1,300", DialogTexts(market));
        Click(DialogButton(market, "confirm-buy"));
        market.Settle();

        var (first, second) = (market.Server.BuyRequests[0], market.Server.BuyRequests[1]);
        Assert.Equal(1300, second.Price);
        Assert.NotEqual(first.Key, second.Key);
    });

    [Fact]
    public void A_double_click_and_a_retry_send_the_one_key_and_charge_once() => RenderThread.Run(() =>
    {
        using var market = OpenBuy(server => server.BuyRefusal = new BuyReply("rate_limited", "Too many purchase attempts. Wait a minute and try again.", 0, 0, false));

        var confirm = DialogButton(market, "confirm-buy");
        Click(confirm);
        Click(confirm);
        market.Settle();
        Assert.Single(market.Server.BuyRequests);

        market.Server.BuyRefusal = null;
        Click(DialogButton(market, "confirm-buy"));
        market.Settle();

        Assert.Equal(2, market.Server.BuyRequests.Count);
        Assert.Single(market.Server.BuyRequests.Select(r => r.Key).Distinct());
        Assert.Equal("50 MMD", market.Panel.BalanceText);
    });

    [Fact]
    public void Esc_cancels_the_buy_dialog_before_it_closes_the_window() => RenderThread.Run(() =>
    {
        using var market = OpenBuy(server => { });

        market.Window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = market.Window });

        Assert.Null(market.Browse.Buying);
        Assert.Equal(0, market.CloseRequests);
        Assert.Empty(market.Server.BuyRequests);
    });

    /// <summary>A wide window with Nariyid Breastplate (1,200 MMD, from Kaelin) picked and its Buy clicked</summary>
    private static MarketFixture OpenBuy(System.Action<FakeMarketServer> configure)
    {
        var market = new MarketFixture(1200, configure);
        market.Browse.Select(1);
        market.Settle();
        var buy = market.Panel.BrowseScreen.BuyButton!;
        Assert.True(buy.IsEnabled);
        Click(buy);
        market.Settle();
        return market;
    }

    private static string[] DialogTexts(MarketFixture market) =>
        market.Panel.BuyDialog.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text ?? string.Empty).ToArray();

    private static Button[] DialogButtons(MarketFixture market) => market.Panel.BuyDialog.GetVisualDescendants().OfType<Button>().ToArray();

    private static Button DialogButton(MarketFixture market, string tag) => DialogButtons(market).First(b => b.Tag as string == tag);

    private static void Click(Button button)
    {
        if (button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}

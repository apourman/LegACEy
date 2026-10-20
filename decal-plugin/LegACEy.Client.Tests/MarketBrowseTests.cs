using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Plugin.Market;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// The Browse screen over a <see cref="FakeMarketServer"/>: the search, the chips, the paging, the table, the categories and the detail at each width.
/// The window is driven as a player drives it: its buttons are clicked, its boxes typed in and its results picked.
/// </summary>
public sealed class MarketBrowseTests
{
    [Fact]
    public void Results_show_as_cards_with_the_listing_facts_and_your_own_listing_marked() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);

        var texts = market.Texts();
        Assert.Contains("Nariyid Breastplate", texts);
        Assert.Contains("Armor · Gold", texts);
        Assert.Contains("Wk 8", texts);
        Assert.Contains("Melee Weapon · Steel", texts);
        Assert.Contains("1,200 MMD", texts);
        Assert.Contains("Your listing", texts);
        Assert.Equal(5, market.Browse.Results.Count);
    });

    [Fact]
    public void A_filter_shows_as_a_chip_and_clicking_the_chip_takes_it_off() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);

        market.Browse.AddFilter("price", "1000", "2000");
        market.Settle();

        Assert.Contains("Price 1000–2000  ×", market.Panel.BrowseScreen.ChipTexts);
        Assert.Equal(2, market.Browse.Results.Count);

        Click(ButtonWithText(market, "Price 1000–2000  ×"));
        market.Settle();

        Assert.DoesNotContain("Price 1000–2000  ×", market.Panel.BrowseScreen.ChipTexts);
        Assert.Equal(5, market.Browse.Results.Count);
    });

    [Theory]
    [InlineData("", "1500", "Price ≤ 1500  ×", 2)]
    [InlineData("2000", "", "Price ≥ 2000  ×", 3)]
    public void A_price_range_with_one_side_filters_and_labels_that_side(string low, string high, string chip, int count) => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);

        market.Browse.AddFilter("price", low, high);
        market.Settle();

        Assert.Contains(chip, market.Panel.BrowseScreen.ChipTexts);
        Assert.Equal(count, market.Browse.Results.Count);
    });

    [Fact]
    public void Add_filter_adds_a_price_range_through_its_editor() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);

        Click(ButtonWithText(market, "+ Add filter"));
        Assert.True(market.Panel.BrowseScreen.EditorOpen);

        // the kind's menu is the server's filter kinds; the range's two boxes show when it is picked
        var kinds = market.Host.Content.GetVisualDescendants().OfType<ComboBox>().First(box => box.Items.Cast<object>().Contains("Wield level"));
        kinds.SelectedIndex = kinds.Items.Cast<object>().ToList().IndexOf("Price");
        market.Settle();
        TextBoxWithWatermark(market, "From").Text = "1000";
        TextBoxWithWatermark(market, "To").Text = "2000";
        Click(ButtonWithText(market, "Apply"));
        market.Settle();

        Assert.Contains("Price 1000–2000  ×", market.Panel.BrowseScreen.ChipTexts);
        Assert.Equal(2, market.Browse.Results.Count);
    });

    [Fact]
    public void The_filter_kinds_sorts_and_categories_come_from_the_server() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);

        var facets = market.Browse.Facets!;

        Assert.Equal(new[] { "Name", "Category", "Price", "Seller", "Wield level", "Workmanship", "Arcane lore" }, facets.Kinds.Select(k => k.Label));
        Assert.Equal(FilterShape.Range, facets.Kinds.Single(k => k.Key == "price").Shape);
        Assert.Contains(facets.Sorts, s => s.Value == "price" && !s.Descending);
        Assert.Equal(new[] { "Armor", "Jewelry", "Melee Weapon" }, facets.Categories.Select(c => c.Label));
        Assert.Equal(new[] { "All categories", "Armor  2", "Jewelry  1", "Melee Weapon  2" }, market.Panel.CategoryTexts);
    });

    [Fact]
    public void Pages_load_in_order_without_repeats_to_the_end() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980, server =>
        {
            server.Catalog.Clear();
            server.Catalog.AddRange(Many(120));
        });

        // the window loads a page when its results don't yet fill the list, so the first page is there and more may follow on screen
        Assert.True(market.Browse.Results.Count >= 50, "the first page is shown");

        while (market.Browse.HasMore)
        {
            market.Browse.LoadMore();
            market.Settle();
        }

        Assert.Equal(120, market.Browse.Results.Count);
        Assert.Equal(120, market.Browse.Results.Select(r => r.Id).Distinct().Count());
        Assert.False(market.Browse.HasMore);
        // 120 listings in pages of 50: each page after the first is asked for once, by its cursor
        Assert.Equal(2, market.Server.BrowseRequests.Count(r => r.Cursor.Length > 0));
    });

    [Fact]
    public void The_table_shows_the_portal_columns_and_a_header_sorts_it_in_either_direction() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);

        market.Browse.SetView(BrowseView.Table);
        market.Settle();
        Assert.Contains("Workmanship", market.Texts());
        Assert.Contains("Listed time ↓", market.Texts());
        Assert.Contains("Nariyid Breastplate summary", market.Texts());

        Click(ButtonWithText(market, "Price"));
        market.Settle();
        Assert.Equal("price", market.Browse.Sort);
        Assert.False(market.Browse.Descending);
        Assert.Equal("Nariyid Breastplate", market.Browse.Results[0].Name);

        Click(ButtonWithText(market, "Price ↑"));
        market.Settle();
        Assert.True(market.Browse.Descending);
        Assert.Equal("Weeping Ring", market.Browse.Results[0].Name);

        market.Browse.SetView(BrowseView.Cards);
        market.Settle();
        Assert.DoesNotContain("Listed time ↓", market.Texts());
        Assert.Contains("Your listing", market.Texts());
    });

    [Theory]
    [InlineData(1200, true)]
    [InlineData(980, true)]
    [InlineData(520, false)]
    public void A_picked_listing_opens_its_detail_beside_over_or_in_place_of_the_results(int width, bool resultsStillShown) => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(width);

        market.Browse.Select(1);
        market.Settle();

        var screen = market.Panel.BrowseScreen;
        Assert.True(screen.DetailVisible, "the detail is open");
        Assert.Equal(resultsStillShown, screen.ResultsVisible);
        Assert.Contains("Workmanship: 8", market.Texts());
        Assert.Contains("Legendary Invulnerability", market.Texts());
        Assert.True(screen.BuyButton!.IsEnabled, "another seller's listing can be bought");
    });

    [Fact]
    public void Medium_detail_has_a_close_button_that_returns_to_the_results() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        market.Browse.Select(1);
        market.Settle();

        Click(ButtonWithText(market, "✕"));
        market.Settle();

        Assert.False(market.Panel.BrowseScreen.DetailVisible);
        Assert.True(market.Panel.BrowseScreen.ResultsVisible);
    });

    [Fact]
    public void Narrow_Back_returns_from_the_detail_to_the_list() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(520);
        market.Browse.Select(1);
        market.Settle();

        Click(ButtonWithText(market, "‹ Back to 5 results"));
        market.Settle();

        Assert.False(market.Panel.BrowseScreen.DetailVisible);
        Assert.True(market.Panel.BrowseScreen.ResultsVisible);
    });

    [Fact]
    public void The_tell_box_keeps_its_text_until_a_different_listing_is_picked() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);
        market.Browse.Select(1);
        market.Settle();
        Click(ButtonWithText(market, "Tell"));
        TextBoxWithWatermark(market, "Tell Kaelin").Text = "still typing";

        market.Browse.AddFilter("price", "", "5000");
        market.Settle();
        Assert.Equal("still typing", market.Panel.BrowseScreen.TellText);
        Assert.True(market.Panel.BrowseScreen.TellOpen);

        market.Browse.Select(5);
        market.Settle();
        Assert.Equal(string.Empty, market.Panel.BrowseScreen.TellText);
        Assert.False(market.Panel.BrowseScreen.TellOpen);
    });

    [Fact]
    public void More_from_filters_the_results_by_that_seller() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);
        market.Browse.Select(1);
        market.Settle();

        Click(ButtonWithText(market, "More from Kaelin"));
        market.Settle();

        Assert.Equal(2, market.Browse.Results.Count);
        Assert.All(market.Browse.Results, listing => Assert.Equal("Kaelin", listing.Seller));
        Assert.False(market.Panel.BrowseScreen.DetailVisible, "the detail closes with the new search");
    });

    [Fact]
    public void Categories_show_their_counts_and_a_category_narrows_the_results() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);

        Assert.Contains("Melee Weapon  2", market.Panel.CategoryTexts);

        Click(ButtonWithText(market, "Melee Weapon  2"));
        market.Settle();

        Assert.Equal(2, market.Browse.Results.Count);
        Assert.All(market.Browse.Results, listing => Assert.Equal("MeleeWeapon", listing.ItemType));
        Assert.Contains("Category: Melee Weapon  ×", market.Panel.BrowseScreen.ChipTexts);
    });

    [Fact]
    public void Typing_suggests_names_and_choosing_one_searches_by_it() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);

        market.Browse.SuggestFor("At");
        market.Settle();
        Assert.Equal(new[] { "Atlan Sword" }, market.Browse.Suggestions);

        Click(ButtonWithText(market, "Atlan Sword"));
        market.Settle();

        Assert.Single(market.Browse.Results);
        Assert.Equal("Atlan Sword", market.Browse.Results[0].Name);
        Assert.Contains("Name: Atlan Sword  ×", market.Panel.BrowseScreen.ChipTexts);
    });

    [Fact]
    public void A_suggestion_reply_for_text_that_has_changed_is_dropped() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);

        market.Browse.SuggestFor("At");
        market.Browse.SuggestFor("Fr");
        market.Settle();

        Assert.Equal(new[] { "Frost Spear" }, market.Browse.Suggestions);
    });

    [Fact]
    public void Facets_that_failed_are_asked_again_on_the_next_refresh() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200, server => server.FacetsStatus = ChannelStatus.Error);
        Assert.Null(market.Browse.Facets);

        market.Server.FacetsStatus = null;
        market.Browse.Refresh();
        market.Settle();

        Assert.NotNull(market.Browse.Facets);
        Assert.Contains("Melee Weapon  2", market.Panel.CategoryTexts);
    });

    [Fact]
    public void A_listing_that_has_gone_is_reported_and_the_detail_closes() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);
        market.Browse.Select(1);
        market.Settle();
        market.Server.Catalog.RemoveAll(listing => listing.Id == 1);

        market.Browse.Select(1);
        market.Settle();

        Assert.Null(market.Browse.SelectedId);
        Assert.Equal("That listing is no longer for sale.", market.Browse.Notice);
        Assert.DoesNotContain(market.Browse.Results, card => card.Id == 1);
    });

    [Fact]
    public void Slash_is_a_character_while_a_text_box_has_the_keyboard() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);
        market.Browse.Select(1);
        market.Settle();
        Click(ButtonWithText(market, "Tell"));
        var tell = TextBoxWithWatermark(market, "Tell Kaelin");
        tell.Focus();

        Assert.False(market.Window.WantsShortcut(Key.Oem2));
    });

    [Fact]
    public void A_tell_goes_through_the_window_the_plugin_creates() => RenderThread.Run(() =>
    {
        var clock = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var server = new FakeMarketServer(() => clock);
        var channel = new ServerChannelClient(server, () => clock);
        server.Deliver = channel.Receive;
        var sent = new List<(string Recipient, string Text)>();
        Control? created = null;
        using var host = AvaloniaPanel.Create(() => created = MarketPlugin.CreateWindow(channel, new NoArt(), new FakeInventoryPort(), (recipient, text) =>
        {
            sent.Add((recipient, text));
            return true;
        }, () => { }), 980, 640);
        void Settle()
        {
            for (var step = 0; step < 12; step++)
            {
                clock += TimeSpan.FromMilliseconds(30);
                server.Pump();
                channel.Tick();
                host.Tick();
            }
        }
        Settle();
        var window = (MarketShellWindow)created!;

        window.Panel.BrowseState.Select(1);
        Settle();
        Click(ButtonWithText(host, "Tell"));
        var box = TextBoxWithWatermark(host, "Tell Kaelin");
        box.Text = "  hello there  ";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });

        Assert.Equal(new[] { ("Kaelin", "hello there") }, sent);
    });

    private static IEnumerable<ListingCard> Many(int count)
    {
        var listed = new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);
        for (var number = 1; number <= count; number++)
            yield return new ListingCard(1000 + number, (uint)(0x80000000 + number), $"Item {number:000}", "MeleeWeapon", "Melee Weapon", "Steel", 5, null,
                null, "summary", 1, number * 10, "Kaelin", listed, false, 0, 0, 0, 0, 0, 0);
    }

    private static Button ButtonWithText(MarketFixture market, string text) => ButtonWithText(market.Host, text);

    private static Button ButtonWithText(AvaloniaPanel host, string text) => host.Content.GetVisualDescendants().OfType<Button>()
        .First(button => button.IsVisible && (button.Content as TextBlock)?.Text == text);

    private static TextBox TextBoxWithWatermark(MarketFixture market, string watermark) => TextBoxWithWatermark(market.Host, watermark);

    private static TextBox TextBoxWithWatermark(AvaloniaPanel host, string watermark) => host.Content.GetVisualDescendants().OfType<TextBox>()
        .First(box => box.IsVisible && (box.Watermark ?? string.Empty).StartsWith(watermark, StringComparison.Ordinal));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}

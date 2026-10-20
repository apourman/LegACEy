using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Market;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// The Market window over a <see cref="FakeMarketServer"/>, on the real channel wire: the account loads, the window lays itself out at a width,
/// and the keys work. The test drives the clock in <see cref="Step"/>.
/// </summary>
public sealed class MarketFixture : IDisposable
{
    private DateTime _now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
    private readonly ServerChannelClient _channel;
    private readonly MarketShellPanel _panel;
    private readonly MarketShellWindow _window;
    public int CloseRequests { get; private set; }

    /// <param name="width">The window's width: the layout follows it.</param>
    /// <param name="configure">Sets the fake server up before the window loads the account.</param>
    public MarketFixture(int width = 980, Action<FakeMarketServer>? configure = null)
    {
        Server = new FakeMarketServer(() => _now);
        configure?.Invoke(Server);
        _channel = new ServerChannelClient(Server, () => _now);
        Server.Deliver = _channel.Receive;
        Client = new MarketClient(_channel);
        Browse = new MarketBrowse(_channel);
        Sales = new MarketSales(_channel, (title, line) => Toasts.Add($"{title} {line}"), ChatLines.Add);
        Sell = new SellScreen(new SellViewModel(Client, Inventory, Sales), new InventorySample.NoArt());
        MarketShellPanel? panel = null;
        MarketShellWindow? window = null;
        Host = AvaloniaPanel.Create(() =>
        {
            panel = new MarketShellPanel(Client, Browse, new NoArt(), (seller, text) => { Server.Chat.Add($"/t {seller}, {text}"); return true; }, Sell, Sales);
            window = new MarketShellWindow(panel);
            window.CloseRequested += (_, _) => CloseRequests++;
            return window;
        }, width, (int)MarketPlugin.WindowSize.Height);
        _panel = panel!;
        _window = window!;
        Client.Start();
        Settle();
    }

    public FakeMarketServer Server { get; }
    public MarketClient Client { get; }
    public MarketBrowse Browse { get; }
    /// <summary>The character's pack, as the client sees it. The Sell screen's pack tab reads it.</summary>
    public FakeInventoryPort Inventory { get; } = new();
    /// <summary>The sale notices, as the plugin keeps them: the toasts over the game and the chat lines they asked the client for.</summary>
    public MarketSales Sales { get; }
    public List<string> Toasts { get; } = new();
    public List<string> ChatLines { get; } = new();
    /// <summary>The Sell screen the window shows.</summary>
    public SellScreen Sell { get; }
    /// <summary>The fake server's clock.</summary>
    public DateTime Now => _now;
    public AvaloniaPanel Host { get; }
    public MarketShellPanel Panel => _panel;
    public MarketShellWindow Window => _window;

    public void Step(TimeSpan time)
    {
        _now += time;
        Server.Pump();
        _channel.Tick();
        Host.Tick();
    }

    /// <summary>Lets the round trip happen and the window draw: a dozen steps of 30 ms.</summary>
    public void Settle()
    {
        for (var step = 0; step < 12; step++) Step(TimeSpan.FromMilliseconds(30));
    }

    /// <summary>The window's text, as drawn.</summary>
    public string[] Texts() => Host.Content.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text ?? string.Empty).ToArray();

    public void Resize(int width)
    {
        Host.Resize(width, (int)MarketPlugin.WindowSize.Height);
        Host.Tick();
    }

    public void Dispose()
    {
        _panel.Dispose();
        Sales.Dispose();
        Host.Dispose();
    }

    private sealed class NoArt : LegACEy.Client.GameArt.IGameArtSource
    {
        public LegACEy.Client.GameArt.GameImage? ReadImage(uint id) => null;
    }
}

public sealed class MarketShellTests
{
    [Theory]
    [InlineData(520, MarketLayoutMode.Narrow)]
    [InlineData(759, MarketLayoutMode.Narrow)]
    [InlineData(760, MarketLayoutMode.Medium)]
    [InlineData(1099, MarketLayoutMode.Medium)]
    [InlineData(1100, MarketLayoutMode.Wide)]
    public void The_layout_follows_the_window_width_and_the_top_bar_shows_the_vault_space_only_when_wide(int width, MarketLayoutMode mode) => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(width);

        Assert.Equal(mode, market.Panel.Mode);
        Assert.Equal(mode == MarketLayoutMode.Wide, market.Panel.VaultSpaceText.Length > 0);
        Assert.Null(market.Host.LastError);
    });

    [Fact]
    public void The_layout_changes_as_the_window_is_resized() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        Assert.Equal(MarketLayoutMode.Medium, market.Panel.Mode);

        market.Resize(1200);
        Assert.Equal(MarketLayoutMode.Wide, market.Panel.Mode);
        Assert.Contains("12 / 1,000", market.Texts());

        market.Resize(520);
        Assert.Equal(MarketLayoutMode.Narrow, market.Panel.Mode);
        Assert.DoesNotContain("12 / 1,000", market.Texts());
    });

    [Fact]
    public void The_top_bar_shows_the_balance_and_the_character_the_window_acts_for() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(1200);

        Assert.Equal("1,250", market.Panel.BalanceText);
        Assert.Contains("Arwic Wanderer", market.Texts());
    });

    [Fact]
    public void A_refused_market_shows_the_servers_plain_message() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980, server => server.Refusal = "The marketplace is not open yet.");

        Assert.Equal("The marketplace is not open yet.", market.Panel.NoticeText);
        Assert.Equal("—", market.Panel.BalanceText);
        Assert.Null(market.Client.Account);
    });

    [Fact]
    public void Slash_focuses_the_search_from_whatever_has_the_focus() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        var button = market.Window.GetVisualDescendants().OfType<DerethButton>().First();
        button.Focus();
        Assert.False(market.Panel.SearchHasFocus);

        market.Host.KeyDown(Key.Oem2);

        Assert.True(market.Panel.SearchHasFocus);
    });

    [Fact]
    public void Slash_in_a_narrow_window_opens_the_full_width_search_first() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(520);
        Assert.False(market.Panel.NarrowSearchOpen);

        market.Host.KeyDown(Key.Oem2);

        Assert.True(market.Panel.NarrowSearchOpen);
        Assert.True(market.Panel.SearchHasFocus);
    });

    [Fact]
    public void Escape_asks_the_window_to_close() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);

        market.Host.KeyDown(Key.Escape);

        Assert.Equal(1, market.CloseRequests);
    });
}

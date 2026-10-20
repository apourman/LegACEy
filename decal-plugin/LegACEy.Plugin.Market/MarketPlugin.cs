using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Market;

/// <summary>The Market, opened from the market station in the Marketplace hub (the "market" station). Its window has its own chrome.</summary>
public sealed class MarketPlugin : ILegACEyPlugin
{
    public const string Title = "Market";
    private const string WindowId = "market";
    private const string Station = "market";

    /// <summary>The size the window opens at, the first time, centred on the screen.</summary>
    public static readonly System.Drawing.Size WindowSize = new(980, 640);

    /// <summary>The smallest the window goes: the narrow layout, with its tab row and search icon, fits here.</summary>
    public static readonly System.Drawing.Size MinimumSize = new(520, 600);

    public string Name => "Market";
    public string Version => typeof(MarketPlugin).Assembly.GetName().Version.ToString(3);

    /// <summary>
    /// The actions the Market client requests. The client needs station.leave for every station window, as the Vault does, and the server
    /// lists only the actions it registers, so a server without market.account hides the Market.
    /// </summary>
    public IReadOnlyCollection<string> RequiredActions { get; } = new[]
    {
        MarketProtocol.Account, MarketProtocol.Sellable, MarketProtocol.List, MarketProtocol.Delist, MarketProtocol.Reprice, MarketProtocol.Listings,
        MarketProtocol.VaultList, MarketProtocol.History, MarketProtocol.WithdrawNotes, MarketBrowseProtocol.Browse, MarketBrowseProtocol.Suggest,
        MarketBrowseProtocol.Facets, MarketBrowseProtocol.Detail, MarketBrowseProtocol.Buy, MarketBrowseProtocol.Fits,
    };

    /// <summary>The session's sale notices, from <see cref="Start"/> on.</summary>
    public MarketSales? Sales { get; private set; }

    public void Start(ILegACEyClient client)
    {
        // sales are heard with the Market closed too; the subscription goes with the plugin
        var sales = Sales = new MarketSales(client.ServerChannel, client.ShowToast, client.WriteChat);
        // logoff, and the start of each login, clear the server's actions: the next session starts with no unseen sales
        client.WhenServerActionsChange(() => { if (!client.SupportsAction(MarketProtocol.Account)) sales.Seen(); });
        client.RegisterStationWindow(Station, WindowId, Title, (int)WindowSize.Width, (int)WindowSize.Height, null,
            close => CreateWindow(client.ServerChannel, client.Art, client.Inventory, client.Tell, close, sales), new DerethClientTheme(), new WindowResizing(MinimumSize),
            DerethWindow.TitleBarHeight);
    }

    /// <summary>
    /// The window for one station session, built over the channel, the art and the tell path. The client opens a fresh window for each session.
    /// </summary>
    /// <param name="inventory">the character's pack and Vault, which the Sell screen lists from</param>
    /// <param name="tell">sends a tell: the recipient and the text. False when nothing was sent.</param>
    /// <param name="close">closes the window and ends the station session</param>
    /// <param name="sales">the unseen sales the Sell entries count, and the Sold tab clears</param>
    public static Control CreateWindow(IServerChannel channel, IGameArtSource art, IInventoryPort inventory, Func<string, string, bool> tell, Action close,
        MarketSales? sales = null)
    {
        MarketClient? market = null;
        MarketBrowse? browse = null;
        MarketIcons? icons = null;
        SellScreen? sell = null;
        MarketShellPanel? panel = null;
        try
        {
            market = new MarketClient(channel);
            browse = new MarketBrowse(channel);
            icons = new MarketIcons(art);
            sell = new SellScreen(new SellViewModel(market, inventory, sales), icons);
            panel = new MarketShellPanel(market, browse, icons, tell, sell, sales);
            var window = new MarketShellWindow(panel);
            window.CloseRequested += (_, _) => close();
            window.DetachedFromVisualTree += (_, _) => window.Dispose();
            market.Start();
            return window;
        }
        catch
        {
            panel?.Dispose();
            browse?.Dispose();
            sell?.Dispose();
            icons?.Dispose();
            market?.Dispose();
            throw;
        }
    }
}

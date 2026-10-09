using System;
using System.Collections.Generic;
using System.Drawing;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Vault;

/// <summary>The account Vault, opened from the Vault chest in Yaraq (the "vault" station). Its window has its own chrome.</summary>
public sealed class VaultPlugin : ILegACEyPlugin
{
    private const string WindowId = "vault";
    private const string Station = "vault";
    // Whole 50 px cells: the default 344 × 606 sits on the grid (44 px and 6 px past it), and the narrowest step is 294,
    // the first width at or above the 290 px that fits "N selected", Withdraw N and Clear on one line.
    private static readonly WindowResizing Sizing = new(new Size(290, 256), new Size(50, 50), new Size(44, 6));

    public string Name => "Vault";
    public string Version => typeof(VaultPlugin).Assembly.GetName().Version.ToString(3);
    /// <summary>
    /// The actions the Vault client requests (the client requires station.leave for every station window). vault.changed is not
    /// listed: it is a push from the server, and the server lists only the actions it registers, so requiring it would
    /// hide the Vault.
    /// </summary>
    public IReadOnlyCollection<string> RequiredActions { get; } = new[]
    {
        VaultProtocol.List, VaultProtocol.Deposit, VaultProtocol.Withdraw, VaultProtocol.Check, VaultProtocol.Move
    };

    public void Start(ILegACEyClient client) =>
        client.RegisterStationWindow(Station, WindowId, "Vault",
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight, new Point(240, 100), close => CreateWindow(client, close), new DerethClientTheme(), Sizing);

    private static Control CreateWindow(ILegACEyClient client, Action close)
    {
        // The client keeps a closed window hidden and shows it again, so this runs once per session.
        VaultClient? vaultClient = null;
        VaultShellPanel? vault = null;
        try
        {
            vaultClient = new VaultClient(client.ServerChannel);
            vault = new VaultShellPanel(client.Art, vaultClient, client.ItemDrag);
            var window = new VaultShellWindow(vault);
            window.CloseRequested += (_, _) => close();
            window.DetachedFromVisualTree += (_, _) => window.Dispose();
            return window;
        }
        catch
        {
            vault?.Dispose();
            vaultClient?.Dispose();
            throw;
        }
    }
}

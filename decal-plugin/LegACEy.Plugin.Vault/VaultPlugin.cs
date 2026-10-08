using System;
using System.Collections.Generic;
using System.Drawing;
using Avalonia.Controls;
using LegACEy.Client.Demo;

namespace LegACEy.Plugin.Vault;

/// <summary>The account Vault, opened from the LegACEy menu. Its window has its own chrome.</summary>
public sealed class VaultPlugin : ILegACEyPlugin
{
    private const string WindowId = "vault";
    private const uint MenuIcon = 0x06001020;

    public string Name => "Vault";
    public string Version => typeof(VaultPlugin).Assembly.GetName().Version.ToString(3);
    /// <summary>
    /// The actions the Vault client requests. vault.changed is not listed: it is a push from the server, and the server
    /// lists only the actions it registers, so requiring it would hide the Vault.
    /// </summary>
    public IReadOnlyCollection<string> RequiredActions { get; } = new[]
    {
        VaultProtocol.List, VaultProtocol.Deposit, VaultProtocol.Withdraw, VaultProtocol.Check, VaultProtocol.Move
    };

    public void Start(ILegACEyClient client) =>
        client.AddMenuEntry("Vault", MenuIcon, () => client.ToggleWindowWithChrome(WindowId, "Vault",
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight, new Point(240, 100), close => CreateWindow(client, close)));

    private static Control CreateWindow(ILegACEyClient client, Action close)
    {
        // The client keeps a closed window hidden and shows it again, so this runs once per session.
        VaultClient? vaultClient = null;
        VaultShellPanel? vault = null;
        try
        {
            vaultClient = new VaultClient(client.ServerChannel, () => client.CurrentSelection);
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

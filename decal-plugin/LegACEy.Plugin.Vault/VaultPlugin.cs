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
    // The window resizes freely; the grid centres its columns in the spare width. The minimum width fits "N selected", Withdraw N and
    // Clear on one line, and at the minimum height one grid row still fits with the status line shown.
    private static readonly WindowResizing Sizing = new(new Size(290, 306)) { ItemSlots = true };

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
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight, new Point(240, 100), close => CreateWindow(client, close), new DerethClientTheme(), Sizing, DerethWindow.TitleBarHeight);

    private static Control CreateWindow(ILegACEyClient client, Action close)
    {
        // The client keeps a closed window hidden and shows it again, so this runs once per session.
        VaultClient? vaultClient = null;
        VaultShellPanel? vault = null;
        try
        {
            vaultClient = new VaultClient(client.ServerChannel);
            var appraiser = vaultClient;
            void Appraise(VaultItemView item)
            {
                // An older server has no Vault appraisals: right-click then does nothing, as before.
                if (!client.SupportsAction(VaultProtocol.Appraise)) return;
                appraiser.Appraise(item.Guid, body => client.ShowAppraisal(body, item.Name,
                    new ItemVisual(item.Icon, item.Underlay, item.Overlay, unchecked((uint)item.UiEffects), item.Plate), item.StackSize));
            }
            // The game cannot select a Vault item, so a selected one is handed to the client for the E key to appraise.
            vault = new VaultShellPanel(client.Art, vaultClient, client.ItemDrag, Appraise, () => client.IsAppraisalOpen,
                item => client.SelectForExamination(item == null ? null : () => Appraise(item)));
            var window = new VaultShellWindow(vault);
            window.CloseRequested += (_, _) =>
            {
                client.SelectForExamination(null);
                close();
            };
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

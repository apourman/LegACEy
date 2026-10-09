using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Vault;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>Withdrawing a selection at once: Withdraw N and a selection dragged onto the inventory send one batch; a refused batch keeps the selection.</summary>
public sealed class VaultBatchWithdrawTests
{
    private const KeyModifiers Ctrl = KeyModifiers.Control;

    [Fact]
    public void Withdraw_N_sends_one_batch_with_every_selected_id() => RenderThread.Run(() =>
    {
        using var vault = new BatchVault();
        var selected = SelectThree(vault);

        vault.Press(vault.ButtonLabelled("Withdraw 3"));
        vault.Settle();

        Assert.Equal(selected, Assert.Single(vault.Server.Batches));
        Assert.DoesNotContain(VaultProtocol.Withdraw, vault.Server.Received);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Dragging_a_selected_item_onto_the_inventory_sends_one_batch_of_the_whole_selection() => RenderThread.Run(() =>
    {
        using var vault = new BatchVault();
        var selected = SelectThree(vault);

        var start = vault.Center(vault.Cells[4]);
        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 30, start.Y);
        vault.Host.PointerMove(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Host.PointerUp(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Settle();

        Assert.Equal(selected, Assert.Single(vault.Server.Batches));
        Assert.DoesNotContain(VaultProtocol.Withdraw, vault.Server.Received);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void The_drag_icon_carries_the_count_of_a_selection_drag() => RenderThread.Run(() =>
    {
        using var vault = new BatchVault();
        SelectThree(vault);

        var start = vault.Center(vault.Cells[4]);
        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 30, start.Y);
        Assert.Equal(3, Assert.Single(vault.Drag.StacksShown));

        vault.Host.PointerMove(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Host.PointerUp(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Settle();
        Assert.Equal(0, vault.Drag.IconsOpen);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void A_refused_batch_keeps_the_selection_and_shows_the_reason_without_reloading_the_page() => RenderThread.Run(() =>
    {
        using var vault = new BatchVault();
        SelectThree(vault);
        const string reason = "Your pack has no room for all 3 items. Nothing was withdrawn.";
        vault.Server.BatchRefusal = reason;
        var requestsBefore = vault.Server.ListRequests.Count;

        vault.Press(vault.ButtonLabelled("Withdraw 3"));
        vault.Settle();

        Assert.Equal(new[] { 2, 4, 7 }, vault.Client.Selection.Indices);
        Assert.Contains(reason, vault.Texts());
        Assert.Equal(requestsBefore, vault.Server.ListRequests.Count); // no Refresh: an accepted list reply would clear the selection
        Assert.Equal(317, vault.Server.Items.Count);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void An_accepted_batch_reloads_the_page_and_clears_the_selection() => RenderThread.Run(() =>
    {
        using var vault = new BatchVault();
        SelectThree(vault);

        vault.Press(vault.ButtonLabelled("Withdraw 3"));
        vault.Settle();

        Assert.Empty(vault.Client.Selection.Indices);
        Assert.Equal(314, vault.Server.Items.Count);
        Assert.Equal(314, vault.Client.Snapshot!.VaultCount);
        Assert.Contains("314 / 1,000", vault.Texts());
        Assert.Contains("3 items are back in your pack.", vault.Texts());
        Assert.Null(vault.Host.LastError);
    });

    /// <summary>Selects places 2, 4 and 7 of the page: a click, then two Ctrl-clicks. Returns the three ids, read before anything is withdrawn.</summary>
    private static uint[] SelectThree(BatchVault vault)
    {
        vault.Press(vault.Cells[2]);
        vault.Press(vault.Cells[4], Ctrl);
        vault.Press(vault.Cells[7], Ctrl);
        return new[] { vault.Server.Items[2].Guid, vault.Server.Items[4].Guid, vault.Server.Items[7].Guid };
    }

    /// <summary>A live Vault of 317 items in the default window, with the Withdraw N button to press.</summary>
    private sealed class BatchVault : VaultFixture
    {
        public BatchVault() : base(Vault(317)) { }

        public Button ButtonLabelled(string text) =>
            Window.GetVisualDescendants().OfType<DerethButton>().Single(button => button.Content is TextBlock label && label.Text == text);
    }
}

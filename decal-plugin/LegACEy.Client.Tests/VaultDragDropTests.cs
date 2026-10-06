using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;

namespace LegACEy.Client.Tests;

public sealed class VaultDragDropTests
{
    [Fact]
    public void Retail_item_dragged_over_a_cell_shows_the_drop_indicator_and_deposits_on_release() => RenderThread.Run(() =>
    {
        using var vault = new LiveVault();
        var window = vault.Window;
        var target = vault.Cells[10]; // an empty cell
        var over = vault.Center(target);

        window.RetailDragOver(0x50000099, "Fine Sword", over);
        vault.Host.Tick();

        var indicator = Assert.Single(vault.VisibleIndicators());
        Assert.Same(vault.SlotOf(target), indicator.Parent);
        Assert.Contains("Release to deposit Fine Sword", vault.Texts());

        // Over the window but not over a cell: no indicator, and the hint says where to drop.
        window.RetailDragOver(0x50000099, "Fine Sword", new Point(30, 440));
        vault.Host.Tick();
        Assert.Empty(vault.VisibleIndicators());
        Assert.Contains("Drop Fine Sword on a vault cell to deposit it", vault.Texts());

        window.RetailDragOver(0x50000099, "Fine Sword", over);
        Assert.True(window.RetailDrop(0x50000099, "Fine Sword", over));
        vault.Step(TimeSpan.FromMilliseconds(30));

        Assert.Equal(VaultProtocol.Deposit, vault.Server.Received.Last());
        Assert.Empty(vault.VisibleIndicators());
        Assert.True(vault.Client.TransferPending);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Retail_item_released_off_the_cells_is_not_deposited_and_leaving_clears_the_indicator() => RenderThread.Run(() =>
    {
        using var vault = new LiveVault();
        var over = vault.Center(vault.Cells[2]);

        vault.Window.RetailDragOver(7, "Gem", over);
        vault.Window.RetailDragOver(7, "Gem", null);
        vault.Host.Tick();
        Assert.Empty(vault.VisibleIndicators());

        Assert.False(vault.Window.RetailDrop(7, "Gem", new Point(30, 440)));
        vault.Step(TimeSpan.FromMilliseconds(30));
        Assert.DoesNotContain(VaultProtocol.Deposit, vault.Server.Received);
        Assert.Contains("Drop the item on a vault cell to deposit it.", vault.Texts());
    });

    [Fact]
    public void Dragging_an_item_onto_the_inventory_withdraws_it_and_shows_its_icon_while_dragging() => RenderThread.Run(() =>
    {
        using var vault = new LiveVault();
        var cell = vault.Cells[1];
        var start = vault.Center(cell);

        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 30, start.Y);
        Assert.Equal(1, vault.Drag.IconsOpen);
        Assert.Equal(new uint[] { 0x060011F3, 0x06000FAD }, vault.Drag.IconsShown.Single());
        vault.Host.PointerMove(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Host.PointerUp(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Step(TimeSpan.FromMilliseconds(30));

        Assert.Equal(0, vault.Drag.IconsOpen);
        Assert.Equal(VaultProtocol.Withdraw, vault.Server.Received.Last());
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Dropping_inside_the_window_or_away_from_the_inventory_withdraws_nothing() => RenderThread.Run(() =>
    {
        using var vault = new LiveVault();
        var start = vault.Center(vault.Cells[0]);

        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 40, start.Y + 40);
        vault.Host.PointerUp(start.X + 40, start.Y + 40); // onto another vault cell: a move, not a withdrawal
        vault.Step(TimeSpan.FromMilliseconds(30));

        vault.Drag.Target = ItemDropTarget.InventoryClosed;
        start = vault.Center(vault.Cells[0]);
        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(-60, start.Y);
        vault.Host.PointerUp(-60, start.Y);
        vault.Step(TimeSpan.FromMilliseconds(30));

        Assert.DoesNotContain(VaultProtocol.Withdraw, vault.Server.Received);
        Assert.Equal(0, vault.Drag.IconsOpen);
        Assert.Contains("Open your inventory, then drop the item on it to withdraw it.", vault.Texts());
    });

    [Fact]
    public void Refused_retail_item_shows_a_red_indicator_with_the_reason_and_is_not_deposited() => RenderThread.Run(() =>
    {
        using var vault = new LiveVault();
        vault.Server.Refused[0x50000077] = "Attuned items can't go in the Vault.";
        var over = vault.Center(vault.Cells[0]);

        vault.Window.RetailDragOver(0x50000077, "Bound Ring", over);
        vault.Step(TimeSpan.FromMilliseconds(30)); // the deposit check comes back

        var indicator = Assert.Single(vault.VisibleIndicators());
        Assert.Equal(Avalonia.Media.Color.Parse("#D9584A"), ((Avalonia.Media.ISolidColorBrush)indicator.BorderBrush!).Color);
        Assert.Contains("Attuned items can't go in the Vault.", vault.Texts());

        Assert.False(vault.Window.RetailDrop(0x50000077, "Bound Ring", over));
        vault.Step(TimeSpan.FromMilliseconds(30));
        Assert.DoesNotContain(VaultProtocol.Deposit, vault.Server.Received);
    });

    [Fact]
    public void Lifting_a_vault_item_onto_another_cell_moves_it_there_with_the_hover_indicator() => RenderThread.Run(() =>
    {
        using var vault = new LiveVault();
        var first = vault.Client.Snapshot!.Items[0];
        var start = vault.Center(vault.Cells[0]);
        var target = vault.Center(vault.Cells[4]);

        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 20, start.Y);
        vault.Host.PointerMove(target.X, target.Y);
        vault.Host.Tick();

        Assert.Equal(0.4, vault.Cells[0].Opacity);
        var indicator = Assert.Single(vault.VisibleIndicators());
        Assert.Same(vault.Cells[4], indicator.Parent);
        Assert.Contains($"Release to move {first.Name} here", vault.Texts());

        vault.Host.PointerUp(target.X, target.Y);
        Assert.Equal(first.Guid, vault.Client.Snapshot.Items[4].Guid); // at once, before the server answers
        vault.Step(TimeSpan.FromMilliseconds(30));
        vault.Step(TimeSpan.FromMilliseconds(30));

        Assert.Contains(VaultProtocol.Move, vault.Server.Received);
        Assert.Equal(first.Guid, vault.Server.Items[4].Guid);
        Assert.Equal(first.Guid, vault.Client.Snapshot!.Items[4].Guid);
        Assert.DoesNotContain(VaultProtocol.Withdraw, vault.Server.Received);
        Assert.Empty(vault.VisibleIndicators());
        Assert.All(vault.Cells, cell => Assert.Equal(1, cell.Opacity));
    });

    private sealed class LiveVault : IDisposable
    {
        private DateTime _now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        private readonly VaultShellPanel _panel;
        private readonly ServerChannelClient _channel;

        public LiveVault()
        {
            Server = new FakeVaultServer(() => _now) { Latency = TimeSpan.FromMilliseconds(30) };
            _channel = new ServerChannelClient(Server, () => _now);
            Server.Deliver = _channel.Receive;
            Client = new VaultClient(_channel);
            VaultShellPanel? panel = null;
            Host = AvaloniaPanel.Create(() => new VaultShellWindow(panel = new VaultShellPanel(new NoArt(), Client, Drag)),
                VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
            _panel = panel!;
            Step(TimeSpan.FromMilliseconds(30));
            Step(TimeSpan.FromMilliseconds(30));
            Assert.Equal(VaultConnection.Live, Client.Connection);
        }

        public FakeVaultServer Server { get; }
        public VaultClient Client { get; }
        public FakeItemDragHost Drag { get; } = new();
        public AvaloniaPanel Host { get; }
        public VaultShellWindow Window => (VaultShellWindow)Host.Content;

        public Control[] Cells => Host.Content.GetVisualDescendants().OfType<UniformGrid>().Single().Children.ToArray();
        public Control SlotOf(Control cell) => cell;
        public Border[] VisibleIndicators() => Cells.SelectMany(cell => ((Grid)cell).Children.OfType<Border>()).Where(border => border.IsVisible).ToArray();
        public string[] Texts() => Host.Content.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray();

        public Point Center(Control control) =>
            control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Host.Content)!.Value;

        public void Step(TimeSpan time)
        {
            _now += time;
            Server.Pump();
            _channel.Tick();
            Host.Tick();
        }

        public void Dispose()
        {
            _panel.Dispose();
            Host.Dispose();
        }
    }

    private sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}

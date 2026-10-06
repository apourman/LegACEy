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

    [Theory]
    [InlineData(0)]
    [InlineData(12)] // taller in-game fonts leave the grid area shorter
    public void Drop_frame_on_the_top_row_is_fully_visible_without_scrolling(int heightLost) => RenderThread.Run(() =>
    {
        using var vault = new LiveVault(VaultShellPanel.WindowHeight - heightLost);
        var scroller = vault.Host.Content.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(scroller.Extent.Height <= scroller.Viewport.Height + 0.5, $"extent {scroller.Extent.Height} > viewport {scroller.Viewport.Height}");
        Assert.Equal(0, scroller.Offset.Y);

        for (var column = 0; column < 6; column++)
        {
            vault.Window.RetailDragOver(5, "Gem", vault.Center(vault.Cells[column]));
            vault.Host.Tick();
            var indicator = Assert.Single(vault.VisibleIndicators());
            // The space the grid area is given: a scroll area taller than it is centred and clipped by it.
            var area = (Visual)scroller.GetVisualParent()!;
            var top = indicator.TranslatePoint(default, area)!.Value;
            Assert.True(top.Y >= 1, $"frame top {top.Y} is at or above the grid area's edge");
            Assert.True(scroller.Bounds.Height <= area.Bounds.Height + 0.5, $"scroll area {scroller.Bounds.Height} is taller than its space {area.Bounds.Height}");
        }
    });

    [Fact]
    public void Lifted_item_hover_works_when_the_drag_icon_is_its_own_panel_as_in_game() => RenderThread.Run(() =>
    {
        var icons = new PanelDragHost();
        using var vault = new LiveVault(dragHost: icons);
        var start = vault.Center(vault.Cells[0]);
        var target = vault.Center(vault.Cells[4]);
        var empty = vault.Center(vault.Cells[15]);

        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 20, start.Y);
        Assert.NotNull(icons.Panel);
        vault.Host.PointerMove(target.X, target.Y);
        HostFrame();
        Assert.Same(vault.Cells[4], Assert.Single(vault.VisibleIndicators()).Parent);

        vault.Host.PointerMove(empty.X, empty.Y);
        HostFrame();
        Assert.Same(vault.Cells[15], Assert.Single(vault.VisibleIndicators()).Parent);

        vault.Host.PointerUp(empty.X, empty.Y);
        Assert.Null(icons.Panel);

        // What the plugin does every render frame: report that no retail drag is in progress.
        void HostFrame()
        {
            vault.Window.RetailDragOver(0, string.Empty, null);
            vault.Host.Tick();
        }
    });

    /// <summary>Like the plugin's drag host: the icon is a separate Avalonia panel created when the drag starts.</summary>
    private sealed class PanelDragHost : IItemDragHost
    {
        public AvaloniaPanel? Panel { get; private set; }
        public IDisposable ShowDragIcon(IReadOnlyList<uint> iconLayers)
        {
            Panel = AvaloniaPanel.Create(() => new Grid { Width = 32, Height = 32 }, 32, 32);
            return new Icon(this);
        }
        public ItemDropTarget DropTargetAtPointer() => ItemDropTarget.Elsewhere;
        private sealed class Icon : IDisposable
        {
            private readonly PanelDragHost _owner;
            public Icon(PanelDragHost owner) => _owner = owner;
            public void Dispose() { _owner.Panel?.Dispose(); _owner.Panel = null; }
        }
    }

    private sealed class LiveVault : IDisposable
    {
        private DateTime _now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        private readonly VaultShellPanel _panel;
        private readonly ServerChannelClient _channel;

        public LiveVault(int height = VaultShellPanel.WindowHeight, IItemDragHost? dragHost = null)
        {
            Server = new FakeVaultServer(() => _now) { Latency = TimeSpan.FromMilliseconds(30) };
            _channel = new ServerChannelClient(Server, () => _now);
            Server.Deliver = _channel.Receive;
            Client = new VaultClient(_channel);
            VaultShellPanel? panel = null;
            Host = AvaloniaPanel.Create(() => new VaultShellWindow(panel = new VaultShellPanel(new NoArt(), Client, dragHost ?? Drag)),
                VaultShellPanel.WindowWidth, height);
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

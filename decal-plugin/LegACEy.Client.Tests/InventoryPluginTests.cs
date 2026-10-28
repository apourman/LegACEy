using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Inventory;
using Xunit;
using static LegACEy.Client.Tests.PanelFrameAssert;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace LegACEy.Client.Tests;

/// <summary>
/// The inventory plugin on a fake client. The client's window placement is the real <see cref="WindowManager"/> over a placements
/// store, so the sizes and positions each window keeps are the ones the client keeps. The windows are real headless panels.
/// </summary>
public sealed class InventoryPluginTests
{
    // The window's id, kept from when it was the vertical of two layouts.
    private const string Full = "inventory-vertical";

    // The plugin's settings value: the Slots toggle is 2. 4 was the collapsed equipment section, which is gone.
    private const int SlotsOff = 0;
    private const int SlotsOn = 2;
    private const int OldCollapsed = 4;

    [Fact]
    public void Retail_opening_its_panel_shows_the_layout_window_and_the_close_box_closes_retail() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        new InventoryPlugin().Start(client);
        Assert.Null(client.Windows.Get(Full));

        client.Retail!.Retail(true);
        Assert.NotNull(client.Windows.Get(Full));

        var window = client.Panel(Full);
        // The close box is the header's one button (the doll's expand button is not in the header).
        InventoryDriver.Press(window, window.Content!.GetVisualDescendants().OfType<DerethHeaderButton>().Single(button => button.Name != "ExpandDoll"));

        Assert.Null(client.Windows.Get(Full));
        Assert.Equal(1, client.Retail.CloseCalls);
        Assert.False(client.Retail.Open);
        // The window stands in for retail's panel, so it has no menu entry of its own.
        Assert.Empty(client.MenuEntries);
    });

    [Fact]
    public void The_slots_choice_comes_back_from_the_settings_file_after_a_restart() => RenderThread.Run(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "legacey-inventory-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using (var first = new FakeInventoryClient(new FileWindowPositionStore(path)))
            {
                new InventoryPlugin().Start(first);
                first.Retail!.Toggle();
                InventoryDriver.PressSlots(first.Panel(Full));
                Assert.Equal(SlotsOff, first.Settings);
            }

            // A new session reads the same file: the window opens with the Slots toggle still off.
            using var second = new FakeInventoryClient(new FileWindowPositionStore(path));
            new InventoryPlugin().Start(second);
            second.Retail!.Toggle();
            var window = second.Panel(Full);
            Assert.Null(InventoryDriver.SlotOrNull(window, PaperdollSlot.Head));
            Assert.NotNull(InventoryDriver.SlotOrNull(window, PaperdollSlot.Neck));
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void Settings_follow_the_character_logged_in_when_the_window_opens() => RenderThread.Run(() =>
    {
        var store = new MemoryWindowPositionStore();
        using var client = new FakeInventoryClient(store);
        new InventoryPlugin().Start(client);
        client.SaveSettings(SlotsOn);
        client.Retail!.Toggle();
        Assert.NotNull(client.Windows.Get(Full));
        client.Retail!.Toggle();
        Assert.Null(client.Windows.Get(Full));

        // Another character logs in with its Slots toggle off, saved when the equipment section could be collapsed: the next open
        // follows that character, not the cached one, and shows the whole window.
        client.Character = "Other";
        store.Save("Server", "Other", "settings:Inventory", (new Point(OldCollapsed | SlotsOff, 0), null));
        client.Retail!.Toggle();
        var window = client.Panel(Full);
        Assert.Null(InventoryDriver.SlotOrNull(window, PaperdollSlot.Head));
        Assert.NotNull(InventoryDriver.SlotOrNull(window, PaperdollSlot.Neck));
    });

    [Fact]
    public void A_hidden_window_draws_what_changed_while_it_was_hidden() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        new InventoryPlugin().Start(client);
        client.Retail!.Toggle();
        client.Retail!.Toggle();

        // The port opens a pack while the window is hidden; back on screen, it shows that pack.
        client.Port.Push(InventorySample.Snapshot(openContainer: InventorySample.Potions));
        client.Retail!.Toggle();
        var cells = InventoryDriver.Slots(client.Panel(Full)).Where(slot => InventoryDriver.Id(slot).Place == SlotPlace.Cell).ToArray();
        Assert.Equal(24, cells.Length);
        Assert.All(cells, cell => Assert.Equal(InventorySample.Potions, InventoryDriver.Id(cell).Container));
    });

    [Fact]
    public void The_doll_s_expand_button_inspects_the_character_and_shows_only_with_the_model() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = new ServerChannelClient(new LookTransport()) };
        client.ServerActions.Add(PaperdollProtocol.Look);
        client.SaveSettings(SlotsOff);
        new InventoryPlugin().Start(client);
        client.Retail!.Toggle();
        var expand = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(client.Panel(Full).Content).OfType<DerethHeaderButton>().Single(b => b.Name == "ExpandDoll");
        Assert.True(expand.IsVisible);

        expand.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { 0u }, client.Inspected);

        client.Retail.Toggle();
        Assert.False(expand.IsVisible);
    });

    [Fact]
    public void With_the_look_on_the_server_and_slots_off_the_doll_area_hosts_the_model_and_asks_for_the_look_while_shown() => RenderThread.Run(() =>
    {
        var transport = new LookTransport();
        var channel = new ServerChannelClient(transport);
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = channel };
        client.ServerActions.Add(PaperdollProtocol.Look);
        client.SaveSettings(SlotsOff);
        new InventoryPlugin().Start(client);
        client.Retail!.Toggle();

        var panel = client.Panel(Full);
        var model = Assert.Single(ModelViews(panel));
        Assert.Equal(new[] { PaperdollProtocol.Look }, transport.Actions);

        // The model takes the height beside the aetheria row and stays above the Slots toggle, which the host draws it over.
        InventoryDriver.Tick(panel);
        var bounds = OnPanel(model, panel);
        Assert.True(bounds.Top < OnPanel(InventoryDriver.SlotOrNull(panel, PaperdollSlot.AetheriaOne)!, panel).Bottom);
        Assert.False(bounds.Intersects(OnPanel(InventoryDriver.SlotsToggle(panel), panel)));

        // The server says the look changed, as it does when the player equips something: the doll asks again.
        channel.Receive(LookChanged());
        Assert.Equal(2, transport.Actions.Count);

        // Hidden, the window stops asking; shown again, it asks.
        client.Retail!.Toggle();
        Assert.Null(client.Windows.Get(Full));
        channel.Receive(LookChanged());
        Assert.Equal(2, transport.Actions.Count);
        client.Retail!.Toggle();
        Assert.Equal(3, transport.Actions.Count);
    });

    [Fact]
    public void On_any_server_the_doll_shows_the_look_the_game_sent() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        client.LookCache.Receive(LookCache.CreateObject, LookCacheTests.Created(FakeInventoryClient.Player, setup: 0x02000001));
        client.SaveSettings(SlotsOff);
        new InventoryPlugin().Start(client);
        client.Retail!.Toggle();

        Assert.Single(ModelViews(client.Panel(Full)));
    });

    [Fact]
    public void A_window_opened_before_the_server_lists_the_look_gets_its_doll_when_the_list_arrives() => RenderThread.Run(() =>
    {
        var transport = new LookTransport();
        var channel = new ServerChannelClient(transport);
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = channel };
        client.SaveSettings(SlotsOff);
        new InventoryPlugin().Start(client);
        client.Retail!.Toggle();
        // Without the look on the server the doll area is a plain panel and asks for nothing.
        Assert.Empty(ModelViews(client.Panel(Full)));
        channel.Receive(LookChanged());
        Assert.Empty(transport.Actions);

        // channel.hello lands after the window opened, as it does at login: the open window gets the doll and asks for the look.
        client.Hello(PaperdollProtocol.Look);
        Assert.Single(ModelViews(client.Panel(Full)));
        Assert.Equal(new[] { PaperdollProtocol.Look }, transport.Actions);

        // Turned and zoomed, then closed and opened again: the doll starts from its first view.
        var view = ModelViews(client.Panel(Full)).Single();
        view.Zoom = 3;
        view.Focus = 0.9f;
        client.Retail!.Toggle();
        client.Retail!.Toggle();
        Assert.Equal((1f, 0.5f), (view.Zoom, view.Focus));
    });

    [Fact]
    public void A_reply_that_arrives_after_the_doll_left_the_window_changes_nothing() => RenderThread.Run(() =>
    {
        var transport = new LookTransport();
        var channel = new ServerChannelClient(transport);
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = channel };
        client.ServerActions.Add(PaperdollProtocol.Look);
        client.SaveSettings(SlotsOff);
        new InventoryPlugin().Start(client);
        client.Retail!.Toggle();
        var status = (TextBlock)ModelViews(client.Panel(Full)).Single().Child!;

        client.Retail!.Toggle();   // hides the window, and with it the doll
        // A reply the server sent before the hide, with a body this client cannot read: it must not reach the status line.
        channel.Receive(ChannelWire.EncodeEvent(ChannelEventKind.Reply, ChannelStatus.Ok, transport.Ids.Single(), PaperdollProtocol.Look, new byte[] { 1 }));
        Assert.Equal("Loading…", status.Text);
    });

    [Fact]
    public void With_slots_on_no_model_shows_and_no_look_is_asked_for_until_slots_go_off_again() => RenderThread.Run(() =>
    {
        var transport = new LookTransport();
        var channel = new ServerChannelClient(transport);
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = channel };
        client.ServerActions.Add(PaperdollProtocol.Look);
        client.SaveSettings(SlotsOn);
        new InventoryPlugin().Start(client);
        client.Retail!.Toggle();

        var panel = client.Panel(Full);
        Assert.Empty(ModelViews(panel));
        channel.Receive(LookChanged());
        Assert.Empty(transport.Actions);

        InventoryDriver.PressSlots(panel);
        Assert.Single(ModelViews(panel));
        Assert.Single(transport.Actions);

        // Slots back on: the model goes, and a later change asks the server for nothing.
        InventoryDriver.PressSlots(panel);
        Assert.Empty(ModelViews(panel));
        channel.Receive(LookChanged());
        Assert.Single(transport.Actions);
    });

    /// <summary>
    /// Every visible control lies inside the window at the minimum and default sizes the plugin asks for, with the full sample drawn,
    /// and the pack grid shows at least one row. Run it again with the Windows fonts (FONTCONFIG_FILE) to check the taller text.
    /// </summary>
    [Fact]
    public void Nothing_spills_outside_the_window_at_the_minimum_and_default_sizes() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        client.Port.Push(InventorySample.Snapshot(openContainer: InventorySample.Potions, selected: InventorySample.BluePotion));
        client.SaveSettings(new InventorySettings(showSlots: true).ToInt());
        new InventoryPlugin().Start(client);
        client.Retail!.Toggle();

        var requested = client.Requested[Full];
        var panel = client.Panel(Full);
        Assert.Equal(new Size(requested.Width, requested.Height), client.Windows.Get(Full)!.Size);
        AssertFitsWithOneGridRow(panel);

        var minimum = requested.Resizing!.Minimum;
        panel.Resize(minimum.Width, minimum.Height);
        InventoryDriver.Tick(panel);
        AssertFitsWithOneGridRow(panel);
    });

    /// <summary>
    /// The vertical window opens as retail's panel does: the main pack six cells across and eleven down, titled with the character's name.
    /// Its width is fixed, and the grid has no spare width beside its columns.
    /// </summary>
    [Fact]
    public void The_vertical_window_opens_with_retail_s_six_by_eleven_main_pack_and_the_character_s_name() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { Art = new InventorySample.SolidArt() };
        client.Port.Push(InventorySample.Snapshot(characterName: "R o Z"));
        new InventoryPlugin().Start(client);
        client.Retail!.Toggle();

        var (width, _, resizing) = client.Requested[Full];
        // Retail's toolbar window width, as the Dereth toolbar's, so the two line up stacked.
        Assert.Equal(RetailToolbar.RetailWidth, width);
        Assert.Equal(width, resizing!.Minimum.Width);
        Assert.Equal(width, resizing.Maximum!.Value.Width);
        var panel = client.Panel(Full);
        InventoryDriver.Tick(panel);
        var grid = Assert.Single(panel.Content.GetVisualDescendants().OfType<DerethSlotGrid>());
        var pitch = grid.ColumnPitch;
        Assert.Equal(6, grid.Cells.Take(12).Select(cell => cell.Bounds.X).Distinct().Count());
        var viewport = Assert.Single(grid.GetVisualDescendants().OfType<ScrollViewer>()).Viewport.Width;
        Assert.InRange(viewport - 6 * pitch, 0, pitch / 2);
        Assert.InRange(grid.Bounds.Height, 11 * pitch, 11.5 * pitch);
        Assert.Contains(panel.Content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Inventory of R o Z");

        // The pack column stands right of the paperdoll and the grid, clear of both.
        var slots = InventoryDriver.Slots(panel);
        var dollRight = slots.Where(slot => InventoryDriver.Id(slot).Place == SlotPlace.Paperdoll).Max(slot => OnPanel(slot, panel).Right);
        var packsLeft = slots.Where(slot => InventoryDriver.Id(slot).Place == SlotPlace.Pack).Min(slot => OnPanel(slot, panel).Left);
        Assert.True(packsLeft > dollRight, "The pack column overlaps the paperdoll.");
        Assert.True(packsLeft > OnPanel(grid, panel).Right, "The pack column overlaps the grid.");

        // Icons are never scaled down: each one, the header's and the pyreal's too, draws at its native size on the panel.
        var icons = panel.Content.GetVisualDescendants().OfType<DerethItemIcon>().ToArray();
        Assert.NotEmpty(icons);
        foreach (var icon in icons)
        {
            var drawn = icon.TranslatePoint(new Avalonia.Point(icon.Bounds.Width, icon.Bounds.Height), panel.Content)!.Value - icon.TranslatePoint(default, panel.Content)!.Value;
            Assert.Equal(ItemCellSize.Standard.Icon, drawn.X, 1);
            Assert.Equal(ItemCellSize.Standard.Icon, drawn.Y, 1);
        }
    });

    [Fact]
    public void A_long_character_name_drops_the_inventory_prefix_to_fit_the_header() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        client.Port.Push(InventorySample.Snapshot(characterName: "Sir Reginald Ashbourne the Unyielding"));
        new InventoryPlugin().Start(client);
        client.Retail!.Toggle();
        var panel = client.Panel(Full);
        InventoryDriver.Tick(panel);

        // The title shares the header with the close box: one row, so a long name never makes the header taller.
        var title = Assert.Single(panel.Content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Sir Reginald Ashbourne the Unyielding");
        Assert.InRange(title.FontSize, 11, 13);
        Assert.True(OnPanel(title, panel).Bottom <= DerethWindow.TitleBarHeight, "The title sits below the header.");
    });

    private static void AssertFitsWithOneGridRow(AvaloniaPanel panel)
    {
        Assert.Null(panel.LastError);
        AssertNothingOutsideFrame(panel);
        var grid = Assert.Single(panel.Content.GetVisualDescendants().OfType<DerethSlotGrid>());
        Assert.True(grid.Bounds.Height >= ItemCellSize.Standard.Cell, $"The grid is {grid.Bounds.Height} px tall, less than one row.");
    }

    private static IEnumerable<ModelView> ModelViews(AvaloniaPanel panel) => panel.Content.GetVisualDescendants().OfType<ModelView>();

    /// <summary>A control's bounds in the panel's coordinates.</summary>
    private static Rect OnPanel(Visual visual, AvaloniaPanel panel) => new(visual.TranslatePoint(default, panel.Content)!.Value, visual.Bounds.Size);

    /// <summary>The paperdoll.changed push, as the server sends it after an equipment change.</summary>
    private static byte[] LookChanged() => ChannelWire.EncodeEvent(ChannelEventKind.Push, ChannelStatus.Ok, 0, PaperdollProtocol.Changed, Array.Empty<byte>());

    /// <summary>A placements store that keeps nothing, as a read-only install path does: every save is dropped without an error.</summary>
    private sealed class UnwritablePositionStore : IWindowPositionStore
    {
        public (Point Location, Size? Size)? Load(string server, string character, string windowId) => null;
        public void Save(string server, string character, string windowId, (Point Location, Size? Size) placement)
        {
        }
    }

    /// <summary>Records the action of every request the channel sends; nothing answers them.</summary>
    private sealed class LookTransport : IServerChannelTransport
    {
        public List<string> Actions { get; } = new();
        public List<uint> Ids { get; } = new();
        public bool IsAvailable => true;

        public bool Send(byte[] requestPayload)
        {
            ChannelWire.TryDecodeRequest(requestPayload, out var id, out var action, out _);
            Ids.Add(id);
            Actions.Add(action);
            return true;
        }
    }

    /// <summary>A client for the inventory plugin: a real <see cref="WindowManager"/> places its windows, and each window's panel is kept across hiding.</summary>
    private sealed class FakeInventoryClient : ILegACEyClient, IDisposable
    {
        // The fake keeps the plugin's one settings value in the store it is given, under this key.
        private const string SettingsKey = "settings:Inventory";
        private readonly IWindowPositionStore _store;
        private readonly Dictionary<string, AvaloniaPanel> _panels = new(StringComparer.Ordinal);

        public FakeInventoryClient(IWindowPositionStore store)
        {
            _store = store;
            Windows = new WindowManager(new Size(1920, 1080), store, "Server", "Character");
        }

        public WindowManager Windows { get; }
        public FakeInventoryPort Port { get; } = new();
        public List<(string Title, uint Icon, Action Action)> MenuEntries { get; } = new();
        /// <summary>The size and minimum each window was asked for, by id.</summary>
        public Dictionary<string, (int Width, int Height, WindowResizing? Resizing)> Requested { get; } = new(StringComparer.Ordinal);

        /// <summary>The actions the server registered, as channel.hello lists them.</summary>
        public HashSet<string> ServerActions { get; } = new(StringComparer.Ordinal);
        private Action? _serverActionsChanged;

        /// <summary>channel.hello answered: the actions are now the server's, and the plugin hears about it.</summary>
        public void Hello(params string[] actions)
        {
            ServerActions.UnionWith(actions);
            _serverActionsChanged?.Invoke();
        }

        public void WhenServerActionsChange(Action changed) => _serverActionsChanged += changed;

        /// <summary>The character logged in now. Its settings are kept per character; its window placements are not in this fake.</summary>
        public string Character { get; set; } = "Character";

        /// <summary>The plugin's saved settings value for the character logged in now, as the store holds it.</summary>
        public int? Settings => _store.Load("Server", Character, SettingsKey)?.Location.X;

        /// <summary>The panel of a window, open or hidden.</summary>
        public AvaloniaPanel Panel(string id) => _panels[id];

        public IServerChannel ServerChannel { get; set; } = UnavailableServerChannel.Instance;
        public const uint Player = 0x50000001;
        public LookCache LookCache { get; } = new();
        public ILookSource Looks => new GameLooks(LookCache, () => ServerChannel, SupportsAction, () => Player);
        public string PortalPath => string.Empty;
        public IGameArtSource Art { get; init; } = new InventorySample.NoArt();
        public IItemDragHost ItemDrag { get; } = new FakeItemDragHost();
        public IInventoryPort Inventory => Port;
        /// <summary>The retail inventory panel the plugin took over, once it has.</summary>
        public FakeRetailPanel? Retail { get; private set; }
        public IRetailPanel TakeOverRetailInventory(Action<bool> retailOpenChanged)
        {
            Retail = new FakeRetailPanel(retailOpenChanged);
            return Retail;
        }
        public bool SupportsAction(string action) => ServerActions.Contains(action);
        public bool Tell(string recipient, string text) => false;
        public void ShowToast(string title, string line) { }
        public void WriteChat(string text) { }
        public int? LoadSettings() => Settings;
        public void SaveSettings(int value) => _store.Save("Server", Character, SettingsKey, (new Point(value, 0), null));
        public void AddMenuEntry(string title, uint iconId, Action action) => MenuEntries.Add((title, iconId, action));
        public void ShowAppraisal(byte[] appraisal, string name, ItemVisual? visual, int stackCount) { }
        public void Inspect(uint objectId, string name, ItemVisual? visual) => Inspected.Add(objectId);
        public List<uint> Inspected { get; } = new();
        public bool IsAppraisalOpen => false;
        public void SelectForExamination(Action? examine) { }
        public void ToggleWindow(string id, string title, int width, int height, Point defaultLocation, Func<Control> createContent) =>
            throw new NotSupportedException();

        public void ToggleWindowWithChrome(string id, string title, int width, int height, Point defaultLocation, Func<Action, Control> createWindow,
            IClientTheme? theme = null, WindowResizing? resizing = null, int titleBarHeight = 28, string? sharesLocationWith = null)
        {
            Requested[id] = (width, height, resizing);
            if (Windows.Get(id) != null)
            {
                Windows.Close(id);
                return;
            }
            var window = Windows.Open(new WindowDefinition(id, title, width, height, titleBarHeight, theme, resizing, sharesLocationWith), defaultLocation);
            if (_panels.TryGetValue(id, out var panel))
            {
                panel.Resize(window.Width, window.Height);
                return;
            }
            panel = AvaloniaPanel.Create(() => createWindow(() => Windows.Close(id)), window.Width, window.Height);
            if (theme != null) panel.ApplyTheme(theme);
            _panels[id] = panel;
        }

        public void RegisterStationWindow(string station, string id, string title, int width, int height, Point? defaultLocation, Func<Action, Control> createWindow,
            IClientTheme? theme = null, WindowResizing? resizing = null, int titleBarHeight = 28) => throw new NotSupportedException();

        public void Dispose()
        {
            foreach (var panel in _panels.Values) panel.Dispose();
            _panels.Clear();
        }
    }
}

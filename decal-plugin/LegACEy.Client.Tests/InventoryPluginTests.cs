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
/// store, so the sizes and positions each layout keeps are the ones the client keeps. The windows are real headless panels.
/// </summary>
public sealed class InventoryPluginTests
{
    private const string Vertical = "inventory-vertical";
    private const string Horizontal = "inventory-horizontal";

    // Vertical is 0 and horizontal is 1 in the plugin's settings value; the Slots toggle adds 2.
    private const int VerticalSlotsOff = 0;
    private const int HorizontalSlotsOn = 3;
    private const int VerticalSlotsOn = 2;
    private const int HorizontalSlotsOff = 1;

    [Fact]
    public void Switching_layout_hides_one_window_and_opens_the_other_at_its_own_saved_size() => RenderThread.Run(() =>
    {
        var positions = new MemoryWindowPositionStore();
        positions.Save("Server", "Character", Vertical, (new Point(100, 100), new Size(380, 600)));
        positions.Save("Server", "Character", Horizontal, (new Point(120, 120), new Size(700, 500)));
        using var client = new FakeInventoryClient(positions);
        new InventoryPlugin().Start(client);
        client.MenuEntries.Single().Action();
        Assert.Equal(new Size(380, 600), client.Windows.Get(Vertical)!.Size);

        InventoryDriver.PressLayout(client.Panel(Vertical), InventoryLayout.Horizontal);
        Assert.Null(client.Windows.Get(Vertical));
        Assert.Equal(new Size(700, 500), client.Windows.Get(Horizontal)!.Size);
        Assert.Equal(HorizontalSlotsOn, client.Settings);

        // Hiding kept the vertical window's size; it comes back at that size.
        InventoryDriver.PressLayout(client.Panel(Horizontal), InventoryLayout.Vertical);
        Assert.Null(client.Windows.Get(Horizontal));
        Assert.Equal(new Size(380, 600), client.Windows.Get(Vertical)!.Size);
        Assert.Equal(VerticalSlotsOn, client.Settings);
    });

    [Fact]
    public void Retail_opening_its_panel_shows_the_layout_window_and_the_close_box_closes_retail() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        new InventoryPlugin().Start(client);
        Assert.Null(client.Windows.Get(Vertical));

        client.Retail!.Retail(true);
        Assert.NotNull(client.Windows.Get(Vertical));

        var window = client.Panel(Vertical);
        // The close box is the header's one button without a layout tag.
        InventoryDriver.Press(window, window.Content!.GetVisualDescendants().OfType<DerethButton>().Single(button => button.Tag == null));

        Assert.Null(client.Windows.Get(Vertical));
        Assert.Equal(1, client.Retail.CloseCalls);
        Assert.False(client.Retail.Open);
    });

    [Fact]
    public void A_layout_switch_neither_closes_retail_nor_hides_the_panel_from_the_window() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        new InventoryPlugin().Start(client);
        client.Retail!.Retail(true);

        InventoryDriver.PressLayout(client.Panel(Vertical), InventoryLayout.Horizontal);

        Assert.NotNull(client.Windows.Get(Horizontal));
        Assert.Equal(0, client.Retail.CloseCalls);
        Assert.True(client.Retail.Open);
    });

    [Fact]
    public void The_layout_and_slots_choice_come_back_from_the_placements_file_after_a_restart() => RenderThread.Run(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "legacey-inventory-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using (var first = new FakeInventoryClient(new FileWindowPositionStore(path)))
            {
                new InventoryPlugin().Start(first);
                first.MenuEntries.Single().Action();
                InventoryDriver.PressSlots(first.Panel(Vertical));
                InventoryDriver.PressLayout(first.Panel(Vertical), InventoryLayout.Horizontal);
                Assert.Equal(HorizontalSlotsOff, first.Settings);
            }

            // A new session reads the same file: the horizontal layout opens with the Slots toggle still off.
            using var second = new FakeInventoryClient(new FileWindowPositionStore(path));
            new InventoryPlugin().Start(second);
            second.MenuEntries.Single().Action();
            var horizontal = second.Panel(Horizontal);
            Assert.Null(second.Windows.Get(Vertical));
            Assert.Null(InventoryDriver.SlotOrNull(horizontal, PaperdollSlot.Head));
            Assert.NotNull(InventoryDriver.SlotOrNull(horizontal, PaperdollSlot.Neck));
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
        client.SaveSettings(VerticalSlotsOn);
        client.MenuEntries.Single().Action();
        Assert.NotNull(client.Windows.Get(Vertical));
        client.MenuEntries.Single().Action();
        Assert.Null(client.Windows.Get(Vertical));

        // Another character logs in and has its own saved layout: the next open follows that character, not the cached one.
        client.Character = "Other";
        store.Save("Server", "Other", "settings:Inventory", (new Point(1, 0), null));
        client.MenuEntries.Single().Action();
        Assert.Null(client.Windows.Get(Vertical));
        Assert.NotNull(client.Windows.Get(Horizontal));
    });

    [Fact]
    public void A_hidden_layout_takes_the_slots_choice_made_in_the_other_layout_and_draws_what_changed_meanwhile() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        new InventoryPlugin().Start(client);
        client.MenuEntries.Single().Action();
        InventoryDriver.PressLayout(client.Panel(Vertical), InventoryLayout.Horizontal);

        // Slots goes off in the horizontal window, and the port opens a pack while the vertical one is hidden.
        InventoryDriver.PressSlots(client.Panel(Horizontal));
        client.Port.Push(InventorySample.Snapshot(openContainer: InventorySample.Potions));
        InventoryDriver.PressLayout(client.Panel(Horizontal), InventoryLayout.Vertical);
        Assert.Equal(VerticalSlotsOff, client.Settings);
        Assert.Null(InventoryDriver.SlotOrNull(client.Panel(Vertical), PaperdollSlot.Head));
        // Back on screen, the vertical window shows the pack that was opened while it was hidden.
        var cells = InventoryDriver.Slots(client.Panel(Vertical)).Where(slot => InventoryDriver.Id(slot).Place == SlotPlace.Cell).ToArray();
        Assert.Equal(24, cells.Length);
        Assert.All(cells, cell => Assert.Equal(InventorySample.Potions, InventoryDriver.Id(cell).Container));

        // Once more with the Slots choice unchanged, so only the redraw on showing can bring the grid up to date.
        InventoryDriver.PressLayout(client.Panel(Vertical), InventoryLayout.Horizontal);
        client.Port.Push(InventorySample.Snapshot(openContainer: InventorySample.Character));
        InventoryDriver.PressLayout(client.Panel(Horizontal), InventoryLayout.Vertical);
        cells = InventoryDriver.Slots(client.Panel(Vertical)).Where(slot => InventoryDriver.Id(slot).Place == SlotPlace.Cell).ToArray();
        Assert.Equal(96, cells.Length);
    });

    [Fact]
    public void With_the_look_on_the_server_and_slots_off_the_doll_area_hosts_the_model_and_asks_for_the_look() => RenderThread.Run(() =>
    {
        var transport = new LookTransport();
        var channel = new ServerChannelClient(transport);
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = channel };
        client.ServerActions.Add(PaperdollProtocol.Look);
        client.SaveSettings(VerticalSlotsOff);
        new InventoryPlugin().Start(client);
        client.MenuEntries.Single().Action();

        var panel = client.Panel(Vertical);
        var model = Assert.Single(ModelViews(panel));
        Assert.Equal(new[] { PaperdollProtocol.Look }, transport.Actions);

        // The model stays clear of the aetheria row above it and the Slots toggle below it, which the host draws over the model.
        InventoryDriver.Tick(panel);
        var bounds = OnPanel(model, panel);
        Assert.False(bounds.Intersects(OnPanel(InventoryDriver.SlotOrNull(panel, PaperdollSlot.AetheriaOne)!, panel)));
        Assert.False(bounds.Intersects(OnPanel(InventoryDriver.SlotsToggle(panel), panel)));

        // The server says the look changed, as it does when the player equips something: the doll asks again.
        channel.Receive(LookChanged());
        Assert.Equal(new[] { PaperdollProtocol.Look, PaperdollProtocol.Look }, transport.Actions);
    });

    [Fact]
    public void A_window_opened_before_the_server_lists_the_look_gets_its_doll_on_the_next_open() => RenderThread.Run(() =>
    {
        var transport = new LookTransport();
        var channel = new ServerChannelClient(transport);
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = channel };
        client.SaveSettings(VerticalSlotsOff);
        new InventoryPlugin().Start(client);
        client.MenuEntries.Single().Action();
        Assert.Empty(ModelViews(client.Panel(Vertical)));

        // channel.hello lands after the window opened; the next open gives the doll and asks for the look.
        client.ServerActions.Add(PaperdollProtocol.Look);
        client.MenuEntries.Single().Action();
        client.MenuEntries.Single().Action();
        Assert.Single(ModelViews(client.Panel(Vertical)));
        Assert.Equal(new[] { PaperdollProtocol.Look }, transport.Actions);
    });

    [Fact]
    public void A_reply_that_arrives_after_the_doll_left_the_window_changes_nothing() => RenderThread.Run(() =>
    {
        var transport = new LookTransport();
        var channel = new ServerChannelClient(transport);
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = channel };
        client.ServerActions.Add(PaperdollProtocol.Look);
        client.SaveSettings(VerticalSlotsOff);
        new InventoryPlugin().Start(client);
        client.MenuEntries.Single().Action();
        var status = (TextBlock)ModelViews(client.Panel(Vertical)).Single().Child!;

        client.MenuEntries.Single().Action();   // hides the window, and with it the doll
        // A reply the server sent before the hide, with a body this client cannot read: it must not reach the status line.
        channel.Receive(ChannelWire.EncodeEvent(ChannelEventKind.Reply, ChannelStatus.Ok, transport.Ids.Single(), PaperdollProtocol.Look, new byte[] { 1 }));
        Assert.Equal("Loading…", status.Text);
    });

    [Fact]
    public void Without_the_look_on_the_server_the_doll_area_is_a_plain_panel_and_asks_for_nothing() => RenderThread.Run(() =>
    {
        var transport = new LookTransport();
        var channel = new ServerChannelClient(transport);
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = channel };
        client.SaveSettings(VerticalSlotsOff);
        new InventoryPlugin().Start(client);
        client.MenuEntries.Single().Action();

        Assert.Empty(ModelViews(client.Panel(Vertical)));
        channel.Receive(LookChanged());
        Assert.Empty(transport.Actions);
    });

    [Fact]
    public void With_slots_on_no_model_shows_and_no_look_is_asked_for_until_slots_go_off_again() => RenderThread.Run(() =>
    {
        var transport = new LookTransport();
        var channel = new ServerChannelClient(transport);
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = channel };
        client.ServerActions.Add(PaperdollProtocol.Look);
        client.SaveSettings(VerticalSlotsOn);
        new InventoryPlugin().Start(client);
        client.MenuEntries.Single().Action();

        var panel = client.Panel(Vertical);
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

    [Fact]
    public void A_hidden_window_stops_asking_for_looks_and_asks_again_when_it_is_shown() => RenderThread.Run(() =>
    {
        var transport = new LookTransport();
        var channel = new ServerChannelClient(transport);
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore()) { ServerChannel = channel };
        client.ServerActions.Add(PaperdollProtocol.Look);
        client.SaveSettings(VerticalSlotsOff);
        new InventoryPlugin().Start(client);
        client.MenuEntries.Single().Action();
        Assert.Single(transport.Actions);

        client.MenuEntries.Single().Action();
        Assert.Null(client.Windows.Get(Vertical));
        channel.Receive(LookChanged());
        Assert.Single(transport.Actions);

        client.MenuEntries.Single().Action();
        Assert.Equal(2, transport.Actions.Count);
    });

    /// <summary>
    /// Every visible control lies inside the window at the minimum and default sizes the plugin asks for, with the full sample drawn,
    /// and the pack grid shows at least one row. At its minimum the horizontal pack strip stays on one row. Run it again with the
    /// Windows fonts (FONTCONFIG_FILE) to check the taller text.
    /// </summary>
    [Theory]
    [InlineData(InventoryLayout.Vertical)]
    [InlineData(InventoryLayout.Horizontal)]
    public void Nothing_spills_outside_the_window_at_the_minimum_and_default_sizes_in_both_layouts(InventoryLayout layout) => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        client.Port.Push(InventorySample.Snapshot(openContainer: InventorySample.Potions, selected: InventorySample.BluePotion));
        client.SaveSettings(new InventorySettings(layout, showSlots: true).ToInt());
        new InventoryPlugin().Start(client);
        client.MenuEntries.Single().Action();

        var id = layout == InventoryLayout.Horizontal ? Horizontal : Vertical;
        var requested = client.Requested[id];
        var panel = client.Panel(id);
        Assert.Equal(new Size(requested.Width, requested.Height), client.Windows.Get(id)!.Size);
        AssertFitsWithOneGridRow(panel);

        var minimum = requested.Resizing!.Minimum;
        panel.Resize(minimum.Width, minimum.Height);
        InventoryDriver.Tick(panel);
        AssertFitsWithOneGridRow(panel);
        if (layout == InventoryLayout.Horizontal) AssertPackTabsOnOneRow(panel);
    });

    private static void AssertFitsWithOneGridRow(AvaloniaPanel panel)
    {
        Assert.Null(panel.LastError);
        AssertNothingOutsideFrame(panel);
        var grid = Assert.Single(panel.Content.GetVisualDescendants().OfType<DerethSlotGrid>());
        Assert.True(grid.Bounds.Height >= DerethSlotGrid.CellSize, $"The grid is {grid.Bounds.Height} px tall, less than one row.");
    }

    /// <summary>The main pack, the divider and the seven side packs are eight pack tabs, and they all sit on one row.</summary>
    private static void AssertPackTabsOnOneRow(AvaloniaPanel panel)
    {
        var tabs = InventoryDriver.Slots(panel).Where(slot => InventoryDriver.Id(slot).Place == SlotPlace.Pack).ToArray();
        Assert.Equal(8, tabs.Length);
        var rows = tabs.Select(tab => tab.TranslatePoint(default, panel.Content)!.Value.Y).Distinct().Count();
        Assert.Equal(1, rows);
    }

    private static IEnumerable<ModelView> ModelViews(AvaloniaPanel panel) => panel.Content.GetVisualDescendants().OfType<ModelView>();

    /// <summary>A control's bounds in the panel's coordinates.</summary>
    private static Rect OnPanel(Visual visual, AvaloniaPanel panel) => new(visual.TranslatePoint(default, panel.Content)!.Value, visual.Bounds.Size);

    /// <summary>The paperdoll.changed push, as the server sends it after an equipment change.</summary>
    private static byte[] LookChanged() => ChannelWire.EncodeEvent(ChannelEventKind.Push, ChannelStatus.Ok, 0, PaperdollProtocol.Changed, Array.Empty<byte>());

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
        // The client keeps the plugin's one settings value under this key, as ClientUiRuntime does.
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

        /// <summary>The character logged in now. Its settings are kept per character; its window placements are not in this fake.</summary>
        public string Character { get; set; } = "Character";

        /// <summary>The plugin's saved settings value for the character logged in now, as the store holds it.</summary>
        public int? Settings => _store.Load("Server", Character, SettingsKey)?.Location.X;

        /// <summary>The panel of a window, open or hidden.</summary>
        public AvaloniaPanel Panel(string id) => _panels[id];

        public IServerChannel ServerChannel { get; set; } = UnavailableServerChannel.Instance;
        public string PortalPath => string.Empty;
        public IGameArtSource Art { get; } = new InventorySample.NoArt();
        public IItemDragHost ItemDrag { get; } = new FakeItemDragHost();
        public IItemDropRelay ItemDropRelay { get; } = new FakeItemDropRelay();
        public IInventoryPort Inventory => Port;
        /// <summary>The retail inventory panel the plugin took over, once it has.</summary>
        public FakeRetailPanel? Retail { get; private set; }
        public IRetailPanel TakeOverRetailPanel(uint rootId, Action<bool> retailOpenChanged)
        {
            Retail = new FakeRetailPanel(rootId, retailOpenChanged);
            return Retail;
        }
        public bool SupportsAction(string action) => ServerActions.Contains(action);
        public int? LoadSettings() => Settings;
        public void SaveSettings(int value) => _store.Save("Server", Character, SettingsKey, (new Point(value, 0), null));
        public void AddMenuEntry(string title, uint iconId, Action action) => MenuEntries.Add((title, iconId, action));
        public void ToggleWindow(string id, string title, int width, int height, Point defaultLocation, Func<Control> createContent) =>
            throw new NotSupportedException();

        public void ToggleWindowWithChrome(string id, string title, int width, int height, Point defaultLocation, Func<Action, Control> createWindow,
            IClientTheme? theme = null, WindowResizing? resizing = null, int titleBarHeight = 28)
        {
            Requested[id] = (width, height, resizing);
            if (Windows.Get(id) != null)
            {
                Windows.Close(id);
                return;
            }
            var window = Windows.Open(new WindowDefinition(id, title, width, height, titleBarHeight, theme, resizing), defaultLocation);
            if (_panels.TryGetValue(id, out var panel))
            {
                panel.Resize(window.Width, window.Height);
                return;
            }
            panel = AvaloniaPanel.Create(() => createWindow(() => Windows.Close(id)), window.Width, window.Height);
            if (theme != null) panel.ApplyTheme(theme);
            _panels[id] = panel;
        }

        public void RegisterStationWindow(string station, string id, string title, int width, int height, Point defaultLocation, Func<Action, Control> createWindow,
            IClientTheme? theme = null, WindowResizing? resizing = null, int titleBarHeight = 28) => throw new NotSupportedException();

        public void Dispose()
        {
            foreach (var panel in _panels.Values) panel.Dispose();
            _panels.Clear();
        }
    }
}

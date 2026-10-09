using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Inventory;
using Xunit;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace LegACEy.Client.Tests;

/// <summary>
/// The inventory plugin on a fake client. The client's window placement is the real <see cref="WindowManager"/> over a memory store,
/// so the sizes and positions each layout keeps are the ones the client keeps. The windows are real headless panels.
/// </summary>
public sealed class InventoryPluginTests
{
    private const string Vertical = "inventory-vertical";
    private const string Horizontal = "inventory-horizontal";

    [Fact]
    public void The_first_open_uses_each_layouts_default_size_and_the_menu_entry_is_the_inventory() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        new InventoryPlugin().Start(client);
        var entry = Assert.Single(client.MenuEntries);
        Assert.Equal("Inventory", entry.Title);

        entry.Action();
        Assert.Equal(new Size(360, 530), client.Windows.Get(Vertical)!.Size);
        InventoryDriver.PressLayout(client.Panel(Vertical), InventoryLayout.Horizontal);
        Assert.Equal(new Size(640, 400), client.Windows.Get(Horizontal)!.Size);
    });

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
        Assert.Equal(new Point(1, 1), client.Settings);

        // Hiding kept the vertical window's size; it comes back at that size.
        InventoryDriver.PressLayout(client.Panel(Horizontal), InventoryLayout.Vertical);
        Assert.Null(client.Windows.Get(Horizontal));
        Assert.Equal(new Size(380, 600), client.Windows.Get(Vertical)!.Size);
        Assert.Equal(new Point(0, 1), client.Settings);
    });

    [Fact]
    public void The_layout_and_slots_choice_round_trip_through_the_store() => RenderThread.Run(() =>
    {
        var positions = new MemoryWindowPositionStore();
        using (var first = new FakeInventoryClient(positions))
        {
            new InventoryPlugin().Start(first);
            first.MenuEntries.Single().Action();
            InventoryDriver.PressSlots(first.Panel(Vertical));
            Assert.Equal(new Point(0, 0), first.Settings);
            InventoryDriver.PressLayout(first.Panel(Vertical), InventoryLayout.Horizontal);
            Assert.Equal(new Point(1, 0), first.Settings);
        }

        // A new session reads the same store: it opens the horizontal layout with the Slots toggle still off.
        using var second = new FakeInventoryClient(positions, savedSettings: new Point(1, 0));
        new InventoryPlugin().Start(second);
        second.MenuEntries.Single().Action();
        var horizontal = second.Panel(Horizontal);
        Assert.Null(second.Windows.Get(Vertical));
        Assert.Null(InventoryDriver.SlotOrNull(horizontal, PaperdollSlot.Head));
        Assert.NotNull(InventoryDriver.SlotOrNull(horizontal, PaperdollSlot.Neck));
    });

    [Fact]
    public void A_hidden_layout_takes_the_slots_choice_made_in_the_other_layout() => RenderThread.Run(() =>
    {
        using var client = new FakeInventoryClient(new MemoryWindowPositionStore());
        new InventoryPlugin().Start(client);
        client.MenuEntries.Single().Action();
        InventoryDriver.PressLayout(client.Panel(Vertical), InventoryLayout.Horizontal);

        // Slots goes off in the horizontal window; the vertical one is hidden and keeps its content.
        InventoryDriver.PressSlots(client.Panel(Horizontal));
        InventoryDriver.PressLayout(client.Panel(Horizontal), InventoryLayout.Vertical);
        Assert.Equal(new Point(0, 0), client.Settings);
        Assert.Null(InventoryDriver.SlotOrNull(client.Panel(Vertical), PaperdollSlot.Head));
    });

    /// <summary>A client for the inventory plugin: a real <see cref="WindowManager"/> places its windows, and each window's panel is kept across hiding.</summary>
    private sealed class FakeInventoryClient : ILegACEyClient, IDisposable
    {
        private readonly Dictionary<string, AvaloniaPanel> _panels = new(StringComparer.Ordinal);

        public FakeInventoryClient(IWindowPositionStore positions, Point? savedSettings = null)
        {
            Windows = new WindowManager(new Size(1920, 1080), positions, "Server", "Character");
            Settings = savedSettings;
        }

        public WindowManager Windows { get; }
        public Point? Settings { get; private set; }
        public FakeInventoryPort Port { get; } = new();
        public List<(string Title, uint Icon, Action Action)> MenuEntries { get; } = new();

        /// <summary>The panel of a window, open or hidden.</summary>
        public AvaloniaPanel Panel(string id) => _panels[id];

        public IServerChannel ServerChannel => UnavailableServerChannel.Instance;
        public string PortalPath => string.Empty;
        public IGameArtSource Art { get; } = new InventorySample.NoArt();
        public IItemDragHost ItemDrag => throw new NotSupportedException();
        public IInventoryPort Inventory => Port;
        public bool SupportsAction(string action) => false;
        public Point? LoadSettings() => Settings;
        public void SaveSettings(Point settings) => Settings = settings;
        public void AddMenuEntry(string title, uint iconId, Action action) => MenuEntries.Add((title, iconId, action));
        public void ToggleWindow(string id, string title, int width, int height, Point defaultLocation, Func<Control> createContent) =>
            throw new NotSupportedException();

        public void ToggleWindowWithChrome(string id, string title, int width, int height, Point defaultLocation, Func<Action, Control> createWindow,
            IClientTheme? theme = null, WindowResizing? resizing = null, int titleBarHeight = 28)
        {
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

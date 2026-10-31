using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Xunit;
using AvaloniaPoint = Avalonia.Point;

namespace LegACEy.Client.Tests;

/// <summary>
/// The container window and the state behind it, fed the messages ACE sends when a player opens a chest (Container.Open): ViewContents for
/// the chest and each pack in it, a CreateObject for each item, then CloseGroundContainer when it closes.
/// </summary>
public sealed class ContainerWindowTests
{
    private const int GameEvent = 0xF7B0, CreateObject = 0xF745, RemoveObject = 0x0024;
    private const uint Me = 0x50000002, Chest = 0x7A000001, Pack = 0x7A000002;
    private const uint Sword = 0x80000010, Coins = 0x80000011, Potion = 0x80000012, Scroll = 0x80000013, InPack = 0x80000014, Mine = 0x80000099;

    // The writer keeps the message's alignment from its start, the type's four bytes cut off as ACE's offsets count them.
    private sealed class Wire
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        public Wire() => _writer = new BinaryWriter(_stream);
        public Wire U8(int value) { _writer.Write((byte)value); return this; }
        public Wire U16(int value) { _writer.Write((ushort)value); return this; }
        public Wire U32(uint value) { _writer.Write(value); return this; }
        public Wire Align() { while (_stream.Length % 4 != 0) _writer.Write((byte)0); return this; }
        public Wire String(string text) { U16(text.Length); _writer.Write(Encoding.ASCII.GetBytes(text)); while ((_stream.Length) % 4 != 0) _writer.Write((byte)0); return this; }

        // ACE's WritePackedDwordOfKnownType: two bytes up to 0x7FFF, else four with the high half first.
        public Wire Packed(uint value, uint type = 0)
        {
            if ((value & type) > 0) value -= type;
            if (value <= 0x7FFF) return U16((int)value);
            return U16((int)((value >> 16) | 0x8000)).U16((int)(value & 0xFFFF));
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    private static byte[] Event(uint type, Action<Wire> body)
    {
        var w = new Wire().U32(Me).U32(1).U32(type);
        body(w);
        return w.ToArray();
    }

    /// <summary>ViewContents: the container, then each item and whether it is a pack (1) or not (0).</summary>
    private static byte[] View(uint container, params (uint Id, uint Type)[] items) => Event(0x0196, w =>
    {
        w.U32(container).U32((uint)items.Length);
        foreach (var (id, type) in items) w.U32(id).U32(type);
    });

    /// <summary>A CreateObject as ACE's SerializeCreateObject writes it, with the fields a container's items use.</summary>
    private static byte[] Created(uint id, string name, uint icon, uint? container = null, int stack = 1, int stackMax = 0, int capacity = 0, int packs = 0,
        uint overlay = 0, uint itemType = 0x80, bool moving = false)
    {
        var w = new Wire().U32(id);
        // The model: 0x11, no palettes, textures or parts.
        w.U8(0x11).U8(0).U8(0).U8(0).Align();
        // The physics: a moving object's flags carry its position (32 bytes) and the translucency, then nine timestamps.
        var flags = moving ? 0x8000u | 0x40000 | 0x1 | 0x80 : 0u;
        w.U32(flags).U32(0);
        if (moving)
        {
            for (var i = 0; i < 8; i++) w.U32(0);
            w.U32(0x02000001).U32(0x3F800000).U32(0);
        }
        for (var i = 0; i < 9; i++) w.U16(0);
        w.Align();
        uint header = 0;
        if (capacity > 0) header |= 0x2;
        if (packs > 0) header |= 0x4;
        if (stack > 1) header |= 0x1000;
        if (stackMax > 0) header |= 0x2000;
        if (container != null) header |= 0x4000;
        if (overlay != 0) header |= 0x40000000;
        w.U32(header).String(name).Packed(0x1234).Packed(icon, 0x06000000).U32(itemType).U32(0).Align();
        if (capacity > 0) w.U8(capacity);
        if (packs > 0) w.U8(packs);
        if (stack > 1) w.U16(stack);
        if (stackMax > 0) w.U16(stackMax);
        if (container != null) w.U32(container.Value);
        if (overlay != 0) w.Packed(overlay, 0x06000000);
        return w.ToArray();
    }

    private static ContainerState OpenedChest()
    {
        var state = new ContainerState();
        state.Receive(CreateObject, Created(Chest, "Chest of Lyre", 0x06001111, capacity: 24, packs: 2));
        state.Receive(GameEvent, View(Chest, (Sword, 0), (Coins, 0), (Pack, 1), (Potion, 0)));
        state.Receive(GameEvent, View(Pack, (InPack, 0)));
        state.Receive(CreateObject, Created(Sword, "Iron Sword", 0x06002000, Chest, overlay: 0x06002100));
        state.Receive(CreateObject, Created(Coins, "Pyreal", 0x06002001, Chest, stack: 25000, stackMax: 25000, moving: true));
        state.Receive(CreateObject, Created(Pack, "Leather Sack", 0x06002002, Chest, capacity: 24));
        state.Receive(CreateObject, Created(Potion, "Healing Draught", 0x06002003, Chest));
        state.Receive(CreateObject, Created(InPack, "Scroll of Banish", 0x06002004, Pack));
        return state;
    }

    [Fact]
    public void Opening_a_chest_gives_its_name_size_items_and_packs_in_the_server_s_order()
    {
        var state = OpenedChest();
        Assert.Equal(Chest, state.Opened);
        Assert.Equal("Chest of Lyre", state.NameOf(Chest));
        Assert.Equal(24, state.CapacityOf(Chest));
        Assert.Equal(0x06001111u, state.VisualOf(Chest)!.Icon);
        // The pack takes the third place in the server's list, so the potion is fourth there but the third item cell.
        Assert.Equal(new[] { Sword, Coins, Potion }, state.ItemsOf(Chest).Select(item => item.Id));
        Assert.Equal(new[] { Pack }, state.PacksOf(Chest).Select(item => item.Id));
        var sword = state.ItemsOf(Chest).First();
        Assert.Equal(("Iron Sword", 0x06002000u, 0x06002100u), (sword.Name, sword.Visual.Icon, sword.Visual.Overlay));
        // A stack's count and size, read past a moving object's physics.
        var coins = state.ItemsOf(Chest).Single(item => item.Id == Coins);
        Assert.Equal((25000, 25000), (coins.StackCount, coins.StackMax));
        // A pack's contents are its own view, and the pack is not the container the player has open.
        Assert.Equal(new[] { InPack }, state.ItemsOf(Pack).Select(item => item.Id));
        Assert.Equal(Chest, state.Opened);
    }

    [Fact]
    public void Items_move_in_and_out_and_closing_forgets_the_container()
    {
        var state = OpenedChest();
        var changed = 0;
        state.Changed += () => changed++;

        // The player takes the sword (it goes into their pack), then the potion is deleted, then a scroll is put in at the front.
        state.Receive(GameEvent, Event(0x0022, w => w.U32(Sword).U32(Me).U32(0).U32(0)));
        state.Receive(RemoveObject, new Wire().U32(Potion).ToArray());
        state.Receive(GameEvent, Event(0x0022, w => w.U32(Mine).U32(Chest).U32(0).U32(0)));
        state.Receive(CreateObject, Created(Mine, "Old Scroll", 0x06002005, Chest));
        Assert.Equal(new[] { Mine, Coins }, state.ItemsOf(Chest).Select(item => item.Id));
        Assert.True(changed >= 4);

        state.Receive(GameEvent, Event(0x0052, w => w.U32(Chest)));
        Assert.Equal(0u, state.Opened);
        Assert.Empty(state.ItemsOf(Chest));
        Assert.Empty(state.ItemsOf(Pack));
        // The player's own objects stay known; a world object that is nobody's item was never kept.
        state.Receive(CreateObject, Created(Scroll, "Tree", 0x06002006));
        Assert.Null(state.VisualOf(Scroll));
    }

    [Fact]
    public void A_pack_the_player_picks_up_while_a_chest_is_open_does_not_replace_the_chest()
    {
        var state = OpenedChest();
        // ACE puts the ground pack in the player's inventory, then sends its contents as it does for every carried pack at login.
        state.Receive(CreateObject, Created(Mine, "Backpack", 0x06002005, capacity: 24));
        state.Receive(GameEvent, Event(0x0022, w => w.U32(Mine).U32(Me).U32(0).U32(1)));
        state.Receive(GameEvent, View(Mine, (Scroll, 0)));
        Assert.Equal(Chest, state.Opened);
        Assert.Equal(new[] { Sword, Coins, Potion }, state.ItemsOf(Chest).Select(item => item.Id));
    }

    [Fact]
    public void A_message_cut_short_changes_nothing()
    {
        var state = OpenedChest();
        var whole = Created(Mine, "Old Scroll", 0x06002005, Chest);
        state.Receive(CreateObject, whole.Take(whole.Length - 3).ToArray());
        Assert.Null(state.VisualOf(Mine));
        Assert.Equal(new[] { Sword, Coins, Potion }, state.ItemsOf(Chest).Select(item => item.Id));
    }

    private sealed class Rig : IDisposable
    {
        public ContainerState State { get; } = OpenedChest();
        public FakeInventoryPort Port { get; } = new();
        public FakeItemDragHost Drag { get; } = new();
        public AvaloniaPanel Panel { get; }
        public ContainerWindow View { get; private set; } = null!;
        public int Closed, Toggled, Moves;

        public Rig(ContainerLayout layout, int height)
        {
            Panel = AvaloniaPanel.Create(() =>
            {
                View = new ContainerWindow(layout, State, Port, new NoArt(), Drag, () => Moves++);
                View.CloseRequested += () => Closed++;
                View.ToggleRequested += () => Toggled++;
                return View;
            }, ContainerWindow.Width, height);
            Panel.ApplyTheme(new DerethClientTheme());
            Panel.Tick();
        }

        public DerethSlot[] Slots() => Panel.Content.GetVisualDescendants().OfType<DerethSlot>().ToArray();

        public DerethSlot Item(uint id) => Slots().Single(slot => ItemId(slot) == id);

        public static uint ItemId(Control slot) => ContainerWindow.ItemIdOf(slot);

        public void Dispose()
        {
            Panel.Dispose();
            View.Dispose();
        }

        private sealed class NoArt : IGameArtSource
        {
            public GameImage? ReadImage(uint id) => null;
        }
    }

    [Fact]
    public void The_strip_shows_the_container_its_pack_slots_and_a_row_of_cells_with_the_container_selected() => RenderThread.Run(() =>
    {
        using var rig = new Rig(ContainerLayout.Strip, ContainerWindow.StripHeight);
        Assert.Equal("Chest of Lyre", rig.View.ContainerName);
        // The chest takes two packs, so two pack slots.
        Assert.Equal(2, rig.View.PackCellCount);
        // Capacity 24, so a cell for every slot; the cells are the capacity, not just the items.
        Assert.Equal(24, rig.View.ItemCellCount);
        Assert.Equal(new[] { Sword, Coins, Potion }, rig.View.ItemsShown);
        Assert.True(rig.Slots().Single(slot => Rig.ItemId(slot) == Chest).Selected);
        Assert.Equal(string.Empty, rig.View.CountText);

        // A pack opened on the ground takes no packs, so as in retail its icon stands alone on the top row.
        rig.State.Receive(GameEvent, Event(0x0052, w => w.U32(Chest)));
        rig.State.Receive(CreateObject, Created(Mine, "Backpack", 0x06002005, capacity: 24));
        rig.State.Receive(GameEvent, View(Mine, (Scroll, 0)));
        rig.Panel.Tick();
        Assert.Equal(0, rig.View.PackCellCount);
    });

    [Fact]
    public void The_grid_wraps_whole_rows_of_cells_and_counts_the_items_against_the_capacity() => RenderThread.Run(() =>
    {
        using var rig = new Rig(ContainerLayout.Grid, ContainerWindow.GridHeight);
        // 24 slots in 17 columns: two rows.
        Assert.Equal(34, rig.View.ItemCellCount);
        Assert.Equal(2, rig.View.PackCellCount);
        Assert.Equal("3 / 24", rig.View.CountText);
        Assert.Equal("Chest of Lyre", rig.View.ContainerName);
    });

    [Fact]
    public void A_click_selects_a_double_click_uses_and_a_right_click_appraises() => RenderThread.Run(() =>
    {
        using var rig = new Rig(ContainerLayout.Grid, ContainerWindow.GridHeight);
        InventoryDriver.Press(rig.Panel, rig.Item(Sword));
        Assert.Equal(new[] { $"select 0x{Sword:X8}" }, rig.Port.Commands);

        rig.Port.Commands.Clear();
        InventoryDriver.Press(rig.Panel, rig.Item(Coins));
        InventoryDriver.Press(rig.Panel, rig.Item(Coins));
        Assert.Equal(new[] { $"select 0x{Coins:X8}", $"use 0x{Coins:X8}" }, rig.Port.Commands);

        rig.Port.Commands.Clear();
        InventoryDriver.RightClick(rig.Panel, rig.Item(Potion));
        Assert.Equal(new[] { $"assess 0x{Potion:X8}" }, rig.Port.Commands);
    });

    [Fact]
    public void A_pack_replaces_the_items_shown_with_its_own_and_the_container_s_icon_brings_them_back() => RenderThread.Run(() =>
    {
        using var rig = new Rig(ContainerLayout.Grid, ContainerWindow.GridHeight);
        InventoryDriver.Press(rig.Panel, rig.Item(Pack));
        Assert.Equal(new[] { InPack }, rig.View.ItemsShown);
        // The pack is selected, the container no longer is.
        Assert.True(rig.Item(Pack).Selected);

        // The container's icon in the header.
        InventoryDriver.Press(rig.Panel, rig.Panel.Content.GetVisualDescendants().OfType<Control>().First(control => Rig.ItemId(control) == Chest));
        Assert.Equal(new[] { Sword, Coins, Potion }, rig.View.ItemsShown);
    });

    [Fact]
    public void An_item_of_the_player_s_released_over_the_window_is_put_in_the_container_or_the_pack_under_it() => RenderThread.Run(() =>
    {
        using var rig = new Rig(ContainerLayout.Grid, ContainerWindow.GridHeight);
        var at = InventoryDriver.Centre(rig.Panel, rig.Item(Sword));
        var onCell = new AvaloniaPoint(at.X, at.Y);
        Assert.True(rig.View.RetailDrop(Mine, "Old Scroll", onCell));
        Assert.Equal(new[] { $"move 0x{Mine:X8} to 0x{Chest:X8} slot 0" }, rig.Port.Commands);

        // Onto the pack's tile it goes into the pack; an item the container already holds goes nowhere.
        rig.Port.Commands.Clear();
        var packAt = InventoryDriver.Centre(rig.Panel, rig.Item(Pack));
        Assert.True(rig.View.RetailDrop(Mine, "Old Scroll", new AvaloniaPoint(packAt.X, packAt.Y)));
        Assert.Equal(new[] { $"move 0x{Mine:X8} to 0x{Pack:X8} slot 0" }, rig.Port.Commands);
        Assert.False(rig.View.RetailDrop(Sword, "Iron Sword", onCell));
        Assert.Single(rig.Port.Commands);
    });

    [Fact]
    public void A_drag_that_leaves_the_window_is_handed_to_retail_and_a_release_outside_goes_to_the_window_under_it() => RenderThread.Run(() =>
    {
        using var rig = new Rig(ContainerLayout.Grid, ContainerWindow.GridHeight);
        var start = InventoryDriver.Centre(rig.Panel, rig.Item(Sword));
        var outside = ContainerWindow.Width + 40;

        // Retail's list for the container has the item, so retail takes the drag and none of ours is left.
        rig.Drag.TakeHandOff = true;
        rig.Panel.PointerDown(start.X, start.Y, Avalonia.Input.KeyModifiers.None);
        rig.Panel.PointerMove(start.X + 10, start.Y);
        Assert.Equal(1, rig.Drag.IconsOpen);
        rig.Panel.PointerMove(outside, start.Y);
        Assert.Equal(new[] { Sword }, rig.Drag.HandedOff.Distinct());
        Assert.Equal(0, rig.Drag.IconsOpen);
        rig.Panel.PointerUp(outside, start.Y);
        Assert.Empty(rig.Drag.Delivered);

        // Retail has no element for it (an item of a pack): released outside, it goes to the LegACEy window under the pointer, as the
        // inventory's own items do.
        rig.Drag.TakeHandOff = false;
        rig.Panel.PointerDown(start.X, start.Y, Avalonia.Input.KeyModifiers.None);
        rig.Panel.PointerMove(start.X + 10, start.Y);
        rig.Panel.PointerMove(outside, start.Y);
        Assert.Equal(1, rig.Drag.IconsOpen);
        rig.Panel.PointerUp(outside, start.Y);
        Assert.Equal(new[] { (Sword, "Iron Sword") }, rig.Drag.Delivered);
        Assert.Equal(0, rig.Drag.IconsOpen);
    });

    // The header buttons of a window that is not on screen: the toggle has a tooltip, the close box has none.
    private static void Press(ContainerWindow view, bool toggle) =>
        view.GetLogicalDescendants().OfType<DerethHeaderButton>().Single(button => (ToolTip.GetTip(button) != null) == toggle)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // A fake host that opens every window and remembers it, as the client does for a window of its own chrome.
    private sealed class Host : ILegACEyPluginHost
    {
        public List<string> Open { get; } = new();
        public List<string> Hidden { get; } = new();
        public Dictionary<string, Control> Content { get; } = new();
        public Dictionary<string, WindowDefinition> Definitions { get; } = new();
        public FakeInventoryPort Port { get; } = new();

        public IServerChannel ServerChannel => UnavailableServerChannel.Instance;
        public ILookSource Looks => throw new NotSupportedException();
        public string PortalPath => string.Empty;
        public IGameArtSource Art { get; } = new NoArt();
        public IItemDragHost ItemDrag { get; } = new FakeItemDragHost();
        public IInventoryPort Inventory => Port;
        public bool IsWindowOpen(string id) => Open.Contains(id);
        public bool Tell(string recipient, string text) => false;
        public void ShowToast(string title, string line) { }
        public void WriteChat(string text) { }
        public IRetailPanel TakeOverRetailInventory(Action<bool> retailOpenChanged) => throw new NotSupportedException();
        public void Inspect(uint objectId, string name, ItemVisual? visual) { }
        public void ShowAppraisal(byte[] appraisal, string name, ItemVisual? visual, int stackCount) { }
        public void SelectForExamination(Action? examine) { }
        public int? LoadPluginSettings(string plugin) => null;
        public void SavePluginSettings(string plugin, int value) { }

        public bool OpenWindow(WindowDefinition definition, Point location, Func<Action, Avalonia.Controls.Control> createContent, Action<Exception> failed, bool ownChrome)
        {
            if (!Content.ContainsKey(definition.Id)) Content[definition.Id] = createContent(() => HideWindow(definition.Id));
            Definitions[definition.Id] = definition;
            Hidden.Remove(definition.Id);
            if (!Open.Contains(definition.Id)) Open.Add(definition.Id);
            return true;
        }

        public void HideWindow(string id)
        {
            Open.Remove(id);
            Hidden.Add(id);
        }

        public void CloseWindow(string id) => Open.Remove(id);

        private sealed class NoArt : IGameArtSource
        {
            public GameImage? ReadImage(uint id) => null;
        }
    }

    [Fact]
    public void The_window_follows_retail_s_container_view_and_the_layout_toggle_is_kept_for_the_character() => RenderThread.Run(() =>
    {
        var host = new Host();
        var port = new NativeUiLifecycleTests.FakeRetailPanelPort { Location = new Point(10, 400) };
        var store = new MemoryWindowPositionStore();
        var feature = new ContainerFeature(host, port, store, () => ("server", "Pourbot"), _ => { }, _ => { });
        foreach (var message in new (int Type, byte[] Raw)[]
        {
            (CreateObject, Created(Chest, "Chest of Lyre", 0x06001111, capacity: 24)), (GameEvent, View(Chest, (Sword, 0))),
            (CreateObject, Created(Sword, "Iron Sword", 0x06002000, Chest)),
        })
            feature.State.Receive(message.Type, message.Raw);

        // Nothing open: retail's view is closed, so there is no window.
        feature.Tick(enabled: true, canShow: true);
        Assert.Empty(host.Open);

        // Retail opens its container view: the window opens in the character's layout, the row by default.
        port.Open = true;
        feature.Tick(true, true);
        Assert.Equal(new[] { ContainerWindow.StripId }, host.Open);
        Assert.Equal(ContainerLayout.Strip, feature.Layout);

        // The toggle opens the grid in place of the row, and the choice is saved for this character and nobody else.
        Press((ContainerWindow)host.Content[ContainerWindow.StripId], toggle: true);
        Assert.Equal(new[] { ContainerWindow.GridId }, host.Open);
        Assert.Equal(ContainerLayout.Grid, feature.Layout);
        // Each layout keeps its own size, and both open where the other was left.
        Assert.Equal(ContainerWindow.StripId, host.Definitions[ContainerWindow.GridId].SharesLocationWith);
        var other = new ContainerFeature(host, null, store, () => ("server", "Other"), _ => { }, _ => { });
        Assert.Equal(ContainerLayout.Strip, other.Layout);

        // Back and forth, then retail closes the container (a walk away): the window closes with it.
        Press((ContainerWindow)host.Content[ContainerWindow.GridId], toggle: true);
        Assert.Equal(new[] { ContainerWindow.StripId }, host.Open);
        Assert.Equal(ContainerLayout.Strip, feature.Layout);
        Press((ContainerWindow)host.Content[ContainerWindow.StripId], toggle: true);
        port.Open = false;
        for (var frame = 0; frame < RetailPanelTakeover.TransitionFrames + 1; frame++) feature.Tick(true, true);
        Assert.Empty(host.Open);

        // The window's close box closes retail's view through its own path.
        port.Open = true;
        feature.Tick(true, true);
        Assert.Equal(new[] { ContainerWindow.GridId }, host.Open);
        Press((ContainerWindow)host.Content[ContainerWindow.GridId], toggle: false);
        Assert.Equal(1, port.CloseCalls);
        Assert.Empty(host.Open);
        feature.Dispose();
    });

    [Fact]
    public void The_container_window_renders_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_CONTAINER_RENDER names the folder, LEGACEY_PORTAL_DAT the portal for retail's art.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_CONTAINER_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var portalPath = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        using var portal = portalPath == null ? null : new PortalDat(portalPath);
        foreach (var (name, layout, height) in new[] { ("strip", ContainerLayout.Strip, ContainerWindow.StripHeight), ("grid", ContainerLayout.Grid, ContainerWindow.GridHeight) })
        {
            var state = OpenedChest();
            ContainerWindow view = null!;
            using var panel = AvaloniaPanel.Create(() => view = new ContainerWindow(layout, state, new FakeInventoryPort(), portal ?? (IGameArtSource)new NoArtSource(),
                new FakeItemDragHost()), ContainerWindow.Width, height);
            panel.ApplyTheme(new DerethClientTheme());
            panel.Tick();
            panel.Tick();
            PanelPng.Save(panel, Path.Combine(folder, name + ".png"));
            view.Dispose();
        }
    });

    private sealed class NoArtSource : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}

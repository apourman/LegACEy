using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.Demo;

namespace LegACEy.Client.Tests;

/// <summary>
/// The stub-to-client bridge with no Decal and no game: a calls-out table built here, events raised through the table the client
/// fills in, and every call made through the unmanaged function pointers the stub will use.
/// </summary>
public sealed unsafe class BridgeTests : IDisposable
{
    private const uint Character = 0x50000001;
    private static readonly List<string> Calls = new();
    private static string _directory = string.Empty;
    private readonly string _pluginDirectory = ClientUiRuntime.PluginDirectory;
    // The started client's shutdown, so a failed test still leaves the bridge free for the next one.
    private delegate* unmanaged[Stdcall]<void> _shutdown;

    public BridgeTests()
    {
        Calls.Clear();
        _directory = Directory.CreateTempSubdirectory("legacey-bridge").FullName;
    }

    public void Dispose()
    {
        if (_shutdown != null) _shutdown();
        ClientUiRuntime.PluginDirectory = _pluginDirectory;
        Directory.Delete(_directory, recursive: true);
    }

    private static BridgeCalls FakeCalls() => new()
    {
        Fact = &Fact, Text = &Text, Inventory = &Inventory, Item = &Item,
        UseItem = &UseItem, SelectItem = &SelectItem, RequestId = &RequestId, DropItem = &DropItem,
        MoveItem = &MoveItem, MergeItem = &MergeItem, AutoWield = &AutoWield,
        ValidLocations = &ValidLocations, Coverage = &Coverage, PluralName = &PluralName
    };

    [Fact]
    public void Events_reach_the_client_port_calls_come_back_out_and_a_client_failure_switches_the_ui_off()
    {
        var client = new RecordingClient();
        BridgeEvents events;
        var calls = FakeCalls();
        Assert.Equal(0, Bridge.Start(&events, &calls, () => client));
        _shutdown = events.Shutdown;
        BridgeEvents second;
        Assert.Equal(1, Bridge.Start(&second, &calls, () => new RecordingClient()));

        // The slots the client's own hooks replaced still answer a stub from before them, and reach nothing.
        events.Frame();
        Assert.Equal(0, events.WindowMessage(0x1234, 0x0201, 1, (20 << 16) | 10));
        var raw = new byte[] { 0xB0, 0xF7, 0, 0, 1, 2, 3 };
        fixed (byte* bytes = raw) events.ServerMessage(0xF7B0, bytes, raw.Length);
        Assert.True(events.CommandLine == null);
        // So do the login and logoff slots, which the server's own messages replaced (178).
        events.LoginComplete();
        events.Logoff();
        events.LogoffAuthorized();
        events.InventoryChanged(unchecked((int)0x80000010), unchecked((int)Character));
        events.ItemSelected();
        events.ContainerOpened();

        Assert.Equal(new[] { "inventory 80000010 50000001", "selected", "container" }, client.Seen);

        // The bridge-backed ports read the client's Decal facts and send its actions back out through the calls-out table; the character
        // and server come from the server's messages.
        ClientUiRuntime.Identity.Receive(0xF7E1, SessionIdentityTests.WorldInfo("Coldeve"));
        ClientUiRuntime.Identity.Receive(0xF7B0, SessionIdentityTests.Description(Character, "Aerbax"));
        Assert.Equal(new GameStateSnapshot("Aerbax", "Coldeve", 40, 50, 60, 70, 80, 90), Bridge.ReadGameState());
        var inventory = new BridgeInventoryPort(_ => { });
        inventory.MarkStale();
        inventory.Flush(inGame: true);
        var sword = Assert.Single(inventory.Snapshot.Wielded);
        Assert.Equal(("Ŧhe Sword", new ItemVisual(0x06001234, 0x06000001, 0, 1, 0x06000002)), (sword.Name, sword.Visual));
        Assert.Equal(123, inventory.Snapshot.Burden);
        inventory.Use(7);
        inventory.Assess(8);
        inventory.MoveToContainer(9, Character, 3);
        inventory.MergeStack(0x10, 0x11);
        inventory.DropOnGround(0x12);
        inventory.Wield(0x80000020, PaperdollSlot.Weapon);
        Assert.Equal(new[] { "use 7 0", "select 8", "id 8", "move 9 50000001 3 0", "merge 10 11", "drop 12", "wield 80000020 100000 1 0" },
            Calls.Where(call => !call.StartsWith("read")));
        // An item the snapshot doesn't hold, such as one dragged from a chest, has its valid locations read from the world.
        Assert.Equal(0x100000u, inventory.WieldMask(0x80000030, PaperdollSlot.Weapon));

        // A name filling the whole row and cut inside a character loses that character, never shows U+FFFD.
        var row = new BridgeItem();
        Encoding.UTF8.GetBytes(new string('a', BridgeItem.NameCapacity - 1) + "Ŧ").AsSpan(0, BridgeItem.NameCapacity)
            .CopyTo(new Span<byte>(row.Name, BridgeItem.NameCapacity));
        Assert.Equal(new string('a', BridgeItem.NameCapacity - 1), row.ToRead().Name);

        // A Decal call that fails in the stub comes back as its HRESULT and is raised here as the COMException Decal threw, which the
        // game-state poller rides out while stats are unreadable.
        _failFacts = true;
        Assert.Equal(EFail, Assert.Throws<COMException>(Bridge.ReadGameState).HResult);
        // So does the inventory's per-frame Strength read while the character leaves the world.
        inventory.Flush(inGame: true);
        _failFacts = false;

        // A failure inside the client, in any event, switches its UI off and returns to the stub normally.
        client.Throw = true;
        var raised = events;
        var raise = new Action[]
        {
            () => raised.ItemSelected(), () => raised.ContainerOpened(),
            () => raised.InventoryChanged(1, 2), () => raised.Shutdown()
        };
        foreach (var raiseOne in raise) raiseOne();
        Assert.Equal(raise.Length, client.Disabled.Count);
    }

    [Fact]
    public void Init_starts_the_real_client_which_logs_in_the_plugin_directory_and_shuts_down()
    {
        ClientUiRuntime.PluginDirectory = _directory;
        BridgeEvents events;
        var calls = FakeCalls();
        var init = (delegate* unmanaged[Stdcall]<BridgeEvents*, BridgeCalls*, int>)&Bridge.Init;
        Assert.Equal(0, init(&events, &calls));
        _shutdown = events.Shutdown;

        events.Frame();
        events.LoginComplete();
        events.Logoff();

        var log = File.ReadAllText(Path.Combine(_directory, "legacey-avalonia.log"));
        Assert.Contains($"[pid {Environment.ProcessId}]", log);
    }


    [Fact]
    public void A_server_message_yields_the_channel_payload_with_or_without_the_message_type_and_never_from_a_sequence_number()
    {
        var transport = new GameChannelTransport((_, _) => throw new InvalidOperationException("no game"), () => true, _ => { });
        static byte[] Words(params uint[] words) => words.SelectMany(BitConverter.GetBytes).ToArray();

        Assert.Equal(Words(7), transport.ReadEvent(0xF7B0, Words(0xF7B0, Character, 1, ChannelWire.GameEventType, 7)));
        Assert.Equal(Words(7), transport.ReadEvent(0xF7B0, Words(Character, 1, ChannelWire.GameEventType, 7)));
        Assert.Null(transport.ReadEvent(0xF7B0, Words(0xF7B0, Character, ChannelWire.GameEventType, 0x0013, 7)));
        Assert.Null(transport.ReadEvent(0xF7B1, Words(Character, 1, ChannelWire.GameEventType, 7)));
    }

    private sealed class RecordingClient : IClientEvents
    {
        public readonly List<string> Seen = new();
        public readonly List<Exception> Disabled = new();
        public bool Throw;

        private void See(string what)
        {
            if (Throw) throw new InvalidOperationException("client bug");
            Seen.Add(what);
        }

        public void ItemSelected() => See("selected");
        public void ContainerOpened() => See("container");
        public void InventoryChanged(uint id, uint container) => See($"inventory {id:X} {container:X}");
        public void Shutdown() => See("shutdown");
        public void Disable(Exception exception) => Disabled.Add(exception);
    }

    // The calls-out table: the stub's side, answered from fixed values and recorded.

    private const int EFail = unchecked((int)0x80004005);
    private static bool _failFacts;

    private static int Done(string call)
    {
        Calls.Add(call);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Fact(int fact, int* value)
    {
        if (_failFacts) return EFail;
        *value = (BridgeFact)fact switch
        {
            BridgeFact.Health => 40, BridgeFact.MaxHealth => 50, BridgeFact.Stamina => 60, BridgeFact.MaxStamina => 70,
            BridgeFact.Mana => 80, BridgeFact.MaxMana => 90, BridgeFact.Burden => 123, BridgeFact.Strength => 100,
            BridgeFact.MainPackCapacity => 102, BridgeFact.SidePackCapacity => 7,
            _ => 0
        };
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Text(int text, byte* buffer, int capacity, int* length)
    {
        *length = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Inventory(BridgeItem* rows, int capacity, int* count)
    {
        if (capacity >= 1) rows[0] = Sword();
        *count = 1;
        return Done("read inventory");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Item(int id, BridgeItem* row, int* found)
    {
        *found = unchecked((uint)id) == 0x80000020 ? 1 : 0;
        if (*found != 0) *row = Sword();
        return 0;
    }

    private static BridgeItem Sword()
    {
        var row = new BridgeItem
        {
            Id = 0x80000020, Container = Character, Slot = -1, EquippedMask = 0x100000, ValidLocations = 0x100000, StackCount = 1,
            Icon = 0x06001234, Underlay = 0x06000001, UiEffects = 1, Plate = 0x06000002
        };
        Encoding.UTF8.GetBytes("Ŧhe Sword", new Span<byte>(row.Name, BridgeItem.NameCapacity));
        return row;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int UseItem(int id, int state) => Done($"use {id:X} {state}");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int SelectItem(int id) => Done($"select {id:X}");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int RequestId(int id) => Done($"id {id:X}");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int DropItem(int id) => Done($"drop {id:X}");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int MoveItem(int id, int container, int slot, int stack) => Done($"move {id:X} {container:X} {slot} {stack}");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int MergeItem(int id, int target) => Done($"merge {id:X} {target:X}");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int AutoWield(int id, int mask, int placement, int unknown) => Done($"wield {id:X} {mask:X} {placement} {unknown}");

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int ValidLocations(int id, int* mask)
    {
        *mask = unchecked((uint)id) == 0x80000030 ? 0x100000 : 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Coverage(int id, int* mask)
    {
        *mask = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PluralName(int id, byte* buffer, int capacity, int* length)
    {
        *length = 0;
        return 0;
    }
}

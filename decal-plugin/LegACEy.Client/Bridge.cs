using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

// The one seam between the net48 Decal stub and this client. Two tables of stdcall function pointers cross it, and only plain
// values: integers, pointers, UTF-8 strings and byte buffers. The stub mirrors these layouts field for field; append, never reorder.
// Every call runs on the game thread, the thread Decal raises its events on.

/// <summary>Decal events into the client. The stub allocates it; <see cref="Bridge.Init"/> fills it in.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct BridgeEvents
{
    /// <summary>Decal's RenderFrame.</summary>
    public delegate* unmanaged[Stdcall]<void> Frame;
    /// <summary>Decal's WindowMessage: (hwnd, msg, wParam, lParam), returning 1 to eat the message.</summary>
    public delegate* unmanaged[Stdcall]<int, int, int, int, int> WindowMessage;
    /// <summary>Decal's EchoFilter.ServerDispatch: (message type, raw bytes, length). The bytes are only read during the call.</summary>
    public delegate* unmanaged[Stdcall]<int, byte*, int, void> ServerMessage;
    /// <summary>CharacterFilter.LoginComplete.</summary>
    public delegate* unmanaged[Stdcall]<void> LoginComplete;
    /// <summary>CharacterFilter.Logoff.</summary>
    public delegate* unmanaged[Stdcall]<void> Logoff;
    /// <summary>CoreManager.ItemSelected.</summary>
    public delegate* unmanaged[Stdcall]<void> ItemSelected;
    /// <summary>CoreManager.ContainerOpened.</summary>
    public delegate* unmanaged[Stdcall]<void> ContainerOpened;
    /// <summary>A WorldFilter create, change, release or move: (object id, its container).</summary>
    public delegate* unmanaged[Stdcall]<int, int, void> InventoryChanged;
    /// <summary>The plugin's Shutdown. No event is raised after it.</summary>
    public delegate* unmanaged[Stdcall]<void> Shutdown;
}

/// <summary>
/// Calls out to Decal, implemented by the stub. Each returns 0, or the HRESULT of the exception Decal threw (the stub catches every
/// exception); the client raises a failure as a <see cref="COMException"/>, as Decal's own calls did. Values come back through the last
/// pointer argument. Ids are Decal's ints; the client reads them as unsigned.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct BridgeCalls
{
    /// <summary>(out device): the game's IDirect3DDevice9, or zero when Decal has none yet.</summary>
    public delegate* unmanaged[Stdcall]<IntPtr*, int> Device;
    /// <summary>(fact, out value): one <see cref="BridgeFact"/> value.</summary>
    public delegate* unmanaged[Stdcall]<int, int*, int> Fact;
    /// <summary>(text, buffer, capacity, out length): one <see cref="BridgeText"/> as UTF-8; length is the full byte count.</summary>
    public delegate* unmanaged[Stdcall]<int, byte*, int, int*, int> Text;
    /// <summary>(rows, capacity, out count): the character's inventory (WorldFilter.GetInventory); count is the full row count.</summary>
    public delegate* unmanaged[Stdcall]<BridgeItem*, int, int*, int> Inventory;
    /// <summary>(id, row, out found): any world object by id; found is 0 when the world has no such object.</summary>
    public delegate* unmanaged[Stdcall]<int, BridgeItem*, int*, int> Item;
    /// <summary>Actions.UseItem(id, state).</summary>
    public delegate* unmanaged[Stdcall]<int, int, int> UseItem;
    /// <summary>Actions.SelectItem(id).</summary>
    public delegate* unmanaged[Stdcall]<int, int> SelectItem;
    /// <summary>Actions.RequestId(id).</summary>
    public delegate* unmanaged[Stdcall]<int, int> RequestId;
    /// <summary>Actions.DropItem(id).</summary>
    public delegate* unmanaged[Stdcall]<int, int> DropItem;
    /// <summary>Actions.MoveItem(id, container, slot, stack), stack 0 or 1.</summary>
    public delegate* unmanaged[Stdcall]<int, int, int, int, int> MoveItem;
    /// <summary>Actions.MoveItem(id, target): the two-argument move, which merges onto a stack.</summary>
    public delegate* unmanaged[Stdcall]<int, int, int> MergeItem;
    /// <summary>Actions.AutoWield(id, slot mask, explicit placement, unknown).</summary>
    public delegate* unmanaged[Stdcall]<int, int, int, int, int> AutoWield;
}

/// <summary>The integer facts <see cref="BridgeCalls.Fact"/> answers.</summary>
public enum BridgeFact
{
    /// <summary>CharacterFilter.Id.</summary>
    CharacterId,
    Health, MaxHealth, Stamina, MaxStamina, Mana, MaxMana,
    /// <summary>CharacterFilter.BurdenUnits.</summary>
    Burden,
    /// <summary>The effective Strength attribute.</summary>
    Strength,
    /// <summary>Character property 230, ACE's AugmentationIncreasedCarryingCapacity.</summary>
    CarryingAugmentations,
    /// <summary>The character object's ItemSlots.</summary>
    MainPackCapacity,
    /// <summary>The character object's icon as a full portal id (0x06 prefix added).</summary>
    MainPackIcon,
    /// <summary>The character object's PackSlots.</summary>
    SidePackCapacity,
    /// <summary>Actions.OpenedContainer.</summary>
    OpenedContainer,
    /// <summary>Actions.CurrentSelection.</summary>
    CurrentSelection
}

/// <summary>The strings <see cref="BridgeCalls.Text"/> answers.</summary>
public enum BridgeText
{
    CharacterName,
    ServerName,
    /// <summary>The folder holding the stub DLL: the log, settings and Plugins\ live there.</summary>
    PluginDirectory
}

/// <summary>
/// One world object as <c>DecalInventoryPort</c> read it: the <see cref="InventoryObjectRead"/> fields, with the icon visual already
/// in full portal ids.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct BridgeItem
{
    public const int NameCapacity = 128;

    public uint Id;
    public uint Container;
    public int Slot;
    public uint EquippedMask;
    public uint ValidLocations;
    public int StackCount;
    public int StackMax;
    public int ItemSlots;
    /// <summary>1 when the object takes a side-pack slot (a pack or a focus).</summary>
    public int IsContainer;
    public int Wcid;
    public uint Icon;
    public uint Underlay;
    public uint Overlay;
    public uint UiEffects;
    public uint Plate;
    /// <summary>The name in UTF-8, zero-padded.</summary>
    // ponytail: a longer name is cut at 128 bytes; AC item names are far shorter
    public fixed byte Name[NameCapacity];

    internal InventoryObjectRead ToRead()
    {
        fixed (byte* name = Name)
        {
            var length = new ReadOnlySpan<byte>(name, NameCapacity).IndexOf((byte)0);
            return new InventoryObjectRead(Id, Encoding.UTF8.GetString(name, length < 0 ? NameCapacity : length), Container, Slot,
                EquippedMask, ValidLocations, StackCount, StackMax, ItemSlots, IsContainer != 0, Wcid,
                new ItemVisual(Icon, Underlay, Overlay, UiEffects, Plate));
        }
    }
}

/// <summary>The client's side of each Decal event. <see cref="ClientUiRuntime"/> is the client.</summary>
internal interface IClientEvents
{
    void Frame();
    bool WindowMessage(int hwnd, int message, int wParam, int lParam);
    void ServerMessage(int type, ReadOnlySpan<byte> raw);
    void LoginComplete();
    void Logoff();
    void ItemSelected();
    void ContainerOpened();
    void InventoryChanged(uint id, uint container);
    void Shutdown();
    /// <summary>Switches LegACEy UI off after a failure that reached the bridge.</summary>
    void Disable(Exception exception);
}

/// <summary>
/// The client's end of the bridge. Every event entry point catches its own exceptions and switches LegACEy UI off: nothing unwinds into
/// the stub. The calls out are the client's only way to Decal.
/// </summary>
public static unsafe class Bridge
{
    private static BridgeCalls _calls;
    private static IClientEvents? _client;

    /// <summary>The stub's one call: fills in <paramref name="events"/>, keeps a copy of <paramref name="calls"/>, starts the client. 0 is success.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int Init(BridgeEvents* events, BridgeCalls* calls) => Start(events, calls, static () =>
    {
        var runtime = new ClientUiRuntime();
        runtime.Startup();
        return runtime;
    });

    internal static int Start(BridgeEvents* events, BridgeCalls* calls, Func<IClientEvents> createClient)
    {
        try
        {
            _calls = *calls;
            var directory = Text(BridgeText.PluginDirectory);
            if (directory.Length > 0) ClientUiRuntime.PluginDirectory = directory;
            _client = createClient();
            *events = new BridgeEvents
            {
                Frame = &OnFrame, WindowMessage = &OnWindowMessage, ServerMessage = &OnServerMessage, LoginComplete = &OnLoginComplete,
                Logoff = &OnLogoff, ItemSelected = &OnItemSelected, ContainerOpened = &OnContainerOpened,
                InventoryChanged = &OnInventoryChanged, Shutdown = &OnShutdown
            };
            return 0;
        }
        catch (Exception exception)
        {
            ClientUiRuntime.Log($"LegACEy client could not start: {exception}");
            _client = null;
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnFrame() => Raise(static client => client.Frame());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnWindowMessage(int hwnd, int message, int wParam, int lParam)
    {
        if (_client is not { } client) return 0;
        try { return client.WindowMessage(hwnd, message, wParam, lParam) ? 1 : 0; }
        catch (Exception exception) { Fail(client, exception); return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnServerMessage(int type, byte* raw, int length)
    {
        if (_client is not { } client) return;
        try { client.ServerMessage(type, new ReadOnlySpan<byte>(raw, length)); }
        catch (Exception exception) { Fail(client, exception); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnLoginComplete() => Raise(static client => client.LoginComplete());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnLogoff() => Raise(static client => client.Logoff());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnItemSelected() => Raise(static client => client.ItemSelected());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnContainerOpened() => Raise(static client => client.ContainerOpened());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnInventoryChanged(int id, int container)
    {
        if (_client is not { } client) return;
        try { client.InventoryChanged(unchecked((uint)id), unchecked((uint)container)); }
        catch (Exception exception) { Fail(client, exception); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnShutdown()
    {
        var client = _client;
        _client = null;
        try { client?.Shutdown(); }
        catch (Exception exception) { ClientUiRuntime.Log($"LegACEy client shutdown failed: {exception}"); }
    }

    private static void Raise(Action<IClientEvents> raise)
    {
        if (_client is not { } client) return;
        try { raise(client); }
        catch (Exception exception) { Fail(client, exception); }
    }

    private static void Fail(IClientEvents client, Exception exception)
    {
        try { client.Disable(exception); }
        catch (Exception again) { ClientUiRuntime.Log($"LegACEy UI could not switch off after {exception}: {again}"); }
    }

    // Calls out to Decal. A failed call throws the COMException Decal's own call would have.

    private static void Check(int result, string call)
    {
        if (result != 0) throw new COMException($"Decal call {call} failed.", result);
    }

    internal static IntPtr Device()
    {
        IntPtr device;
        Check(_calls.Device(&device), nameof(Device));
        return device;
    }

    internal static int Fact(BridgeFact fact)
    {
        int value;
        Check(_calls.Fact((int)fact, &value), fact.ToString());
        return value;
    }

    internal static string Text(BridgeText text)
    {
        Span<byte> buffer = stackalloc byte[256];
        int length;
        fixed (byte* bytes = buffer) Check(_calls.Text((int)text, bytes, buffer.Length, &length), text.ToString());
        if (length <= buffer.Length) return Encoding.UTF8.GetString(buffer[..length]);
        var large = new byte[length];
        fixed (byte* bytes = large) Check(_calls.Text((int)text, bytes, large.Length, &length), text.ToString());
        return Encoding.UTF8.GetString(large, 0, Math.Min(length, large.Length));
    }

    internal static InventoryObjectRead[] Inventory()
    {
        var rows = new BridgeItem[256];
        int count;
        while (true)
        {
            fixed (BridgeItem* first = rows) Check(_calls.Inventory(first, rows.Length, &count), nameof(Inventory));
            if (count <= rows.Length) break;
            rows = new BridgeItem[count];
        }
        var reads = new InventoryObjectRead[count];
        for (var i = 0; i < count; i++) reads[i] = rows[i].ToRead();
        return reads;
    }

    /// <summary>Any world object by id, or null when the world has none.</summary>
    internal static InventoryObjectRead? Item(uint id)
    {
        BridgeItem row;
        int found;
        Check(_calls.Item(unchecked((int)id), &row, &found), nameof(Item));
        return found != 0 ? row.ToRead() : null;
    }

    internal static GameStateSnapshot ReadGameState() => new(Text(BridgeText.CharacterName), Text(BridgeText.ServerName),
        Fact(BridgeFact.Health), Fact(BridgeFact.MaxHealth), Fact(BridgeFact.Stamina), Fact(BridgeFact.MaxStamina),
        Fact(BridgeFact.Mana), Fact(BridgeFact.MaxMana));

    internal static void UseItem(uint id, int state) => Check(_calls.UseItem(unchecked((int)id), state), nameof(UseItem));
    internal static void SelectItem(uint id) => Check(_calls.SelectItem(unchecked((int)id)), nameof(SelectItem));
    internal static void RequestId(uint id) => Check(_calls.RequestId(unchecked((int)id)), nameof(RequestId));
    internal static void DropItem(uint id) => Check(_calls.DropItem(unchecked((int)id)), nameof(DropItem));
    internal static void MoveItem(uint id, uint container, int slot, bool stack) =>
        Check(_calls.MoveItem(unchecked((int)id), unchecked((int)container), slot, stack ? 1 : 0), nameof(MoveItem));
    internal static void MergeItem(uint id, uint target) => Check(_calls.MergeItem(unchecked((int)id), unchecked((int)target)), nameof(MergeItem));
    internal static void AutoWield(uint id, uint mask, int placement, int unknown) =>
        Check(_calls.AutoWield(unchecked((int)id), unchecked((int)mask), placement, unknown), nameof(AutoWield));
}

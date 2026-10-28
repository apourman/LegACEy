using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>The client's side of each Decal event. <see cref="ClientUiRuntime"/> is the client.</summary>
internal interface IClientEvents
{
    void Frame();
    bool WindowMessage(int hwnd, int message, int wParam, int lParam);
    void ServerMessage(int type, ReadOnlySpan<byte> raw);
    void LoginComplete();
    void Logoff();
    /// <summary>A logoff the server has let through (Decal's Authorized type), after the Logoff it also raises.</summary>
    void LogoffAuthorized();
    void ItemSelected();
    void ContainerOpened();
    void InventoryChanged(uint id, uint container);
    void Shutdown();
    /// <summary>A line the player typed into chat. True eats it, so retail does not send it.</summary>
    bool CommandLine(string text);
    /// <summary>Switches LegACEy UI off after a failure that reached the bridge.</summary>
    void Disable(Exception exception);
}

/// <summary>
/// The client's end of the bridge. Every event entry point catches its own exceptions and switches LegACEy UI off: nothing unwinds into
/// the stub. The calls out are the client's only way to Decal.
/// </summary>
public static unsafe class Bridge
{
    /// <summary>A row as the inventory reads it.</summary>
    internal static InventoryObjectRead ToRead(this ref BridgeItem row)
    {
        var bytes = MemoryMarshal.CreateReadOnlySpan(ref row.Name[0], BridgeItem.NameCapacity);
        var end = bytes.IndexOf((byte)0);
        // Not a final block: a character cut off at the end of a full buffer is dropped rather than shown as U+FFFD.
        Span<char> chars = stackalloc char[BridgeItem.NameCapacity];
        Utf8.ToUtf16(end < 0 ? bytes : bytes[..end], chars, out _, out var written, isFinalBlock: false);
        return new InventoryObjectRead(row.Id, new string(chars[..written]), row.Container, row.Slot, row.EquippedMask,
            row.ValidLocations, row.StackCount, row.StackMax, row.ItemSlots, row.IsContainer != 0, row.Wcid,
            new ItemVisual(row.Icon, row.Underlay, row.Overlay, row.UiEffects, row.Plate),
            row.MaxStructure > 0 && row.Structure < row.MaxStructure ? (double)Math.Max(0, row.Structure) / row.MaxStructure : null);
    }

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
        if (_client != null)
        {
            ClientUiRuntime.Log("LegACEy client already running; a second start was refused.");
            return 1;
        }
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
                InventoryChanged = &OnInventoryChanged, Shutdown = &OnShutdown, CommandLine = &OnCommandLine,
                LogoffAuthorized = &OnLogoffAuthorized
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
        var bytes = (IntPtr)raw;
        Raise(client => client.ServerMessage(type, new ReadOnlySpan<byte>((byte*)bytes, length)));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnLoginComplete() => Raise(static client => client.LoginComplete());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnLogoff() => Raise(static client => client.Logoff());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnLogoffAuthorized() => Raise(static client => client.LogoffAuthorized());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnItemSelected() => Raise(static client => client.ItemSelected());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnContainerOpened() => Raise(static client => client.ContainerOpened());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnInventoryChanged(int id, int container) => Raise(client => client.InventoryChanged((uint)id, (uint)container));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnCommandLine(byte* text, int length)
    {
        if (_client is not { } client) return 0;
        try { return client.CommandLine(Encoding.UTF8.GetString(text, length)) ? 1 : 0; }
        catch (Exception exception) { Fail(client, exception); return 0; }
    }

    /// <summary>A failed shutdown switches the UI off too, which gives retail's panels back; the client is gone either way.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnShutdown()
    {
        Raise(static client => client.Shutdown());
        _client = null;
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

    internal static string Text(BridgeText text) => ReadText(_calls.Text, (int)text, text.ToString());

    /// <summary>A world object's plural name, empty when it has none.</summary>
    internal static string PluralName(uint id) => ReadText(_calls.PluralName, (int)id, nameof(PluralName));

    private static string ReadText(delegate* unmanaged[Stdcall]<int, byte*, int, int*, int> call, int key, string name)
    {
        // A value that grew between the two calls is asked for again, never cut.
        Span<byte> buffer = stackalloc byte[256];
        int length;
        while (true)
        {
            fixed (byte* bytes = buffer) Check(call(key, bytes, buffer.Length, &length), name);
            if (length <= buffer.Length) return Encoding.UTF8.GetString(buffer[..length]);
            buffer = new byte[length];
        }
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
        Check(_calls.Item((int)id, &row, &found), nameof(Item));
        return found != 0 ? row.ToRead() : null;
    }

    /// <summary>The object with this id for the selection plaque, or null when the world has no such object.</summary>
    internal static RetailSelection? Selection(uint id)
    {
        BridgeItem row;
        int found;
        Check(_calls.Item((int)id, &row, &found), nameof(Item));
        if (found == 0) return null;
        var kind = row.IsCreature != 0 ? SelectionKind.Creature
            : row.HasMana != 0 ? SelectionKind.ItemWithMana
            : row.StackCount > 1 ? SelectionKind.Stack
            : SelectionKind.Other;
        var name = AppraisalContent.WithMaterial(row.ToRead().Name, AppraisalOtherLines.MaterialName(row.Material));
        return new RetailSelection(id, name, kind, StackCount: row.StackCount);
    }

    internal static GameStateSnapshot ReadGameState() => new(Text(BridgeText.CharacterName), Text(BridgeText.ServerName),
        Fact(BridgeFact.Health), Fact(BridgeFact.MaxHealth), Fact(BridgeFact.Stamina), Fact(BridgeFact.MaxStamina),
        Fact(BridgeFact.Mana), Fact(BridgeFact.MaxMana));

    internal static uint ValidLocations(uint id)
    {
        int mask;
        Check(_calls.ValidLocations((int)id, &mask), nameof(ValidLocations));
        return (uint)mask;
    }

    internal static uint CoverageOf(uint id)
    {
        int mask;
        Check(_calls.Coverage((int)id, &mask), nameof(CoverageOf));
        return (uint)mask;
    }

    internal static uint DescriptionFlagsOf(uint id)
    {
        int flags;
        Check(_calls.DescriptionFlags((int)id, &flags), nameof(DescriptionFlagsOf));
        return (uint)flags;
    }

    internal static void Chat(string text, int colour)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* first = bytes) Check(_calls.Chat(first, bytes.Length, colour), nameof(Chat));
    }

    internal static bool ChatClick(uint id, string tag)
    {
        var bytes = Encoding.UTF8.GetBytes(tag);
        int eaten;
        fixed (byte* first = bytes) Check(_calls.ChatClick((int)id, first, bytes.Length, &eaten), nameof(ChatClick));
        return eaten != 0;
    }

    /// <summary>An attribute, vital or skill as Decal reports it: kind 0 attribute, 1 vital (Decal's ids), 2 skill.</summary>
    internal static (int Base, int Buffed) Stat(int kind, uint id)
    {
        int @base, buffed;
        Check(_calls.Stat(kind, (int)id, &@base, &buffed), nameof(Stat));
        return (@base, buffed);
    }

    internal static void UseItem(uint id, int state) => Check(_calls.UseItem((int)id, state), nameof(UseItem));
    internal static void SelectItem(uint id) => Check(_calls.SelectItem((int)id), nameof(SelectItem));
    // Retail's own sender first: Decal's ID queue can hold the request for half a second.
    internal static void RequestId(uint id)
    {
        if (!NativeAppraisalSender.TrySend(id, ClientUiRuntime.Log)) Check(_calls.RequestId((int)id), nameof(RequestId));
    }
    internal static void DropItem(uint id) => Check(_calls.DropItem((int)id), nameof(DropItem));
    internal static void MoveItem(uint id, uint container, int slot, bool stack) =>
        Check(_calls.MoveItem((int)id, (int)container, slot, stack ? 1 : 0), nameof(MoveItem));
    internal static void MergeItem(uint id, uint target) => Check(_calls.MergeItem((int)id, (int)target), nameof(MergeItem));
    internal static void AutoWield(uint id, uint mask, int placement, int unknown) =>
        Check(_calls.AutoWield((int)id, (int)mask, placement, unknown), nameof(AutoWield));
}

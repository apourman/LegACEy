using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The bridge's calls out, answered from Decal on the game thread. Every call catches its own exceptions and returns their HRESULT:
/// nothing unwinds into the client's runtime. World objects leave as flat <see cref="BridgeItem"/> rows.
/// </summary>
internal static unsafe class DecalCalls
{
    private const int EFail = unchecked((int)0x80004005);
    private static readonly Guid Direct3DDevice9 = new("D0223B96-BF7A-43FD-92BD-A43B0D82B9EB");
    // Decal raises ChatNameClicked only from its hook on retail's chat. This is the handler that hook calls, (text, id, ref eat), so a
    // click in LegACEy's chat reaches every plugin's ChatNameClicked as a retail click does. Private: read from Decal.Adapter 2.9.8.3.
    private static readonly MethodInfo? ChatClickIntercept =
        typeof(CoreManager).GetMethod("myHooks_ChatClickIntercept", BindingFlags.Instance | BindingFlags.NonPublic);

    // The client calls through these delegates' pointers for the life of the process, so they are never collected.
    private static readonly List<Delegate> Alive = new();

    /// <summary>The table, allocated once and never freed: the client keeps a copy of it.</summary>
    public static BridgeCalls* Create()
    {
        var calls = (BridgeCalls*)Marshal.AllocHGlobal(sizeof(BridgeCalls));
        calls->Device = (delegate* unmanaged[Stdcall]<IntPtr*, int>)Pointer(new DeviceCall(Device));
        calls->Fact = (delegate* unmanaged[Stdcall]<int, int*, int>)Pointer(new ReadCall(Fact));
        calls->Text = (delegate* unmanaged[Stdcall]<int, byte*, int, int*, int>)Pointer(new TextCall(Text));
        calls->Inventory = (delegate* unmanaged[Stdcall]<BridgeItem*, int, int*, int>)Pointer(new InventoryCall(Inventory));
        calls->Item = (delegate* unmanaged[Stdcall]<int, BridgeItem*, int*, int>)Pointer(new ItemCall(Item));
        calls->UseItem = (delegate* unmanaged[Stdcall]<int, int, int>)Pointer(new Call2(UseItem));
        calls->SelectItem = (delegate* unmanaged[Stdcall]<int, int>)Pointer(new Call1(SelectItem));
        calls->RequestId = (delegate* unmanaged[Stdcall]<int, int>)Pointer(new Call1(RequestId));
        calls->DropItem = (delegate* unmanaged[Stdcall]<int, int>)Pointer(new Call1(DropItem));
        calls->MoveItem = (delegate* unmanaged[Stdcall]<int, int, int, int, int>)Pointer(new Call4(MoveItem));
        calls->MergeItem = (delegate* unmanaged[Stdcall]<int, int, int>)Pointer(new Call2(MergeItem));
        calls->AutoWield = (delegate* unmanaged[Stdcall]<int, int, int, int, int>)Pointer(new Call4(AutoWield));
        calls->ValidLocations = (delegate* unmanaged[Stdcall]<int, int*, int>)Pointer(new ReadCall(ValidLocations));
        calls->Coverage = (delegate* unmanaged[Stdcall]<int, int*, int>)Pointer(new ReadCall(Coverage));
        calls->PluralName = (delegate* unmanaged[Stdcall]<int, byte*, int, int*, int>)Pointer(new TextCall(PluralName));
        calls->DescriptionFlags = (delegate* unmanaged[Stdcall]<int, int*, int>)Pointer(new ReadCall(DescriptionFlags));
        calls->Chat = (delegate* unmanaged[Stdcall]<byte*, int, int, int>)Pointer(new ChatCall(Chat));
        calls->ChatClick = (delegate* unmanaged[Stdcall]<int, byte*, int, int*, int>)Pointer(new ChatClickCall(ChatClick));
        calls->Stat = (delegate* unmanaged[Stdcall]<int, int, int*, int*, int>)Pointer(new StatCall(Stat));
        return calls;
    }

    private static void* Pointer(Delegate call)
    {
        Alive.Add(call);
        return (void*)Marshal.GetFunctionPointerForDelegate(call);
    }

    private static int Fail(Exception exception) => Marshal.GetHRForException(exception) is var result && result != 0 ? result : EFail;

    private static int Device(IntPtr* device) => Act(() => *device = OpenDevice());

    private static IntPtr OpenDevice()
    {
        var iid = Direct3DDevice9;
        var game = CoreManager.Current.Decal.Underlying.GetD3DDevice(ref iid);
        if (game == null) return IntPtr.Zero;
        var unknown = Marshal.GetIUnknownForObject(game);
        // ponytail: the client keeps this reference for the session and never releases it, as Managed DirectX's Device did.
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out var device));
            return device;
        }
        finally { Marshal.Release(unknown); }
    }

    private static int Fact(int fact, int* value) => Act(() => *value = ReadFact((BridgeFact)fact));

    // The augmentation count is ACE's AugmentationIncreasedCarryingCapacity (PropertyInt 230).
    private const int CarryingAugmentationProperty = 230;
    private const int SocietyProperty = 281;
    private const int HeritageProperty = 188;

    private static int ReadFact(BridgeFact fact)
    {
        var core = CoreManager.Current;
        var character = core.CharacterFilter;
        return fact switch
        {
            BridgeFact.CharacterId => character.Id,
            BridgeFact.Health => character.Health,
            BridgeFact.MaxHealth => Maximum(CharFilterVitalType.Health),
            BridgeFact.Stamina => character.Stamina,
            BridgeFact.MaxStamina => Maximum(CharFilterVitalType.Stamina),
            BridgeFact.Mana => character.Mana,
            BridgeFact.MaxMana => Maximum(CharFilterVitalType.Mana),
            BridgeFact.Burden => (int)character.BurdenUnits,
            BridgeFact.Strength => character.EffectiveAttribute[CharFilterAttributeType.Strength],
            BridgeFact.CarryingAugmentations => character.GetCharProperty(CarryingAugmentationProperty),
            // The main pack is the character's own object: ItemSlots is its slot count, and PackSlots counts the side packs.
            BridgeFact.MainPackCapacity => CharacterObject()?.Values(LongValueKey.ItemSlots, 0) ?? 0,
            BridgeFact.SidePackCapacity => CharacterObject()?.Values(LongValueKey.PackSlots, 0) ?? 0,
            BridgeFact.OpenedContainer => core.Actions.OpenedContainer,
            BridgeFact.CurrentSelection => core.Actions.CurrentSelection,
            BridgeFact.CombatMode => (int)core.Actions.CombatMode,
            BridgeFact.CharacterMembership => Membership(character),
            _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, "Unknown bridge fact.")
        };
    }

    /// <summary>
    /// A vital's maximum as retail shows it, through Decal's hooks: the character filter's effective value leaves out vitae (43 where retail
    /// shows 41 with 5% vitae). The filter's value stands in when the hooks give nothing.
    /// </summary>
    private static int Maximum(CharFilterVitalType vital)
    {
        var hooked = CoreManager.Current.Actions.Vital[vital switch
        {
            CharFilterVitalType.Health => VitalType.MaximumHealth,
            CharFilterVitalType.Stamina => VitalType.MaximumStamina,
            _ => VitalType.MaximumMana
        }];
        return hooked > 0 ? hooked : CoreManager.Current.CharacterFilter.EffectiveVital[vital];
    }

    /// <summary>The allegiance, society and Olthoi bits of <see cref="BridgeFact.CharacterMembership"/>. Vassals are the followers count.</summary>
    private static int Membership(CharacterFilter character)
    {
        var bits = 0;
        if (Has(() => character.Allegiance != null)) bits |= 1;
        if (Has(() => character.Monarch != null)) bits |= 2;
        if (Has(() => character.Patron != null)) bits |= 4;
        if (Has(() => character.Followers > 0)) bits |= 8;
        // Faction1Bits (281): any of the three society bits.
        if ((character.GetCharProperty(SocietyProperty) & 7) != 0) bits |= 16;
        // HeritageGroup (188): 12 and 13 are the Olthoi heritages.
        var heritage = character.GetCharProperty(HeritageProperty);
        if (heritage == 12 || heritage == 13) bits |= 32;
        return bits;
    }

    // Decal's allegiance getters throw for a character with no allegiance, monarch or patron rather than return null.
    private static bool Has(Func<bool> read)
    {
        try { return read(); }
        catch (Exception exception) when (exception is NullReferenceException or COMException) { return false; }
    }

    private static WorldObject? CharacterObject() => CoreManager.Current.WorldFilter[CoreManager.Current.CharacterFilter.Id];

    private static int Text(int text, byte* buffer, int capacity, int* length) => Act(() =>
    {
        var value = (BridgeText)text switch
            {
            BridgeText.CharacterName => CoreManager.Current.CharacterFilter.Name,
            BridgeText.ServerName => CoreManager.Current.CharacterFilter.Server,
            BridgeText.PluginDirectory => Plugin.Directory,
            BridgeText.Race => CoreManager.Current.CharacterFilter.Race,
            BridgeText.Gender => CoreManager.Current.CharacterFilter.Gender,
            _ => throw new ArgumentOutOfRangeException(nameof(text), text, "Unknown bridge text.")
        };
        Utf8(value, buffer, capacity, length);
    });

    private static int PluralName(int id, byte* buffer, int capacity, int* length) =>
        Act(() => Utf8(CoreManager.Current.WorldFilter[id]?.Values(StringValueKey.SecondaryName, string.Empty), buffer, capacity, length));

    private static void Utf8(string? value, byte* buffer, int capacity, int* length)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        *length = bytes.Length;
        Marshal.Copy(bytes, 0, (IntPtr)buffer, Math.Min(bytes.Length, capacity));
    }

    private static int Inventory(BridgeItem* rows, int capacity, int* count) => Act(() =>
    {
        var written = 0;
        var seen = new HashSet<int>();
        void Add(WorldObject item)
        {
            if (!seen.Add(item.Id)) return;
            if (written < capacity) Flatten(item, rows + written);
            written++;
        }
        var world = CoreManager.Current.WorldFilter;
        foreach (WorldObject item in world.GetInventory()) Add(item);
        // Decal's inventory list missed objects that came into a pack this session (a split stack, one picked up off the ground, one the
        // Vault put back) until a relog, though their Container already said where they are; the packs' contents are read from that too.
        var character = CoreManager.Current.CharacterFilter.Id;
        var packs = new List<int> { character };
        foreach (WorldObject item in world.GetByContainer(character))
            if (item.ObjectClass == ObjectClass.Container) packs.Add(item.Id);
        var missed = 0;
        foreach (var pack in packs)
            foreach (WorldObject item in world.GetByContainer(pack))
                if (!seen.Contains(item.Id))
                {
                    Add(item);
                    missed++;
                }
        if (missed > 0 && _loggedMissed++ < 5) Plugin.Log($"Decal's inventory list missed {missed} object(s) its packs hold; read from the packs.");
        *count = written;
    });

    private static int _loggedMissed;

    private static int Item(int id, BridgeItem* row, int* found) => Act(() =>
    {
        var item = CoreManager.Current.WorldFilter[id];
        *found = item == null ? 0 : 1;
        if (item != null) Flatten(item, row);
    });

    private static int ValidLocations(int id, int* mask) =>
        Act(() => *mask = CoreManager.Current.WorldFilter[id]?.Values(LongValueKey.EquipableSlots, 0) ?? 0);

    private static int Coverage(int id, int* mask) =>
        Act(() => *mask = CoreManager.Current.WorldFilter[id]?.Values(LongValueKey.Coverage, 0) ?? 0);

    private static int DescriptionFlags(int id, int* flags) =>
        Act(() => *flags = CoreManager.Current.WorldFilter[id]?.Values(LongValueKey.Behavior, 0) ?? 0);

    // Foci are not containers but take a side-pack slot: the server's RequiresPackSlot description flag (0x00800000).
    // Gate: Decal's Behavior is the object description flags.
    private const int RequiresPackSlot = 0x00800000;

    /// <summary>One world object as the inventory reads it, icon visual in full portal ids.</summary>
    private static void Flatten(WorldObject item, BridgeItem* row)
    {
        *row = default;
        row->Id = unchecked((uint)item.Id);
        row->Container = unchecked((uint)item.Container);
        row->Slot = item.Values(LongValueKey.Slot, -1);
        row->EquippedMask = unchecked((uint)item.Values(LongValueKey.EquippedSlots, 0));
        row->ValidLocations = unchecked((uint)item.Values(LongValueKey.EquipableSlots, 0));
        row->StackCount = item.Values(LongValueKey.StackCount, 1);
        row->StackMax = item.Values(LongValueKey.StackMax, 0);
        row->ItemSlots = item.Values(LongValueKey.ItemSlots, 0);
        row->IsContainer = item.ObjectClass == ObjectClass.Container || (item.Values(LongValueKey.Behavior, 0) & RequiresPackSlot) != 0 ? 1 : 0;
        // Decal's Type is the weenie class id (WCID): pyreals are 273.
        row->Wcid = item.Values(LongValueKey.Type, 0);
        row->Icon = Texture(item.Icon);
        row->Underlay = Texture(item.Values(LongValueKey.IconUnderlay, 0));
        row->Overlay = Texture(item.Values(LongValueKey.IconOverlay, 0));
        row->UiEffects = unchecked((uint)item.Values(LongValueKey.IconOutline, 0));
        // Gate: Decal's Category is the item type (ACE's ItemType), which picks the plate.
        row->Plate = ItemIcon.PlateFor(unchecked((uint)item.Values(LongValueKey.Category, 0)));
        var name = Encoding.UTF8.GetBytes(item.Name ?? string.Empty);
        Marshal.Copy(name, 0, (IntPtr)row->Name, Math.Min(name.Length, BridgeItem.NameCapacity));
        row->IsCreature = item.ObjectClass is ObjectClass.Monster or ObjectClass.Npc or ObjectClass.Player ? 1 : 0;
        // Gate: Decal's MaximumMana is the item's ItemMaxMana (ACE property 108), read in play for an item with mana.
        row->HasMana = item.Values(LongValueKey.MaximumMana, 0) > 0 ? 1 : 0;
        // Gate: Decal's Material is the item's MaterialType (ACE property 131), sent with the object.
        row->Material = item.Values(LongValueKey.Material, 0);
        // Gate: Decal's UsesRemaining and UsesTotal are the item's Structure and MaxStructure (ACE 92 and 91), sent with the object.
        row->Structure = item.Values(LongValueKey.UsesRemaining, 0);
        row->MaxStructure = item.Values(LongValueKey.UsesTotal, 0);
    }

    /// <summary>Decal reports portal texture ids without their 0x06 prefix; zero means none.</summary>
    private static uint Texture(int value) =>
        value == 0 ? 0 : (value & 0xFF000000) == 0 ? unchecked((uint)value) | 0x06000000 : unchecked((uint)value);


    private static int UseItem(int id, int state) => Act(() => CoreManager.Current.Actions.UseItem(id, state));
    private static int SelectItem(int id) => Act(() => CoreManager.Current.Actions.SelectItem(id));
    // Every ID we ask for is the player's own (right-click, E, the window following the selection), so it skips Decal's ID queue,
    // which other plugins fill at login; Actions.RequestId would wait behind them. Only the latest one stays pending, as in retail.
    private static int RequestId(int id) => Act(() => CoreManager.Current.IDQueue.ShortcircuitID(id));
    private static int DropItem(int id) => Act(() => CoreManager.Current.Actions.DropItem(id));
    private static int MoveItem(int id, int container, int slot, int stack) =>
        Act(() => CoreManager.Current.Actions.MoveItem(id, container, slot, stack != 0));
    private static int MergeItem(int id, int target) => Act(() => CoreManager.Current.Actions.MoveItem(id, target));
    private static int AutoWield(int id, int mask, int placement, int unknown) =>
        Act(() => CoreManager.Current.Actions.AutoWield(id, mask, placement, unknown));

    private static int Chat(byte* text, int length, int colour) =>
        Act(() => CoreManager.Current.Actions.AddChatText(Encoding.UTF8.GetString(text, length), colour));

    private static int ChatClick(int id, byte* text, int length, int* eaten) => Act(() =>
    {
        var arguments = new object[] { length == 0 ? string.Empty : Encoding.UTF8.GetString(text, length), id, false };
        ChatClickIntercept!.Invoke(CoreManager.Current, arguments);
        *eaten = (bool)arguments[2] ? 1 : 0;
    });

    private static int Stat(int kind, int id, int* @base, int* buffed) => Act(() =>
    {
        var character = CoreManager.Current.CharacterFilter;
        switch (kind)
        {
            case 0:
                var attribute = character.Attributes[(CharFilterAttributeType)id];
                *@base = attribute.Base;
                *buffed = attribute.Buffed;
                break;
            case 1:
                var vital = character.Vitals[(CharFilterVitalType)id];
                *@base = vital.Base;
                *buffed = Maximum((CharFilterVitalType)id);
                break;
            default:
                var skill = character.Skills[(CharFilterSkillType)id];
                *@base = skill.Base;
                *buffed = skill.Buffed;
                break;
        }
    });

    // ponytail: a closure per call, some every frame; plain try/catch per call if the allocations ever show up.
    private static int Act(Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception exception) { return Fail(exception); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DeviceCall(IntPtr* device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadCall(int key, int* value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TextCall(int text, byte* buffer, int capacity, int* length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InventoryCall(BridgeItem* rows, int capacity, int* count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ItemCall(int id, BridgeItem* row, int* found);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ChatCall(byte* text, int length, int colour);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ChatClickCall(int id, byte* text, int length, int* eaten);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StatCall(int kind, int id, int* @base, int* buffed);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call1(int a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call2(int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call4(int a, int b, int c, int d);
}

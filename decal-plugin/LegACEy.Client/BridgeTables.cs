using System;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

// The one seam between the net48 Decal stub and this client. Two tables of stdcall function pointers cross it, and only plain
// values: integers, pointers, UTF-8 strings and byte buffers. Every call runs on the game thread, the thread Decal raises its events on.
// This file holds only the layouts and builds on net48 too (the DecalPlugin project compiles it), so the stub uses this same file
// rather than a mirror. Append fields, never reorder them.

/// <summary>Decal events into the client. The stub allocates it; Bridge.Init fills it in.</summary>
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
    /// <summary>Decal's CommandLineText: (UTF-8 text, length), returning 1 to eat the line. Null from a client built before it.</summary>
    public delegate* unmanaged[Stdcall]<byte*, int, int> CommandLine;
    /// <summary>CharacterFilter.Logoff of the Authorized type, raised after Logoff. Null from a client built before it.</summary>
    public delegate* unmanaged[Stdcall]<void> LogoffAuthorized;
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
    /// <summary>(id, out mask): one world object's valid locations (EquipableSlots), 0 when the world has no such object. A lean
    /// <see cref="Item"/> for the per-frame read during a retail drag over the paperdoll.</summary>
    public delegate* unmanaged[Stdcall]<int, int*, int> ValidLocations;
    /// <summary>(id, out mask): one world object's coverage (Decal's Coverage, the clothing priority bits), 0 when the world has no such
    /// object. Read for the appraisal window's "Covers" line.</summary>
    public delegate* unmanaged[Stdcall]<int, int*, int> Coverage;
    /// <summary>(id, buffer, capacity, out length): one world object's plural name (Decal's SecondaryName) as UTF-8, empty when it has none
    /// or the world has no such object; length is the full byte count. Read for the appraisal window's title on a stack.</summary>
    public delegate* unmanaged[Stdcall]<int, byte*, int, int*, int> PluralName;
    /// <summary>(id, out flags): one world object's description flags (Decal's Behavior, ACE's ObjectDescriptionFlag), 0 when the world has
    /// no such object. Read for whether the appraisal window offers an inscription: the server's appraisal does not say an item is inscribable.</summary>
    public delegate* unmanaged[Stdcall]<int, int*, int> DescriptionFlags;
    /// <summary>(UTF-8 text, length, colour): Actions.AddChatText, a line in the chat window; the colour is Decal's (5 is System).</summary>
    public delegate* unmanaged[Stdcall]<byte*, int, int, int> Chat;
    /// <summary>(id, UTF-8 tag text, length, out eaten): raises CoreManager.ChatNameClicked as a click on a chat link in retail does;
    /// eaten is 1 when a plugin ate it.</summary>
    public delegate* unmanaged[Stdcall]<int, byte*, int, int*, int> ChatClick;
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
    /// <summary>The character object's PackSlots.</summary>
    SidePackCapacity,
    /// <summary>Actions.OpenedContainer.</summary>
    OpenedContainer,
    /// <summary>Actions.CurrentSelection.</summary>
    CurrentSelection,
    /// <summary>Actions.CombatMode: the player's combat stance, Decal's values (1 peace, 2 melee, 4 missile, 8 magic).</summary>
    CombatMode,
    /// <summary>
    /// The player's allegiance and society facts as bits: 1 in an allegiance, 2 with a monarch, 4 with a patron, 8 with vassals, 16 in a
    /// society, 32 an Olthoi (heritage 12 or 13). Decal reads them from CharacterFilter.
    /// </summary>
    CharacterMembership,
    /// <summary>CharacterFilter.Vitae: Decal's vitae penalty. Its unit (percent lost or percent kept) is not confirmed yet.</summary>
    Vitae,
    /// <summary>Actions.ChatState: 1 while Decal sees retail's chat entry open. VTank waits on it.</summary>
    ChatState
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
/// One world object as the stub flattens it from Decal (DecalCalls): the <see cref="InventoryObjectRead"/> fields, with the icon visual already
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
    /// <summary>1 when the object is a creature (Decal's Monster or Npc class), which the selection plaque gives a health meter.</summary>
    public int IsCreature;
    /// <summary>1 when the object has mana (Decal's MaximumMana), which the selection plaque gives a mana meter.</summary>
    public int HasMana;
}

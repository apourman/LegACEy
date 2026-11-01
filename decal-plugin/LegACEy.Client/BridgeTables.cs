using System;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

// The one seam between the net48 Decal stub and this client. Two tables of stdcall function pointers cross it, and only plain
// values: integers, pointers, UTF-8 strings and byte buffers. Every call runs on the game thread, the thread Decal raises its events on.
// This file holds only the layouts and builds on net48 too (the DecalPlugin project compiles it), so the stub uses this same file
// rather than a mirror. Append fields, never reorder them.

/// <summary>Decal events into the client. The stub allocates it; Bridge.Init fills it in, with a do-nothing function in each unused slot.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct BridgeEvents
{
    /// <summary>Decal's RenderFrame. Unused since 177: the client hooks retail's SceneTool::EndFrame.</summary>
    public delegate* unmanaged[Stdcall]<void> Frame;
    /// <summary>Decal's WindowMessage: (hwnd, msg, wParam, lParam), returning 1 to eat the message. Unused since 177: the client subclasses the game window.</summary>
    public delegate* unmanaged[Stdcall]<int, int, int, int, int> WindowMessage;
    /// <summary>Decal's EchoFilter.ServerDispatch: (message type, raw bytes, length). Unused since 177: the client hooks retail's message queue.</summary>
    public delegate* unmanaged[Stdcall]<int, byte*, int, void> ServerMessage;
    /// <summary>CharacterFilter.LoginComplete. Unused since 178: the server's Login_CreatePlayer starts the session.</summary>
    public delegate* unmanaged[Stdcall]<void> LoginComplete;
    /// <summary>CharacterFilter.Logoff. Unused since 178: the server's Login_LogOffCharacter ends the session.</summary>
    public delegate* unmanaged[Stdcall]<void> Logoff;
    /// <summary>CoreManager.ItemSelected. Unused since 179: the client compares the game's selected id each frame.</summary>
    public delegate* unmanaged[Stdcall]<void> ItemSelected;
    /// <summary>CoreManager.ContainerOpened.</summary>
    public delegate* unmanaged[Stdcall]<void> ContainerOpened;
    /// <summary>A WorldFilter create, change, release or move: (object id, its container).</summary>
    public delegate* unmanaged[Stdcall]<int, int, void> InventoryChanged;
    /// <summary>The plugin's Shutdown. No event is raised after it.</summary>
    public delegate* unmanaged[Stdcall]<void> Shutdown;
    /// <summary>Decal's CommandLineText: (UTF-8 text, length), returning 1 to eat the line. Unused since 177: the client hooks retail's chat commands.</summary>
    public delegate* unmanaged[Stdcall]<byte*, int, int> CommandLine;
    /// <summary>CharacterFilter.Logoff of the Authorized type, raised after Logoff. Unused since 178, as Logoff.</summary>
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
    /// <summary>(out device): the game's IDirect3DDevice9. Unused since 177: the client's EndScene hook reads it; the stub fails it.</summary>
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
    /// <summary>Actions.SelectItem(id). Unused since 179: the client calls retail's SetSelectedObject.</summary>
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
    /// <summary>(kind, id, out base, out buffed): an attribute, a vital's maximum or a skill. Unused since 180: the client reads the player's
    /// qualities; the stub fails it.</summary>
    public delegate* unmanaged[Stdcall]<int, int, int*, int*, int> Stat;
}

/// <summary>The integer facts <see cref="BridgeCalls.Fact"/> answers.</summary>
public enum BridgeFact
{
    /// <summary>CharacterFilter.Id. Unused since 178: the server's PlayerDescription names the character.</summary>
    CharacterId,
    /// <summary>CharacterFilter.Health. Unused since 180: the client reads the player's qualities.</summary>
    Health,
    /// <summary>CharacterFilter.MaxHealth. Unused since 180: the client reads the player's qualities.</summary>
    MaxHealth,
    /// <summary>CharacterFilter.Stamina. Unused since 180: the client reads the player's qualities.</summary>
    Stamina,
    /// <summary>CharacterFilter.MaxStamina. Unused since 180: the client reads the player's qualities.</summary>
    MaxStamina,
    /// <summary>CharacterFilter.Mana. Unused since 180: the client reads the player's qualities.</summary>
    Mana,
    /// <summary>CharacterFilter.MaxMana. Unused since 180: the client reads the player's qualities.</summary>
    MaxMana,
    /// <summary>CharacterFilter.BurdenUnits. Unused since 180: the client reads the player's EncumbranceVal.</summary>
    Burden,
    /// <summary>The effective Strength. Unused since 180: the client reads the player's qualities.</summary>
    Strength,
    /// <summary>Property 230, the carrying augmentation. Unused since 180: the client reads the player's qualities.</summary>
    CarryingAugmentations,
    /// <summary>The character object's ItemSlots. Unused since 180: the client reads the player's weenie.</summary>
    MainPackCapacity,
    /// <summary>The character object's PackSlots. Unused since 180: the client reads the player's weenie.</summary>
    SidePackCapacity,
    /// <summary>Actions.OpenedContainer.</summary>
    OpenedContainer,
    /// <summary>Actions.CurrentSelection. Unused since 179: the client reads the game's selected id.</summary>
    CurrentSelection,
    /// <summary>Actions.CombatMode: the player's combat stance (1 peace, 2 melee, 4 missile, 8 magic). Unused since 179: the client reads retail's own combat mode.</summary>
    CombatMode,
    /// <summary>The allegiance and society bits. Unused since 180: the client reads the server's allegiance update.</summary>
    CharacterMembership
}

/// <summary>The strings <see cref="BridgeCalls.Text"/> answers.</summary>
public enum BridgeText
{
    /// <summary>Unused since 178, as ServerName, Race and Gender: the server's PlayerDescription and Login_WorldInfo give them.</summary>
    CharacterName,
    ServerName,
    /// <summary>The folder holding the stub DLL: the log, settings and Plugins\ live there. Unused since 177: the client finds it from its own folder.</summary>
    PluginDirectory,
    /// <summary>CharacterFilter.Race: the heritage, such as "Viamontian".</summary>
    Race,
    /// <summary>CharacterFilter.Gender.</summary>
    Gender
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
    /// <summary>The MaterialType value (ACE property 131), 0 for none; the plaque puts its name in front ("Steel Salvage").</summary>
    public int Material;
    /// <summary>Uses or units left and the most it holds (ACE Structure 92 and MaxStructure 91), 0 for none: a salvage bag's units.</summary>
    public int Structure;
    public int MaxStructure;
}

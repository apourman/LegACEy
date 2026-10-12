using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's "choose a target" mode: using a gem, a kit or a mana stone leaves <c>ClientUISystem</c>'s target mode (+0x2C) set, and the next
/// object the player clicks becomes the target. Retail's item lists finish it on a click: select the item, then, while the mode is set,
/// <c>ExecuteTargetModeForItem(item, mode)</c> and <c>SetTargetMode(0)</c> (gmToolbarUI at 0x004BE037). A click in a LegACEy window never
/// reaches those lists, so <see cref="TryTarget"/> does the same for it. Retail's valid and invalid target cursors judge the object under the
/// pointer, <c>SmartBox</c>'s found object, which retail's own hover over an item list sets (0x004E60E4) and re-picks only on its own mouse
/// moves; <see cref="Hover"/> sets it for the item under the pointer in a LegACEy window and updates the cursor. Call on the game thread.
/// </summary>
internal sealed class RetailTargetMode
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xDA0 in this region) for names; our objdump read of installed acclient.exe";
    private const uint ExecuteTargetModeForItemAddress = 0x005658D0, SetTargetModeAddress = 0x005656E0;
    private const uint SetFoundObjectAddress = 0x00451D30, UpdateCursorStateAddress = 0x005653D0, IsTargetCompatibleAddress = 0x00588EA0;
    private const int UseOnTargetMode = 3;
    private static readonly IntPtr FoundObject = new(0x0083DA44);
    private static readonly IntPtr UiSystemInstance = new(0x00871354);
    private const int TargetModeOffset = 0x2C;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("ClientUISystem::GetUISystem", 0x00564D30, NativeUiCatalogue.Bytes("A1 54 13 87 00 C3"), Source,
            "Cdecl () -> ClientUISystem*, read from 0x00871354"),
        new NativeUiEntry("ClientUISystem::SetTargetMode", SetTargetModeAddress, NativeUiCatalogue.Bytes("8B 44 24 04 56 8B F1 39 46 2C 74 5D 33 D2 3B C2 89 46 2C"),
            Source, "ThisCall (Target_Mode mode): stores the mode at +0x2C and updates the cursor"),
        new NativeUiEntry("ClientUISystem::ExecuteTargetModeForItem", ExecuteTargetModeForItemAddress, NativeUiCatalogue.Bytes(
            "8B 44 24 08 48 74 23 48 74 13 48 75 2E 8B 44 24 04 50 E8 39 44 02 00"), Source,
            "ThisCall (uint objectId, Target_Mode mode); mode 3 calls ItemHolder::TargetAcquired (0x00589D20), which uses the held item on the object"),
        new NativeUiEntry("ItemHolder::TargetAcquired reads the held item", 0x00589D20, NativeUiCatalogue.Bytes("55 8B 6C 24 08 56 8B 35 E4 1A 87 00"), Source,
            "Cdecl (uint target); the held source item is at 0x00871AE4"),
        new NativeUiEntry("gmToolbarUI item click finishes the target mode", 0x004BE037, NativeUiCatalogue.Bytes(
            "E8 F4 6C 0A 00 8B 48 2C 85 C9 74 32 E8 E8 6C 0A 00 8B B6 FC 05 00 00 85 F6 8B 40 2C 74 0E 50 56 E8 D4 6C 0A 00 8B C8"), Source,
            "the retail pattern followed here: mode at +0x2C, ExecuteTargetModeForItem(item, mode), then SetTargetMode(0)"),
        new NativeUiEntry("SmartBox::set_found_object", SetFoundObjectAddress, NativeUiCatalogue.Bytes("8B 44 24 04 8B 4C 24 08 50 A3 44 DA 83 00 89 0D 18 96 81 00"),
            Source, "Cdecl (uint objectId, int part): the object under the pointer (0x0083DA44), then the found-object notice"),
        new NativeUiEntry("UIElement_SmartBoxWrapper::FindObject sets an item list's hovered item", 0x004E60E4,
            NativeUiCatalogue.Bytes("8B 88 FC 05 00 00 6A FF 51 E8 3E BC F6 FF"), Source, "the retail pattern followed here: set_found_object(item +0x5FC, -1)"),
        new NativeUiEntry("ClientUISystem::UpdateCursorState", UpdateCursorStateAddress, NativeUiCatalogue.Bytes("83 EC 08 53 55 56 57 89 4C 24 10 E8 40 C9 EE FF 85 C0"),
            Source, "ThisCall (): in target mode 3 the cursor is valid (0x28) or invalid (0x29) by ItemHolder::IsTargetCompatibleWithTargetingObject"),
        new NativeUiEntry("ItemHolder::IsTargetCompatibleWithTargetingObject", IsTargetCompatibleAddress, NativeUiCatalogue.Bytes(
            "53 8B 5C 24 08 85 DB 56 57 0F 84 84 00 00 00 A1 E4 1A 87 00"), Source, "Cdecl (uint target) -> bool: whether the held item can be used on the target")
    };

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ExecuteFn(IntPtr uiSystem, uint objectId, int mode);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetModeFn(IntPtr uiSystem, int mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetFoundObjectFn(uint objectId, int part);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UpdateCursorFn(IntPtr uiSystem);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool IsCompatibleFn(uint objectId);

    private readonly ExecuteFn? _execute;
    private readonly SetModeFn? _setMode;
    private readonly SetFoundObjectFn? _setFound;
    private readonly UpdateCursorFn? _updateCursor;
    private readonly IsCompatibleFn? _isCompatible;
    // The item this class last made the found object; zero when it left the found object to retail.
    private uint _hovered;

    public RetailTargetMode(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
        {
            log("Using an item on a target in LegACEy windows is off: the running client does not match its native entries.");
            return;
        }
        _execute = Marshal.GetDelegateForFunctionPointer<ExecuteFn>(new IntPtr(ExecuteTargetModeForItemAddress));
        _setMode = Marshal.GetDelegateForFunctionPointer<SetModeFn>(new IntPtr(SetTargetModeAddress));
        _setFound = Marshal.GetDelegateForFunctionPointer<SetFoundObjectFn>(new IntPtr(SetFoundObjectAddress));
        _updateCursor = Marshal.GetDelegateForFunctionPointer<UpdateCursorFn>(new IntPtr(UpdateCursorStateAddress));
        _isCompatible = Marshal.GetDelegateForFunctionPointer<IsCompatibleFn>(new IntPtr(IsTargetCompatibleAddress));
    }

    /// <summary>
    /// Each frame while a LegACEy item is hovered in target mode: retail re-picks the object under the pointer from the world behind the
    /// window every frame (seen in play: a static object, 80 to 150 times a second), so the hovered item is written back and the cursor
    /// updated. Written directly, without set_found_object's notice, so retail is not prompted to pick again.
    /// </summary>
    public void Tick()
    {
        if (_hovered == 0 || _updateCursor == null) return;
        var ui = Marshal.ReadIntPtr(UiSystemInstance);
        if (ui == IntPtr.Zero) return;
        if (Marshal.ReadInt32(ui, TargetModeOffset) == 0)
        {
            _hovered = 0;
            return;
        }
        var found = unchecked((uint)Marshal.ReadInt32(FoundObject));
        if (found == _hovered) return;
        Marshal.WriteInt32(FoundObject, unchecked((int)_hovered));
        _updateCursor(ui);
    }


    /// <summary>True while retail waits for a target (any target mode).</summary>
    public bool Waiting
    {
        get
        {
            if (_execute == null) return false;
            var ui = Marshal.ReadIntPtr(UiSystemInstance);
            return ui != IntPtr.Zero && Marshal.ReadInt32(ui, TargetModeOffset) != 0;
        }
    }

    /// <summary>
    /// The pointer is over an item in a LegACEy window, or over none with zero. While retail waits for a target, the item becomes the object
    /// under the pointer and the cursor shows whether it is a valid target; zero clears it, as leaving a retail item list does.
    /// </summary>
    public void Hover(uint objectId)
    {
        if (_setFound == null || _updateCursor == null || objectId == _hovered) return;
        var ui = Marshal.ReadIntPtr(UiSystemInstance);
        if (ui == IntPtr.Zero) return;
        // Outside target mode the found object is retail's own; only a hover this class set is cleared.
        if (objectId != 0 && Marshal.ReadInt32(ui, TargetModeOffset) == 0) return;
        _hovered = objectId;
        _setFound(objectId, -1);
        _updateCursor(ui);
    }

    /// <summary>
    /// While an item waits to be used on a target, a click on an object it cannot be used on (the invalid cursor) ends the wait, so the
    /// cursor turns plain and the click selects nothing: true when that happened.
    /// </summary>
    public bool CancelIfInvalid(uint objectId)
    {
        if (_isCompatible == null || _setMode == null || objectId == 0) return false;
        var ui = Marshal.ReadIntPtr(UiSystemInstance);
        if (ui == IntPtr.Zero || Marshal.ReadInt32(ui, TargetModeOffset) != UseOnTargetMode || _isCompatible(objectId)) return false;
        _setMode(ui, 0);
        Hover(0);
        return true;
    }

    /// <summary>When retail is waiting for a target, makes the object that target, as retail's item lists do; false when it is not waiting.</summary>
    public bool TryTarget(uint objectId)
    {
        if (_execute == null || _setMode == null || objectId == 0) return false;
        var ui = Marshal.ReadIntPtr(UiSystemInstance);
        if (ui == IntPtr.Zero) return false;
        var mode = Marshal.ReadInt32(ui, TargetModeOffset);
        if (mode == 0) return false;
        _execute(ui, objectId, mode);
        _setMode(ui, 0);
        Hover(0);
        return true;
    }
}

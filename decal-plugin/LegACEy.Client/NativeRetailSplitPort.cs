using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's stack split over the installed client: the selection and split amount are read from retail's globals, and the split is made by
/// retail's own ACCWeenieObject::UIAttemptSplitTo3D and UIAttemptSplitToContainer, which send game actions 0x0056 and 0x0055 and record the
/// request, so a refused split says "The X can't be split" as retail's does. When those do not match the running client, or the client does
/// not know the stack, the split goes by the bare senders (CM_Inventory::Event_StackableSplitTo3D and Event_StackableSplitToContainer) and a
/// refusal shows no line. Only built once <see cref="RetailToolbarCatalogue"/> has passed validation, since the senders are called by address.
/// </summary>
internal sealed class NativeRetailSplitPort : IRetailSplitPort
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xE30 in this region) for names; our objdump read of installed acclient.exe";
    private const uint UIAttemptSplitTo3D = 0x0058E680, UIAttemptSplitToContainer = 0x0058E600;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        // Past the five-byte prologue and up to the sender's E8: Decal patches prologues and call sites.
        new NativeUiEntry("ACCWeenieObject::UIAttemptSplitTo3D asks IsPlayerReadyToMakeInventoryRequest, then sends", UIAttemptSplitTo3D + 5,
            NativeUiCatalogue.Bytes("E8 F6 F7 FF FF 83 C4 04 85 C0 74 59 8B 46 08 57 8B 7C 24 0C 57 50 E8"), Source,
            "ThisCall (ACCWeenieObject* stack, int amount), ret 4"),
        new NativeUiEntry("ACCWeenieObject::UIAttemptSplitTo3D records a split request (prevRequest = 2)", UIAttemptSplitTo3D + 0x34,
            NativeUiCatalogue.Bytes("C7 05 D0 1E 87 00 02 00 00 00"), Source, "instruction reference (mov [0x871ED0], 2)"),
        new NativeUiEntry("ACCWeenieObject::UIAttemptSplitToContainer asks IsPlayerReadyToMakeInventoryRequest, then sends", UIAttemptSplitToContainer + 5,
            NativeUiCatalogue.Bytes("E8 76 F8 FF FF 83 C4 04 85 C0 74 64 8B 44 24 0C 8B 4C 24 08 8B 56 08 57 8B 7C 24 14 57 50 51 52 E8"),
            Source, "ThisCall (ACCWeenieObject* stack, uint containerId, int place, int amount), ret 0xC"),
        new NativeUiEntry("ACCWeenieObject::UIAttemptSplitToContainer records a split request (prevRequest = 2)", UIAttemptSplitToContainer + 0x3E,
            NativeUiCatalogue.Bytes("C7 05 D0 1E 87 00 02 00 00 00"), Source, "instruction reference (mov [0x871ED0], 2)"),
        RetailHudCatalogue.WeenieLookup
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SplitTo3DFn(uint stackId, int amount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SplitToContainerFn(uint stackId, uint containerId, int place, int amount);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void AttemptSplitTo3DFn(IntPtr stack, int amount);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void AttemptSplitToContainerFn(IntPtr stack, uint containerId, int place, int amount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetWeenieObjectFn(uint id);

    private static readonly SplitTo3DFn SplitTo3DNative = Marshal.GetDelegateForFunctionPointer<SplitTo3DFn>(
        new IntPtr(RetailToolbarCatalogue.StackableSplitTo3D));
    private static readonly SplitToContainerFn SplitToContainerNative = Marshal.GetDelegateForFunctionPointer<SplitToContainerFn>(
        new IntPtr(RetailToolbarCatalogue.StackableSplitToContainer));
    private static readonly AttemptSplitTo3DFn AttemptSplitTo3D = Marshal.GetDelegateForFunctionPointer<AttemptSplitTo3DFn>(new IntPtr(UIAttemptSplitTo3D));
    private static readonly AttemptSplitToContainerFn AttemptSplitToContainer =
        Marshal.GetDelegateForFunctionPointer<AttemptSplitToContainerFn>(new IntPtr(UIAttemptSplitToContainer));
    private static readonly GetWeenieObjectFn GetWeenieObject = Marshal.GetDelegateForFunctionPointer<GetWeenieObjectFn>(
        new IntPtr(RetailHudCatalogue.GetWeenieObject));

    private readonly bool _retailAttempts;

    public NativeRetailSplitPort(Func<uint, int, byte[]> readMemory, Action<string> log) =>
        _retailAttempts = NativeUiCatalogue.Validate(readMemory, log, Entries, "A refused split shows no \"can't be split\" line: the running client does not match its native entries.");

    public uint SelectedId => NativeSelection.Selected;

    public int SplitAmount => BitConverter.ToInt32(NativeUi.ReadMemory(RetailToolbarCatalogue.SplitAmount, 4), 0);

    public int StackSize => BitConverter.ToInt32(NativeUi.ReadMemory(RetailToolbarCatalogue.SplitStackSize, 4), 0);

    public void SplitTo3D(uint stackId, int amount)
    {
        if (Stack(stackId) is var stack && stack != IntPtr.Zero) AttemptSplitTo3D(stack, amount);
        else SplitTo3DNative(stackId, amount);
    }

    public void SplitToContainer(uint stackId, uint containerId, int place, int amount)
    {
        if (Stack(stackId) is var stack && stack != IntPtr.Zero) AttemptSplitToContainer(stack, containerId, place, amount);
        else SplitToContainerNative(stackId, containerId, place, amount);
    }

    private IntPtr Stack(uint stackId) => _retailAttempts ? GetWeenieObject(stackId) : IntPtr.Zero;
}

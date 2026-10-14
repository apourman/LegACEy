using System;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's stack split over the installed client: the selection and split amount are read from retail's globals, and the split is sent by
/// retail's own senders (CM_Inventory::Event_StackableSplitTo3D and Event_StackableSplitToContainer, game actions 0x0056 and 0x0055). Only
/// built once <see cref="RetailToolbarCatalogue"/> has passed validation, since the senders are called by address.
/// </summary>
internal sealed class NativeRetailSplitPort : IRetailSplitPort
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SplitTo3DFn(uint stackId, int amount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SplitToContainerFn(uint stackId, uint containerId, int place, int amount);

    private static readonly SplitTo3DFn SplitTo3DNative = Marshal.GetDelegateForFunctionPointer<SplitTo3DFn>(
        new IntPtr(RetailToolbarCatalogue.StackableSplitTo3D));
    private static readonly SplitToContainerFn SplitToContainerNative = Marshal.GetDelegateForFunctionPointer<SplitToContainerFn>(
        new IntPtr(RetailToolbarCatalogue.StackableSplitToContainer));

    public uint SelectedId => (uint)Bridge.Fact(BridgeFact.CurrentSelection);

    public int SplitAmount => BitConverter.ToInt32(NativeUi.ReadMemory(RetailToolbarCatalogue.SplitAmount, 4), 0);

    public int StackSize => BitConverter.ToInt32(NativeUi.ReadMemory(RetailToolbarCatalogue.SplitStackSize, 4), 0);

    public void SplitTo3D(uint stackId, int amount) => SplitTo3DNative(stackId, amount);

    public void SplitToContainer(uint stackId, uint containerId, int place, int amount) => SplitToContainerNative(stackId, containerId, place, amount);
}

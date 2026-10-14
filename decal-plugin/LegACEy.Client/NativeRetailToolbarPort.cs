using System;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The toolbar's port over the installed client. The stance is Decal's combat mode. A panel opens or closes through gmPanelUI's notice, by
/// its panel number; the notice needs gmPanelUI, found from the toolbar's element or else the inventory's. <see cref="Poll"/> raises
/// <see cref="Changed"/> when the stance or the inventory's open state moved, once a frame.
/// </summary>
/// <remarks>
/// Gap: only the inventory's open state is read so far. The other panels' elements are not known yet (ticket 01 is finding them), so
/// their rings stay off until then. The notice itself works for every panel number.
/// </remarks>
internal sealed class NativeRetailToolbarPort : IRetailToolbarPort
{
    private readonly Action<string> _log;
    private RetailStance _lastStance;
    private bool _lastInventoryOpen;
    private bool _noManagerLogged;

    public NativeRetailToolbarPort(Action<string> log) => _log = log;

    public event Action? Changed;

    public RetailStance Stance => (RetailStance)Bridge.Fact(BridgeFact.CombatMode);

    public void SetStance(RetailStance stance) => Bridge.SetCombatMode((int)stance);

    public bool IsPanelOpen(RetailPanel panel)
    {
        if (panel != RetailPanel.Inventory) return false;
        var element = NativeUi.GetElement(NativeUi.InventoryPanel);
        return element != IntPtr.Zero && NativeUi.IsVisible(element);
    }

    public void SetPanelOpen(RetailPanel panel, bool open)
    {
        var manager = PanelManager();
        if (manager == IntPtr.Zero)
        {
            if (!_noManagerLogged) _log("Retail toolbar buttons are off: gmPanelUI was not found from the toolbar or the inventory.");
            _noManagerLogged = true;
            return;
        }
        NativeRetailPanelPort.SetPanelVisibility(manager, (uint)panel, open);
    }

    /// <summary>Raises <see cref="Changed"/> when the stance or the inventory's open state moved since the last call.</summary>
    public void Poll()
    {
        var stance = Stance;
        var inventoryOpen = IsPanelOpen(RetailPanel.Inventory);
        if (stance == _lastStance && inventoryOpen == _lastInventoryOpen) return;
        _lastStance = stance;
        _lastInventoryOpen = inventoryOpen;
        Changed?.Invoke();
    }

    private static IntPtr PanelManager()
    {
        var manager = NativeRetailPanelPort.PanelManagerOf(NativeUi.GetElement(NativeUi.RootToolbarField));
        return manager != IntPtr.Zero ? manager : NativeRetailPanelPort.PanelManagerOf(NativeUi.GetElement(NativeUi.InventoryPanel));
    }
}

using System;
using System.Collections.Generic;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The toolbar's port over the installed client. The stance is Decal's combat mode. A panel opens or closes through gmPanelUI's notice, by
/// its panel number; the notice needs gmPanelUI, found from the toolbar's element or else the inventory's. <see cref="Poll"/> raises
/// <see cref="Changed"/> when the stance or the inventory's open state moved, once a frame.
/// </summary>
/// <remarks>
/// The inventory's open state comes from the LegACEy inventory window while its takeover holds retail's panel, and from retail's element
/// otherwise. The other panels' open states are not read yet: the port returns what it last asked for. A close box or a key does not update
/// that until the readers exist, so a window opened and closed that way reads as open here.
/// </remarks>
internal sealed class NativeRetailToolbarPort : IRetailToolbarPort
{
    private readonly Action<string> _log;
    // Supplies the inventory window's open state while a takeover holds the panel; null when none does.
    private readonly Func<bool?> _takenInventoryOpen;
    private readonly Dictionary<RetailPanel, bool> _requested = new();
    private RetailStance _lastStance;
    private bool _lastInventoryOpen;
    private bool _noManagerLogged;

    public NativeRetailToolbarPort(Action<string> log, Func<bool?> takenInventoryOpen)
    {
        _log = log;
        _takenInventoryOpen = takenInventoryOpen;
    }

    public event Action? Changed;

    public RetailStance Stance => (RetailStance)Bridge.Fact(BridgeFact.CombatMode);

    public void SetStance(RetailStance stance) => Bridge.SetCombatMode((int)stance);

    public bool IsPanelOpen(RetailPanel panel)
    {
        if (panel != RetailPanel.Inventory) return _requested.TryGetValue(panel, out var requested) && requested;
        if (_takenInventoryOpen() is bool taken) return taken;
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
        _requested[panel] = open;
        Changed?.Invoke();
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

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The toolbar's port over the installed client. The stance click is retail's combat toggle (ToggleCombatMode), the stance read is Decal's combat mode.
/// A panel opens or closes through gmPanelUI's notice, by its panel number; the notice needs gmPanelUI, found from the toolbar's element or
/// else the inventory's. The selection is Decal's current selection; Use goes through Decal's UseItem and Examine through the appraisal
/// window, never retail's examination panel. <see cref="Poll"/> raises <see cref="Changed"/> when the stance, the inventory's open state or
/// the selection moved, once a frame.
/// </summary>
/// <remarks>
/// The inventory's open state comes from the LegACEy inventory window while its takeover holds retail's panel, and from retail's element
/// otherwise. Another panel's open state comes from gmPanelUI's table when it lists the panel; a panel the table does not list returns what
/// the port last asked for, so a close box or a key can leave that panel's state stale.
/// </remarks>
internal sealed class NativeRetailToolbarPort : IRetailToolbarPort
{
    private readonly Action<string> _log;
    // Supplies the inventory window's open state while a takeover holds the panel; null when none does.
    private readonly Func<bool?> _takenInventoryOpen;
    private readonly SelectionMeters _meters;
    private readonly AppraisalFeature _appraisals;
    // Retail's target mode for a gem or kit in use; null when it is off.
    private readonly RetailTargetMode? _targetMode;
    private readonly Dictionary<RetailPanel, bool> _requested = new();
    private RetailStance _lastStance;
    private bool _lastInventoryOpen;
    private RetailSelection? _lastSelection;
    // The selected object's name, kind and stack count, as read when its id was last seen or it last changed; the meter is applied on top each read.
    private RetailSelection? _described;
    private bool _noManagerLogged;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetCombatSystemFn();

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ToggleCombatModeFn(IntPtr combatSystem);

    private static readonly GetCombatSystemFn GetCombatSystem = Marshal.GetDelegateForFunctionPointer<GetCombatSystemFn>(
        new IntPtr(RetailToolbarCatalogue.GetCombatSystem));
    private static readonly ToggleCombatModeFn ToggleCombatModeNative = Marshal.GetDelegateForFunctionPointer<ToggleCombatModeFn>(
        new IntPtr(RetailToolbarCatalogue.ToggleCombatMode));

    public NativeRetailToolbarPort(Action<string> log, Func<bool?> takenInventoryOpen, SelectionMeters meters, AppraisalFeature appraisals,
        RetailTargetMode? targetMode)
    {
        _log = log;
        _takenInventoryOpen = takenInventoryOpen;
        _meters = meters;
        _appraisals = appraisals;
        _targetMode = targetMode;
    }

    public event Action? Changed;

    public RetailStance Stance => (RetailStance)Bridge.Fact(BridgeFact.CombatMode);

    /// <summary>
    /// The selected object with its meter read from the server's answers; null when nothing is selected. The name, kind and stack count are
    /// read when the selection's id changes, and again when the game reports the object changed.
    /// </summary>
    public RetailSelection? Selection
    {
        get
        {
            var id = (uint)Bridge.Fact(BridgeFact.CurrentSelection);
            if (id == 0)
            {
                _described = null;
                return null;
            }
            if (_described?.Id != id) _described = Bridge.Selection(id);
            return _described switch
            {
                { Kind: SelectionKind.Creature } creature => creature with { Meter = _meters.Health(id) },
                { Kind: SelectionKind.ItemWithMana } item => item with { Meter = _meters.Mana(id) },
                var other => other
            };
        }
    }

    /// <summary>
    /// Retail's split amount: the global the drag reads for the selected stack. A write is clamped to [1, the stack's size] as retail's box
    /// clamps it, and only lands while the stack the plaque shows is still the selection.
    /// </summary>
    public int SplitAmount
    {
        get => ReadInt(RetailToolbarCatalogue.SplitAmount);
        set
        {
            var id = (uint)Bridge.Fact(BridgeFact.CurrentSelection);
            if (_described is not { Kind: SelectionKind.Stack } || _described.Id != id) return;
            Marshal.WriteInt32(new IntPtr(unchecked((int)RetailToolbarCatalogue.SplitAmount)), Math.Clamp(value, 1, Math.Max(1, ReadInt(RetailToolbarCatalogue.SplitStackSize))));
        }
    }

    private static int ReadInt(uint address) => BitConverter.ToInt32(NativeUi.ReadMemory(address, 4), 0);

    /// <summary>
    /// Uses the selection as the hand button does. While retail waits for a target (a gem or kit in use), the selection is that target, as
    /// a click on it in a retail item list makes it; an object the held item cannot be used on cancels the wait instead.
    /// </summary>
    public void UseSelected()
    {
        var id = (uint)Bridge.Fact(BridgeFact.CurrentSelection);
        if (id == 0) return;
        if (_targetMode?.CancelIfInvalid(id) == true) return;
        if (_targetMode?.TryTarget(id) == true) return;
        Bridge.UseItem(id, 0);
    }

    /// <summary>
    /// Examines the selection in the appraisal window, as the examination takeover does when retail's panel shows: a plugin's selected
    /// stored item first, else the game's selection, shown from the cached appraisal or asked for. Retail's examination panel is never opened.
    /// </summary>
    public void ExamineSelected()
    {
        var id = (uint)Bridge.Fact(BridgeFact.CurrentSelection);
        if (_appraisals.ExamineSelection(id) == ExamineResult.Failed) _log("Could not examine the selection: the appraisal window could not show it.");
    }

    /// <summary>The stance button's click: retail's own toggle (peace and the default stance), as retail's button does. No-op with no character.</summary>
    public void ToggleStance()
    {
        var combatSystem = GetCombatSystem();
        if (combatSystem == IntPtr.Zero) return;
        ToggleCombatModeNative(combatSystem);
    }

    public bool IsPanelOpen(RetailPanel panel)
    {
        if (panel != RetailPanel.Inventory)
        {
            var manager = PanelManager();
            var listed = manager == IntPtr.Zero ? IntPtr.Zero : NativeRetailPanelPort.PanelElement(manager, (uint)panel);
            if (listed != IntPtr.Zero) return NativeUi.IsVisible(listed);
            return _requested.TryGetValue(panel, out var requested) && requested;
        }
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

    /// <summary>The game reported an object changed (a stack's count moved, say): a selected object is read again on the next read.</summary>
    public void ObjectChanged(uint id)
    {
        if (_described?.Id == id) _described = null;
    }

    /// <summary>Raises <see cref="Changed"/> when the stance, the inventory's open state or the selection moved since the last call.</summary>
    public void Poll()
    {
        var stance = Stance;
        var inventoryOpen = IsPanelOpen(RetailPanel.Inventory);
        var selection = Selection;
        // A stack that shrank takes the split amount down with it, as retail's own box clamps it.
        if (selection is { Kind: SelectionKind.Stack } && SplitAmount > ReadInt(RetailToolbarCatalogue.SplitStackSize)) SplitAmount = ReadInt(RetailToolbarCatalogue.SplitStackSize);
        if (stance == _lastStance && inventoryOpen == _lastInventoryOpen && selection == _lastSelection) return;
        _lastStance = stance;
        _lastInventoryOpen = inventoryOpen;
        _lastSelection = selection;
        Changed?.Invoke();
    }

    private static IntPtr PanelManager()
    {
        var manager = NativeRetailPanelPort.PanelManagerOf(NativeUi.GetElement(NativeUi.RootToolbarField));
        return manager != IntPtr.Zero ? manager : NativeRetailPanelPort.PanelManagerOf(NativeUi.GetElement(NativeUi.InventoryPanel));
    }
}

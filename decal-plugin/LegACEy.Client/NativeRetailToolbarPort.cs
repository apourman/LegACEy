using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The toolbar's port over the installed client. The stance click is retail's combat toggle (ToggleCombatMode), the stance read is Decal's combat mode.
/// A panel opens or closes through gmPanelUI's notice, by its panel number; the notice needs gmPanelUI, found from the toolbar's element or
/// else the inventory's. The selection is Decal's current selection; Use goes through Decal's UseItem and Examine through the appraisal
/// window, never retail's examination panel. <see cref="Poll"/> raises <see cref="Changed"/> when the stance, the inventory's open state,
/// the selection or the shortcut list moved, once a frame.
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
    // True only in game: the shortcut chain belongs to the character, so it is not read at login, portal space or after logoff.
    private readonly Func<bool> _inGame;
    private readonly SelectionMeters _meters;
    private readonly AppraisalFeature _appraisals;
    // Retail's target mode for a gem or kit in use; null when it is off.
    private readonly RetailTargetMode? _targetMode;
    private readonly Dictionary<RetailPanel, bool> _requested = new();
    // Poll reads into the buffer and compares in place, so a frame allocates nothing when the list has not moved.
    private readonly RetailShortcut[] _shortcutBuffer = new RetailShortcut[RetailToolbarCatalogue.ShortcutSlots];
    private readonly RetailShortcut[] _lastShortcuts = new RetailShortcut[RetailToolbarCatalogue.ShortcutSlots];
    private int _lastShortcutCount;
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

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UseShortcutFn(IntPtr toolbar, int slot, bool fromKey);

    private static readonly GetCombatSystemFn GetCombatSystem = Marshal.GetDelegateForFunctionPointer<GetCombatSystemFn>(
        new IntPtr(RetailToolbarCatalogue.GetCombatSystem));
    private static readonly ToggleCombatModeFn ToggleCombatModeNative = Marshal.GetDelegateForFunctionPointer<ToggleCombatModeFn>(
        new IntPtr(RetailToolbarCatalogue.ToggleCombatMode));
    private static readonly UseShortcutFn UseShortcutNative = Marshal.GetDelegateForFunctionPointer<UseShortcutFn>(
        new IntPtr(RetailToolbarCatalogue.UseShortcut));

    public NativeRetailToolbarPort(Action<string> log, Func<bool?> takenInventoryOpen, Func<bool> inGame, SelectionMeters meters,
        AppraisalFeature appraisals, RetailTargetMode? targetMode)
    {
        _log = log;
        _takenInventoryOpen = takenInventoryOpen;
        _inGame = inGame;
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

    /// <summary>retail's shortcut list, read from the ShortCutManager: each filled slot's index and object id. Empty outside the game.</summary>
    public IReadOnlyList<RetailShortcut> Shortcuts
    {
        get
        {
            var buffer = new RetailShortcut[RetailToolbarCatalogue.ShortcutSlots];
            return buffer.AsSpan(0, ReadShortcuts(buffer)).ToArray();
        }
    }

    /// <summary>
    /// Uses a shortcut through retail's toolbar with fromKey true. Retail's notice that uses a shortcut (RecvNotice_UseShortcut) passes the
    /// same true, as the number key does; the sender of that notice on a slot click is not traced. Nothing is used when the toolbar is not retail's.
    /// </summary>
    public void UseShortcut(int index)
    {
        var toolbar = NativeUi.GetElement(NativeUi.RootToolbarField);
        if (toolbar == IntPtr.Zero || Marshal.ReadIntPtr(toolbar) != new IntPtr(RetailToolbarCatalogue.GmToolbarUIVtable)) return;
        UseShortcutNative(toolbar, index, true);
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

    /// <summary>Raises <see cref="Changed"/> when the stance, the inventory's open state, the selection or the shortcut list moved since the last call.</summary>
    public void Poll()
    {
        var stance = Stance;
        var inventoryOpen = IsPanelOpen(RetailPanel.Inventory);
        var selection = Selection;
        // A stack that shrank takes the split amount down with it, as retail's own box clamps it.
        if (selection is { Kind: SelectionKind.Stack } && SplitAmount > ReadInt(RetailToolbarCatalogue.SplitStackSize)) SplitAmount = ReadInt(RetailToolbarCatalogue.SplitStackSize);
        var count = ReadShortcuts(_shortcutBuffer);
        if (stance == _lastStance && inventoryOpen == _lastInventoryOpen && selection == _lastSelection
            && _shortcutBuffer.AsSpan(0, count).SequenceEqual(_lastShortcuts.AsSpan(0, _lastShortcutCount))) return;
        _lastStance = stance;
        _lastInventoryOpen = inventoryOpen;
        _lastSelection = selection;
        _shortcutBuffer.AsSpan(0, count).CopyTo(_lastShortcuts);
        _lastShortcutCount = count;
    }

    /// <summary>
    /// Reads the shortcut list into <paramref name="buffer"/> and returns how many slots are filled. CPlayerSystem's PlayerModule sits at +0x34
    /// and holds the ShortCutManager at its +0x04; the manager's 18 slot pointers start at +4, and each filled slot's entry holds the object id
    /// at +8. A zero pointer is an empty slot, and a zero hop before it returns nothing. Row 1 is slots 0 to 8. Outside the game, nothing is read.
    /// </summary>
    private int ReadShortcuts(RetailShortcut[] buffer)
    {
        if (!_inGame()) return 0;
        var system = Marshal.ReadIntPtr(new IntPtr(RetailToolbarCatalogue.PlayerSystem));
        if (system == IntPtr.Zero) return 0;
        var manager = Marshal.ReadIntPtr(system, RetailToolbarCatalogue.PlayerModuleOffset + 4);
        if (manager == IntPtr.Zero) return 0;
        var count = 0;
        for (var slot = 0; slot < RetailToolbarCatalogue.ShortcutSlots; slot++)
        {
            var entry = Marshal.ReadIntPtr(manager, 4 + 4 * slot);
            if (entry != IntPtr.Zero) buffer[count++] = new RetailShortcut(slot, unchecked((uint)Marshal.ReadInt32(entry, 8)));
        }
        return count;
    }

    private static IntPtr PanelManager()
    {
        var manager = NativeRetailPanelPort.PanelManagerOf(NativeUi.GetElement(NativeUi.RootToolbarField));
        return manager != IntPtr.Zero ? manager : NativeRetailPanelPort.PanelManagerOf(NativeUi.GetElement(NativeUi.InventoryPanel));
    }
}

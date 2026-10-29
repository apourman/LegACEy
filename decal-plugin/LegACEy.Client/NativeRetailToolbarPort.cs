using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The toolbar's port over the installed client. The stance click is retail's combat toggle (ToggleCombatMode), the stance read is Decal's combat mode.
/// A panel opens or closes through retail's own broadcast (CM_UI::SendNotice_SetPanelVisibility), by its panel number, as retail's buttons do. The selection is Decal's current selection; Use goes through Decal's UseItem and Examine through the appraisal
/// window, never retail's examination panel. <see cref="Poll"/> raises <see cref="Changed"/> when the stance, any button's panel open state,
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
    private readonly RetailToolbarSnapshot _snapshot = new();
    // The panels the toolbar's buttons open and show, in any order: Poll reads each one's open state every frame.
    private static readonly RetailPanel[] ButtonPanels =
    {
        RetailPanel.Options, RetailPanel.Skills, RetailPanel.Social, RetailPanel.Magic, RetailPanel.WorldMap, RetailPanel.Quests, RetailPanel.Inventory
    };
    // The selected object's name, kind and stack count, as read when its id was last seen or it last changed (null when the world had no such
    // object, which is not read again until the id changes). The meter is applied on top, not stored here.
    private RetailSelection? _described;
    // The selected id last seen, and whether the game reported the selected object changed since it was read.
    private uint _selectedId;
    private bool _rereadSelected;
    // The amount the player chose in the split tab and the stack it is for, while it stays selected; any other stack splits whole.
    private uint _chosenSplitStackId;
    private int _chosenSplitAmount;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetCombatSystemFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendNoticeSetPanelVisibilityFn(uint panelId, byte visible);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ToggleCombatModeFn(IntPtr combatSystem);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UseShortcutFn(IntPtr toolbar, int slot, bool fromKey);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void AddShortcutFn(IntPtr toolbar, uint objectId, int slot, bool sendToServer);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RemoveShortcutInSlotNumFn(IntPtr toolbar, int slot, bool sendToServer);

    private static readonly GetCombatSystemFn GetCombatSystem = Marshal.GetDelegateForFunctionPointer<GetCombatSystemFn>(
        new IntPtr(RetailToolbarCatalogue.GetCombatSystem));
    private static readonly SendNoticeSetPanelVisibilityFn SendNoticeSetPanelVisibility = Marshal.GetDelegateForFunctionPointer<SendNoticeSetPanelVisibilityFn>(
        new IntPtr(RetailToolbarCatalogue.SendNoticeSetPanelVisibility));
    private static readonly ToggleCombatModeFn ToggleCombatModeNative = Marshal.GetDelegateForFunctionPointer<ToggleCombatModeFn>(
        new IntPtr(RetailToolbarCatalogue.ToggleCombatMode));
    private static readonly UseShortcutFn UseShortcutNative = Marshal.GetDelegateForFunctionPointer<UseShortcutFn>(
        new IntPtr(RetailToolbarCatalogue.UseShortcut));
    private static readonly AddShortcutFn AddShortcutNative = Marshal.GetDelegateForFunctionPointer<AddShortcutFn>(
        new IntPtr(RetailToolbarCatalogue.AddShortcut));
    private static readonly RemoveShortcutInSlotNumFn RemoveShortcutInSlotNumNative = Marshal.GetDelegateForFunctionPointer<RemoveShortcutInSlotNumFn>(
        new IntPtr(RetailToolbarCatalogue.RemoveShortcutInSlotNum));

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
            if (Described() is not { } described) return null;
            return described with { Kind = KindOf(described), Meter = MeterOf(described) };
        }
    }

    /// <summary>
    /// The selected object as read, without its meter. Read again only when the selected id changes or the game reports the object changed;
    /// a selection the world has no object for stays null until then. Meters are dropped with the selection that asked for them.
    /// </summary>
    private RetailSelection? Described()
    {
        var id = (uint)Bridge.Fact(BridgeFact.CurrentSelection);
        if (id == _selectedId && !_rereadSelected) return _described;
        if (id != _selectedId) _meters.KeepOnly(id);
        _selectedId = id;
        _rereadSelected = false;
        _described = id == 0 ? null : Bridge.Selection(id);
        return _described;
    }

    /// <summary>
    /// The kind the plaque shows. Decal's maximum mana fills in only after an appraisal, so an item the server gave a mana answer is an item
    /// with mana too. A stack keeps its kind (its split box), and a creature keeps its health meter, whatever answer came.
    /// </summary>
    private SelectionKind KindOf(RetailSelection described) =>
        described.Kind == SelectionKind.Other && _meters.Mana(described.Id) != null ? SelectionKind.ItemWithMana : described.Kind;

    private float? MeterOf(RetailSelection described) => KindOf(described) switch
    {
        SelectionKind.Creature => _meters.Health(described.Id),
        SelectionKind.ItemWithMana => _meters.Mana(described.Id),
        _ => null
    };

    /// <summary>
    /// The selected stack's split amount: the whole stack, unless the player chose an amount for this stack, held within [1, the stack's size].
    /// Retail sets its own amount back to 1 on a selection change, which would split one off by default; <see cref="Poll"/> writes this
    /// amount over retail's global each frame, so retail's drag and LegACEy's both split by it. A write only lands while the stack the tab
    /// shows is still the selection.
    /// </summary>
    public int SplitAmount
    {
        get => Described() is { Kind: SelectionKind.Stack } stack ? SplitAmountFor(stack) : ReadInt(RetailToolbarCatalogue.SplitAmount);
        set
        {
            var id = (uint)Bridge.Fact(BridgeFact.CurrentSelection);
            if (Described() is not { Kind: SelectionKind.Stack } stack || stack.Id != id) return;
            (_chosenSplitStackId, _chosenSplitAmount) = (stack.Id, value);
            WriteSplitAmount(SplitAmountFor(stack));
        }
    }

    private int SplitAmountFor(RetailSelection stack) =>
        stack.Id == _chosenSplitStackId ? Math.Clamp(_chosenSplitAmount, 1, Math.Max(1, stack.StackCount)) : Math.Max(1, stack.StackCount);

    private static void WriteSplitAmount(int amount) => Marshal.WriteInt32(new IntPtr(unchecked((int)RetailToolbarCatalogue.SplitAmount)), amount);

    /// <summary>A global int32 of retail's, read in place. Its address is checked by the catalogue before the takeover is on.</summary>
    private static int ReadInt(uint address) => Marshal.ReadInt32(new IntPtr(unchecked((int)address)));

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
            var listed = NativeUi.GetElement(PanelElementId(panel));
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
        var toolbar = RetailToolbarElement();
        if (toolbar != IntPtr.Zero) UseShortcutNative(toolbar, index, true);
    }

    public void Select(uint objectId) => Bridge.SelectItem(objectId);

    /// <summary>
    /// Puts the object into the slot through retail's add, which sends AddShortCut to the server and updates the list. The caller empties
    /// the slot first, as retail's drop does.
    /// </summary>
    public void AddShortcut(int index, uint objectId)
    {
        var toolbar = _inGame() ? RetailToolbarElement() : IntPtr.Zero;
        if (toolbar != IntPtr.Zero) AddShortcutNative(toolbar, objectId, index, true);
    }

    /// <summary>Takes the shortcut out of the slot through retail's remove, which sends RemoveShortCut to the server. Nothing outside the game.</summary>
    public void RemoveShortcut(int index)
    {
        var toolbar = _inGame() ? RetailToolbarElement() : IntPtr.Zero;
        if (toolbar != IntPtr.Zero) RemoveShortcutInSlotNumNative(toolbar, index, true);
    }

    public void SetPanelOpen(RetailPanel panel, bool open)
    {
        SendNoticeSetPanelVisibility((uint)panel, open ? (byte)1 : (byte)0);
        _requested[panel] = open;
        Changed?.Invoke();
    }

    /// <summary>The game reported an object changed (a stack's count moved, say): a selected object is read again on the next read.</summary>
    public void ObjectChanged(uint id)
    {
        if (id != 0 && id == _selectedId) _rereadSelected = true;
    }

    /// <summary>Raises <see cref="Changed"/> when the stance, any window panel's open state, the selection or the shortcut list moved since the last call.</summary>
    public void Poll()
    {
        var described = Described();
        // A chosen amount lasts while its stack stays selected; selected again later, the stack splits whole.
        if (described?.Id != _chosenSplitStackId) _chosenSplitStackId = 0;
        // Retail's global holds the selected stack's amount (see SplitAmount), whatever retail last wrote to it; a stack that shrank takes the
        // amount down with it.
        if (described is { Kind: SelectionKind.Stack } stack && ReadInt(RetailToolbarCatalogue.SplitAmount) != SplitAmountFor(stack))
        {
            WriteSplitAmount(SplitAmountFor(stack));
        }
        var count = ReadShortcuts(_shortcutBuffer);
        var kind = described is null ? SelectionKind.Other : KindOf(described);
        var meter = described is null ? null : MeterOf(described);
        if (_snapshot.Update(Stance, OpenPanels(), described, kind, meter, _shortcutBuffer.AsSpan(0, count))) Changed?.Invoke();
    }

    /// <summary>The window panels the buttons show, as a bit each at its panel number: a set bit is an open panel.</summary>
    private int OpenPanels()
    {
        var open = 0;
        foreach (var panel in ButtonPanels)
            if (IsPanelOpen(panel)) open |= 1 << (int)panel;
        return open;
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

    /// <summary>retail's gmToolbarUI element, when the element at the toolbar's id is that class; zero otherwise.</summary>
    private static IntPtr RetailToolbarElement()
    {
        var toolbar = NativeUi.GetElement(NativeUi.Toolbar);
        return toolbar != IntPtr.Zero && Marshal.ReadIntPtr(toolbar) == new IntPtr(RetailToolbarCatalogue.GmToolbarUIVtable) ? toolbar : IntPtr.Zero;
    }

    /// <summary>
    /// The element a panel shows in, by the id it is placed with under PanelPages (0x10000180) in LayoutDesc 0x2100006E, whose
    /// property 0x10000029 is the panel number (installed client_local_English.dat).
    /// </summary>
    private static uint PanelElementId(RetailPanel panel) => panel switch
    {
        RetailPanel.WorldMap => NativeUi.MapPanel,
        RetailPanel.Options => 0x1000018D,
        RetailPanel.Skills => NativeUi.CharacterPanel,
        RetailPanel.Social => NativeUi.SocialPanel,
        RetailPanel.Magic => 0x10000190,
        RetailPanel.Quests => NativeUi.QuestsPanel,
        _ => NativeUi.InventoryPanel
    };
}

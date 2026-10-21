using System;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The attack bar's port over the installed client. The stance is Decal's combat mode. The set point, the attack height and the charge are
/// ClientCombatSystem's, and the three options are retail's PlayerModule accessors, so retail's keys and notices stay in step with the bar.
/// It is built only once every list in <see cref="RetailAttackCatalogue"/> has passed. <see cref="Poll"/> raises <see cref="Changed"/> when a
/// value moved, and is called once a frame on the game thread.
/// </summary>
internal sealed class NativeAttackBarPort : IAttackBarPort
{
    // ClientCombatSystem's fields: the charge is the float at +0x38, the attack height the int at +0x24, the set point the float at +0x4C.
    private const int ChargeOffset = 0x38;
    private const int HeightOffset = 0x24;
    private const int SetPointOffset = 0x4C;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetCombatSystemFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendDesiredAttackPowerChangedFn(float power);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendAttackHeightChangedFn(int height);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetRequestedAttackHeightFn(IntPtr combatSystem, int height);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte GetOptionFn(IntPtr playerModule);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetOptionFn(IntPtr playerModule, byte value);

    private static readonly GetCombatSystemFn GetCombatSystem = Marshal.GetDelegateForFunctionPointer<GetCombatSystemFn>(
        new IntPtr(RetailToolbarCatalogue.GetCombatSystem));
    private static readonly SendDesiredAttackPowerChangedFn SendDesiredAttackPower = Marshal.GetDelegateForFunctionPointer<SendDesiredAttackPowerChangedFn>(
        new IntPtr(RetailAttackCatalogue.SendDesiredAttackPowerChanged));
    private static readonly SendAttackHeightChangedFn SendAttackHeight = Marshal.GetDelegateForFunctionPointer<SendAttackHeightChangedFn>(
        new IntPtr(RetailAttackCatalogue.SendAttackHeightChanged));
    private static readonly SetRequestedAttackHeightFn SetRequestedAttackHeight = Marshal.GetDelegateForFunctionPointer<SetRequestedAttackHeightFn>(
        new IntPtr(RetailAttackCatalogue.SetRequestedAttackHeight));
    private static readonly OptionNatives AutoRepeatOption = new(RetailAttackCatalogue.AutoRepeatAttack, RetailAttackCatalogue.SetAutoRepeatAttack);
    private static readonly OptionNatives AutoTargetOption = new(RetailAttackCatalogue.AutoTarget, RetailAttackCatalogue.SetAutoTarget);
    private static readonly OptionNatives KeepInViewOption = new(RetailAttackCatalogue.ViewCombatTarget, RetailAttackCatalogue.SetViewCombatTarget);

    private readonly bool _lockAvailable;
    private Values? _last;

    /// <param name="lockAvailable">Whether retail's Lock UI natives matched; the window does not move while the UI is locked.</param>
    public NativeAttackBarPort(bool lockAvailable) => _lockAvailable = lockAvailable;

    public event EventHandler? Changed;

    public RetailStance Stance => (RetailStance)Bridge.Fact(BridgeFact.CombatMode);

    public double SetPoint
    {
        get => ReadUnit(SetPointOffset);
        set
        {
            if (CombatSystem() is not { } system) return;
            // Retail's order: the field, then the notice that tells the combat UI the set point moved.
            var power = (float)ClampUnit(value);
            Marshal.WriteInt32(system, SetPointOffset, BitConverter.SingleToInt32Bits(power));
            SendDesiredAttackPower(power);
        }
    }

    public AttackHeight Height
    {
        get => CombatSystem() is { } system ? HeightOf(Marshal.ReadInt32(system, HeightOffset)) : AttackHeight.High;
        set
        {
            if (CombatSystem() is not { } system) return;
            // Retail's order: the notice first, then the setter that stores the height.
            var native = NativeHeight(value);
            SendAttackHeight(native);
            SetRequestedAttackHeight(system, native);
        }
    }

    public double Charge => ReadUnit(ChargeOffset);

    public bool AutoRepeatAttack { get => ReadOption(AutoRepeatOption); set => WriteOption(AutoRepeatOption, value); }

    public bool AutoTarget { get => ReadOption(AutoTargetOption); set => WriteOption(AutoTargetOption, value); }

    public bool ViewCombatTarget { get => ReadOption(KeepInViewOption); set => WriteOption(KeepInViewOption, value); }

    public bool UiLocked => _lockAvailable && NativeUi.IsUiLocked;

    /// <summary>
    /// Raises <see cref="Changed"/> when the stance or a value the bar draws moved since the last call. The lock is not among them: the bar
    /// draws nothing for it and reads it when pressed.
    /// </summary>
    public void Poll()
    {
        var now = new Values(Stance, SetPoint, Height, Charge, AutoRepeatAttack, AutoTarget, ViewCombatTarget);
        if (now == _last) return;
        _last = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A value clamped to 0 to 1, as retail's power bar clamps the set point. NaN reads as 0.</summary>
    internal static double ClampUnit(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    /// <summary>Retail's number for an attack height: 1 High, 2 Medium, 3 Low.</summary>
    internal static int NativeHeight(AttackHeight height) => (int)height + 1;

    /// <summary>The attack height for retail's number. Any value other than 2 or 3 reads as High.</summary>
    internal static AttackHeight HeightOf(int native) => native switch
    {
        2 => AttackHeight.Medium,
        3 => AttackHeight.Low,
        _ => AttackHeight.High
    };

    private double ReadUnit(int offset) =>
        CombatSystem() is { } system ? ClampUnit(BitConverter.Int32BitsToSingle(Marshal.ReadInt32(system, offset))) : 0;

    private bool ReadOption(OptionNatives option) => PlayerModule() is { } module && option.Read(module);

    private void WriteOption(OptionNatives option, bool value)
    {
        if (PlayerModule() is { } module) option.Write(module, value);
    }

    /// <summary>ClientCombatSystem, or null when there is no character.</summary>
    private static IntPtr? CombatSystem()
    {
        var system = GetCombatSystem();
        return system == IntPtr.Zero ? null : system;
    }

    /// <summary>PlayerModule, at CPlayerSystem+0x34, or null outside the game.</summary>
    private static IntPtr? PlayerModule()
    {
        var system = Marshal.ReadIntPtr(new IntPtr(RetailToolbarCatalogue.PlayerSystem));
        return system == IntPtr.Zero ? null : system + RetailToolbarCatalogue.PlayerModuleOffset;
    }

    private readonly record struct Values(RetailStance Stance, double SetPoint, AttackHeight Height, double Charge, bool AutoRepeatAttack,
        bool AutoTarget, bool ViewCombatTarget);

    /// <summary>One option's retail accessors: the getter and setter on PlayerModule, which set the bit and send the change.</summary>
    private sealed class OptionNatives
    {
        private readonly GetOptionFn _get;
        private readonly SetOptionFn _set;

        public OptionNatives(uint getter, uint setter)
        {
            _get = Marshal.GetDelegateForFunctionPointer<GetOptionFn>(new IntPtr(getter));
            _set = Marshal.GetDelegateForFunctionPointer<SetOptionFn>(new IntPtr(setter));
        }

        public bool Read(IntPtr playerModule) => _get(playerModule) != 0;

        public void Write(IntPtr playerModule, bool value) => _set(playerModule, value ? (byte)1 : (byte)0);
    }
}

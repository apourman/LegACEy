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
    // Each option's retail getter and setter on PlayerModule: the setter sets the bit and sends the change.
    private static readonly (GetOptionFn Get, SetOptionFn Set) AutoRepeatOption = Option(RetailAttackCatalogue.AutoRepeatAttack, RetailAttackCatalogue.SetAutoRepeatAttack);
    private static readonly (GetOptionFn Get, SetOptionFn Set) AutoTargetOption = Option(RetailAttackCatalogue.AutoTarget, RetailAttackCatalogue.SetAutoTarget);
    private static readonly (GetOptionFn Get, SetOptionFn Set) KeepInViewOption = Option(RetailAttackCatalogue.ViewCombatTarget, RetailAttackCatalogue.SetViewCombatTarget);

    private readonly bool _lockAvailable;
    private readonly Func<bool> _reckless;
    private readonly Action<string> _log;
    private Values? _last;
    // ponytail: diagnostic for backlog 116 (the bar not filling); remove it with LogBuild once the log has shown the cause. Retail's build
    // flag and power bar mode last logged, the peak charge read since, and how many lines are left this session (shared by every port).
    private (byte Building, int Mode)? _lastBuild;
    private double _peak;
    private static int _buildLines = 60;

    /// <param name="lockAvailable">Whether retail's Lock UI natives matched; the window does not move while the UI is locked.</param>
    /// <param name="reckless">Whether the character has Recklessness trained or specialized, from the server's description of the character.</param>
    /// <param name="log">Writes the plugin log.</param>
    public NativeAttackBarPort(bool lockAvailable, Func<bool> reckless, Action<string> log)
    {
        _lockAvailable = lockAvailable;
        _reckless = reckless ?? throw new ArgumentNullException(nameof(reckless));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

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

    public bool Reckless => _reckless();

    public bool UiLocked => _lockAvailable && NativeUi.IsUiLocked;

    /// <summary>
    /// Raises <see cref="Changed"/> when the stance or a value the bar draws moved since the last call. The lock is not among them: the bar
    /// draws nothing for it and reads it when pressed.
    /// </summary>
    public void Poll()
    {
        var now = new Values(Stance, SetPoint, Height, Charge, AutoRepeatAttack, AutoTarget, ViewCombatTarget, Reckless);
        LogBuild(now.Charge);
        if (now == _last) return;
        _last = now;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Diagnostic for backlog 116: logs retail's build flag (+0x30), power bar mode (+0x34), the charge (+0x38), the target (+0x40) and the
    /// set point (+0x4C) each time the build flag or the mode changes, with the peak charge read since the last line.
    /// </summary>
    private void LogBuild(double charge)
    {
        if (_buildLines == 0 || CombatSystem() is not { } system) return;
        _peak = Math.Max(_peak, charge);
        var build = (Marshal.ReadByte(system, 0x30), Marshal.ReadInt32(system, 0x34));
        if (build == _lastBuild) return;
        _lastBuild = build;
        _buildLines--;
        var target = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(system, 0x40));
        _log($"Attack bar charge: building={build.Item1} mode={build.Item2} charge={charge:0.000} peak={_peak:0.000} target={target:0.000} " +
            $"set={SetPoint:0.000} stance={Stance} listened={Changed != null}");
        _peak = 0;
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

    private static (GetOptionFn, SetOptionFn) Option(uint getter, uint setter) =>
        (Marshal.GetDelegateForFunctionPointer<GetOptionFn>(new IntPtr(getter)), Marshal.GetDelegateForFunctionPointer<SetOptionFn>(new IntPtr(setter)));

    private static bool ReadOption((GetOptionFn Get, SetOptionFn Set) option) => PlayerModule() is { } module && option.Get(module) != 0;

    private static void WriteOption((GetOptionFn Get, SetOptionFn Set) option, bool value)
    {
        if (PlayerModule() is { } module) option.Set(module, value ? (byte)1 : (byte)0);
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
        bool AutoTarget, bool ViewCombatTarget, bool Reckless);
}

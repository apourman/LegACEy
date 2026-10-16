using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The selected object glows: its parts' luminosity is raised through retail's own <c>CPhysicsObj::SetLuminosity</c>, and the old
/// selection gets back the luminosity it had. Client-side render state only. Call <see cref="Show"/> each frame on the game thread.
/// </summary>
internal sealed class SelectionGlow
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0xAD0 in this region) for names; our objdump read of installed acclient.exe";
    private const uint GetObjectAddress = 0x0050FF60, SetLuminosityAddress = 0x00512360;
    // Self-lighting added to the selected object's parts (0 = lit by the world only, 1 = full bright). Tune in game.
    private const float Glow = 0.5f;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        // Ends before the call into CObjectMaint, in case a hook patches it.
        new NativeUiEntry("CPhysicsObj::GetObjectA", GetObjectAddress, NativeUiCatalogue.Bytes("8B 0D 64 4D 84 00 85 C9 74 0B 8B 44 24 04 50"),
            Source, "Cdecl (object id): CObjectMaint lookup, null when the object is not in the world"),
        new NativeUiEntry("CPhysicsObj::SetLuminosity", SetLuminosityAddress, NativeUiCatalogue.Bytes("D9 05 BC 78 7C 00 56 DC 5C 24 10 8B F1 DF E0 F6 C4 41 75 15 8B 4E 10 85 C9 74 76"),
            Source, "ThisCall (float start, float end, double seconds): no time sets every part now; part array at +0x10"),
        new NativeUiEntry("CPartArray::SetLuminosityInternal", 0x00518EE0, NativeUiCatalogue.Bytes("57 8B F9 8B 47 54 85 C0 74 30 8B 47 58"),
            Source, "the layout read here: setup +0x54, part count +0x58, parts +0x5C"),
        new NativeUiEntry("CPhysicsPart::SetLuminosity", 0x0050F0C0, NativeUiCatalogue.Bytes("D9 44 24 04 56 8B F1 D8 9E D4 00 00 00"),
            Source, "the layout read here: a part's current luminosity at +0xD4")
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetObjectFn(uint objectId);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetLuminosityFn(IntPtr physicsObject, float start, float end, double seconds);

    private readonly GetObjectFn? _getObject;
    private readonly SetLuminosityFn? _setLuminosity;
    // The glowing object and the luminosity it had before; zero when nothing glows.
    private uint _glowing;
    private float _original;

    public SelectionGlow(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        if (!NativeUiCatalogue.Validate(readMemory, log, Entries))
        {
            log("The selection glow is off: the running client does not match its native entries.");
            return;
        }
        _getObject = Marshal.GetDelegateForFunctionPointer<GetObjectFn>(new IntPtr(GetObjectAddress));
        _setLuminosity = Marshal.GetDelegateForFunctionPointer<SetLuminosityFn>(new IntPtr(SetLuminosityAddress));
    }

    /// <summary>Makes <paramref name="selected"/> glow, and gives the previous object its luminosity back. Zero glows nothing.</summary>
    public void Show(uint selected)
    {
        if (_getObject == null || _setLuminosity == null) return;
        if (selected != _glowing)
        {
            var old = _glowing == 0 ? IntPtr.Zero : _getObject(_glowing);
            if (old != IntPtr.Zero) Set(old, _original);
            var target = selected == 0 ? IntPtr.Zero : _getObject(selected);
            var original = target == IntPtr.Zero ? null : PartLuminosity(target);
            _glowing = original == null ? 0 : selected;
            _original = original ?? 0;
        }
        if (_glowing == 0) return;
        // Checked each frame: an appearance change rebuilds the parts at their own luminosity, and a despawn leaves nothing to light.
        // ponytail: one luminosity for the whole object, read from part 0; per-part originals if a multi-part object's parts differ.
        var current = _getObject(_glowing);
        if (current == IntPtr.Zero) { _glowing = 0; return; }
        var glow = Math.Max(_original, Glow);
        if (PartLuminosity(current) != glow) Set(current, glow);
    }

    private void Set(IntPtr physicsObject, float luminosity) => _setLuminosity!(physicsObject, luminosity, luminosity, 0);

    /// <summary>Part 0's current luminosity, or null when the object has no parts.</summary>
    private static float? PartLuminosity(IntPtr physicsObject)
    {
        var parts = Marshal.ReadIntPtr(physicsObject, 0x10);
        if (parts == IntPtr.Zero || Marshal.ReadIntPtr(parts, 0x54) == IntPtr.Zero || Marshal.ReadInt32(parts, 0x58) == 0) return null;
        var part = Marshal.ReadIntPtr(Marshal.ReadIntPtr(parts, 0x5C));
        if (part == IntPtr.Zero) return null;
        return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(part, 0xD4));
    }
}

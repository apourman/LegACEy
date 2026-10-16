using System;
using System.Collections.Generic;
using System.Linq;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The native entries the Dereth vitals window and radar use: retail's vitals windows and radar (takeover), the player's place and heading,
/// the objects in the world with their radar colour and behaviour, and retail's UI lock as its radar's lock button sets it. Each feature has
/// its own list, checked on its own, because Decal patches call sites and prologues at runtime: a mismatch costs that feature only. Every
/// address and byte list was read from the installed end-of-retail acclient.exe; acclient.pdb (a near build) gave the names. The recipes are
/// in the HUD spike note.
/// </summary>
/// <remarks>
/// Every check starts past a function's first instruction, which a hook rewrites, or inside a function at an instruction that reads or calls
/// what the port uses. The functions the port calls are called at their entries, hooked or not, as retail calls them.
/// The root ids are not byte-checked (an id is not an address): the &lt;VITS&gt;, &lt;SVIT&gt; and &lt;RADA&gt; pushes pin them in the running exe,
/// and at runtime <see cref="RetailHeldWindows.RootPort"/> hides or shows only an element UIElementManager::GetElement finds, as for chat's roots.
/// </remarks>
internal static class RetailHudCatalogue
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb";

    /// <summary>Retail's stacked vitals (tag &lt;VITS&gt;, gmFloatyVitalsUI 0x1000004D), placed in LayoutDesc 0x21000005 from 0x2100006C/0x100005F9.</summary>
    internal const uint StackedVitals = 0x100005FA;
    /// <summary>Retail's side-by-side vitals (tag &lt;SVIT&gt;, gmFloatySideVitalsUI 0x10000056), placed from 0x21000075/0x100006D4. Retail
    /// shows one of the two, by its "side by side vitals" option.</summary>
    internal const uint SideVitals = 0x100006D5;
    /// <summary>Retail's radar (tag &lt;RADA&gt;, gmRadarUI 0x10000010), placed from 0x21000074/0x100006D3.</summary>
    internal const uint Radar = 0x100006D2;

    /// <summary>The ids the vitals takeover holds, one of which retail shows: <c>new RetailHeldWindows(VitalsWindows, oneShows: true)</c>.</summary>
    internal static readonly IReadOnlyList<uint> VitalsWindows = new[] { StackedVitals, SideVitals };
    /// <summary>The ids the radar takeover holds: <c>new RetailHeldWindows(RadarWindows)</c>.</summary>
    internal static readonly IReadOnlyList<uint> RadarWindows = new[] { Radar };

    // SmartBox::smartbox; its player (CPhysicsObj) at +0xF8 and the player's id at +0xF4.
    internal const uint SmartBox = 0x0083DA58;
    internal const int SmartBoxPlayer = 0xF8;
    internal const int SmartBoxPlayerId = 0xF4;
    // CPhysicsObj: id +0x08, hash next +0x04, Position at +0x48 (cell +0x4C, Frame +0x50: the local-to-global matrix at Frame +0x10, its
    // forward axis at +0x1C/+0x20, the origin at Frame +0x34), the cell it is in at +0x90.
    internal const int ObjectId = 0x08;
    internal const int ObjectNext = 0x04;
    internal const int ObjectCell = 0x4C;
    internal const int ObjectForwardX = 0x6C;
    internal const int ObjectOrigin = 0x84;
    internal const int ObjectInCell = 0x90;
    // CObjectMaint (the physics objects by id), read from 0x00844D64; its hash's mask +0x88, buckets +0x90 (mask + 1 of them).
    internal const uint ObjectMaint = 0x00844D64;
    internal const int MaintMask = 0x88;
    internal const int MaintBuckets = 0x90;
    // ACCObjectMaint::GetWeenieObject (Cdecl id -> ACCWeenieObject*), gmRadarUI::GetBlipColor (Cdecl RGBA* out, ACCWeenieObject*).
    internal const uint GetWeenieObject = 0x005583F0;
    internal const uint GetBlipColor = 0x004D8350;
    // ACCWeenieObject: description flags +0x100 (0x80 UiHidden), radar colour +0x104, radar behaviour +0x108, its physics object +0x94.
    internal const int WeenieFlags = 0x100;
    internal const int WeenieBehavior = 0x108;
    internal const int WeeniePhysics = 0x94;
    internal const uint UiHidden = 0x80;
    // The calls retail's radar lock button makes: CPlayerSystem's getter (Cdecl () -> CPlayerSystem*), PlayerModule::SetLockUI (ThisCall
    // PlayerModule*, bool) and UIElementManager::BroadcastGlobalMessage (ThisCall manager, 0xD, 0).
    internal const uint GetPlayerSystem = 0x0055E1D0;
    internal const uint SetLockUi = 0x005D4340;
    internal const uint BroadcastGlobalMessage = 0x0045B4C0;
    internal const uint UiLockChangedMessage = 0xD;

    // Where each retail radar colour lives in the static table GetBlipColor reads (the enum's order is not the table's).
    private static readonly IReadOnlyDictionary<RadarColor, uint> ColourAddress = new Dictionary<RadarColor, uint>
    {
        [RadarColor.Blue] = 0x0081C69C, [RadarColor.Gold] = 0x0081C6AC, [RadarColor.Yellow] = 0x0081C6BC, [RadarColor.White] = 0x0081C6CC,
        [RadarColor.Red] = 0x0081C6DC, [RadarColor.Purple] = 0x0081C6EC, [RadarColor.Pink] = 0x0081C6FC, [RadarColor.Green] = 0x0081C70C,
        [RadarColor.Cyan] = 0x0081C71C, [RadarColor.BrightGreen] = 0x0081C72C
    };

    internal static readonly IReadOnlyList<NativeUiEntry> VitalsTakeover = new[]
    {
        Entry("LoadScreenLayout maps <VITS> to the stacked vitals", 0x004EB7E4, "68 FA 05 00 10", "instruction reference (push 0x100005FA)"),
        Entry("LoadScreenLayout maps <SVIT> to the side-by-side vitals", 0x004EB80B, "68 D5 06 00 10", "instruction reference (push 0x100006D5)"),
        // Both slots hold 0x004CF950, a lone jmp to UIElement::SetVisible (0x00462390): retail hides them as the takeover does.
        Entry("gmFloatyVitalsUI's SetVisible (vtable 0x007BB398 +0x18) is the shared thunk", 0x007BB3B0, "50 F9 4C 00", "data reference (vtable slot)"),
        Entry("gmFloatySideVitalsUI's SetVisible (vtable 0x007BBAA0 +0x18) is the shared thunk", 0x007BBAB8, "50 F9 4C 00", "data reference (vtable slot)")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> RadarTakeover = new[]
    {
        Entry("LoadScreenLayout maps <RADA> to the radar", 0x004EB913, "68 D2 06 00 10", "instruction reference (push 0x100006D2)")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Player = new[]
    {
        Entry("gmRadarUI reads the player at SmartBox::smartbox (0x0083DA58) +0xF8 for its heading", 0x004D9D3E,
            "8B 0D 58 DA 83 00 8B 89 F8 00 00 00 3B CB 75 0F", "instruction reference"),
        Entry("gmRadarUI::AddObject skips the player's id (SmartBox +0xF4)", 0x004DA681, "A1 58 DA 83 00 85 C0 74 08 8B 80 F4 00 00 00",
            "instruction reference"),
        Entry("SmartBox::convert_to_player_space body: an object in a cell (+0x90), its Position at +0x48", 0x00452DE6,
            "83 EC 18 85 C9 74 12 8B 44 24 1C 85 C0 74 0A 8B 90 90 00 00 00 85 D2 75 08 33 C0 83 C4 18 C2 08 00 8D 54 24 00 52 83 C0 48 50 8D 44 24 14 50 83 C1 48",
            "ThisCall (SmartBox*, CPhysicsObj*, Vector3*) at 0x00452DE0"),
        Entry("SmartBox::is_player_outside body: the cell at +0x4C, outdoors below cell 0x100", 0x00451EC6,
            "85 C0 74 12 8B 40 4C 25 FF FF 00 00 3D 00 01 00 00", "ThisCall (SmartBox*) -> bool at 0x00451EC0"),
        Entry("Frame::get_heading body: 450 - atan2(forward axis at frame +0x20, +0x1C) in degrees, mod 360", 0x005364A1,
            "D9 41 28 D8 41 10 D8 0D 44 63 79 00 D8 41 1C D9 41 2C D8 41 14 D8 0D 44 63 79 00 D8 41 20 D9 C9 D9 F3 DC 0D C8 C6 79 00 DC 2D C0 C6 79 00 D9 54 24 00 DD 05 B8 C6 79 00",
            "ThisCall (Frame*) -> float at 0x005364A0"),
        Entry("Frame::get_heading's zero factor, so the axis is frame +0x1C, +0x20 alone", 0x00796344, "00 00 00 00", "static float"),
        Entry("Position::localtolocal reads the origin x at Position +0x3C", 0x00452850, "D9 40 30 D8 49 08 DE C1 D8 40 3C", "instruction reference"),
        Entry("Position::localtolocal reads the origin y at Position +0x40", 0x0045286C, "D9 40 34 D8 49 08 DE C1 D8 40 40", "instruction reference"),
        Entry("Position::localtolocal reads the origin z at Position +0x44 and the cell at +0x04", 0x00452888,
            "D9 40 38 D8 49 08 8B 4E 04 DE C1 D8 40 44 8B 40 04", "instruction reference")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Objects = new[]
    {
        Entry("A CPhysicsObj method (0x00511560) reads CObjectMaint from 0x00844D64 and calls CObjectMaint::GetObjectA", 0x0051156A,
            "8B 0D 64 4D 84 00 33 DB 85 C9 74 5B 55 E8 14 73 FF FF", "instruction reference"),
        Entry("CObjectMaint::GetObjectA body: mask +0x88, shift +0x8C, buckets +0x90; a node's next +0x04, its id +0x08", 0x00508894,
            "56 8B C1 8B 88 8C 00 00 00 8B F2 D3 EE 8B 88 88 00 00 00 8B 80 90 00 00 00 33 F2 23 F1 8B 04 B0 85 C0 5E 74 13 8D A4 24 00 00 00 00 3B 50 08 74 09 8B 40 04",
            "ThisCall (CObjectMaint*, uint id) -> CPhysicsObj* at 0x00508890"),
        Entry("gmRadarUI::DrawObjects skips an object with no cell (+0x90) or cell id (+0x4C)", 0x004DA0D7,
            "E8 B4 E7 02 00 85 C0 0F 84 11 02 00 00 8B 88 90 00 00 00 85 C9 0F 84 03 02 00 00 8B 48 4C 85 C9 0F 86 F8 01 00", "instruction reference"),
        Entry("ACCObjectMaint::GetWeenieObject body: calls through ACCObjectMaint (0x00842ADC)", GetWeenieObject + 6,
            "85 C9 74 0B 8B 44 24 04 50 E8 DC 04 FB FF C3", "Cdecl (uint id) -> ACCWeenieObject*"),
        Entry("ACCWeenieObject's physics-object setter (0x00401DC0) stores it at +0x94", 0x00401DC4, "89 81 94 00 00 00 C2 04 00",
            "ThisCall (ACCWeenieObject*, CPhysicsObj*)"),
        Entry("ACCWeenieObject::InqShowableOnRadar body: a physics object, and radar behaviour (+0x108) 2, 3 or 4", 0x0058D086,
            "85 C0 75 05 33 C0 C2 04 00 8B 81 08 01 00 00 83 F8 02 74 0A 83 F8 04 74 05 83 F8 03 75 E6 B8 01 00 00 00 C2 04 00",
            "ThisCall (ACCWeenieObject*, void*) -> bool at 0x0058D080"),
        Entry("gmRadarUI::GetBlipColor body: description flags +0x100 (UiHidden 0x80), radar colour +0x104, cases 1 to 10", GetBlipColor + 1,
            "56 8B 74 24 10 85 F6 0F 84 02 04 00 00 8B 8E 00 01 00 00 84 C9 0F 88 F4 03 00 00 8B 86 04 01 00 00 85 C0 0F 84 F5 01 00 00 48 83 F8 09 0F 87 BF 01 00 00 FF 24 85 8C 87 4D 00",
            "Cdecl (RGBAColor* out, ACCWeenieObject*) -> RGBAColor*"),
        Entry("gmRadarUI::GetBlipColor's jump table (colour 1 to 10)", 0x004D878C,
            "8B 83 4D 00 B7 83 4D 00 E3 83 4D 00 0F 84 4D 00 3B 84 4D 00 67 84 4D 00 93 84 4D 00 BF 84 4D 00 EB 84 4D 00 17 85 4D 00", "data reference")
    }.Concat(RadarColors.Retail.Select(colour => new NativeUiEntry($"Retail radar colour {colour.Key}", ColourAddress[colour.Key],
        Rgba(colour.Value), Source, "static RGBAColor (4 floats)"))).ToArray();

    internal static readonly IReadOnlyList<NativeUiEntry> Lock = new[]
    {
        Entry("PlayerModule::SetLockUI body: CharacterOptions2 (+0x90) bit 24", SetLockUi + 6,
            "8A 44 24 04 80 E2 01 3A D0 74 29 84 C0 8B 81 90 00 00 00", "ThisCall (PlayerModule*, bool) at 0x005D4340"),
        Entry("UIElementManager::BroadcastGlobalMessage body", BroadcastGlobalMessage + 1, "8B 5C 24 08 33 D2 8B C3 F7 B1 8C 01 00 00",
            "ThisCall (UIElementManager*, uint message, int) at 0x0045B4C0"),
        // Its call targets pin GetPlayerSystem, SetLockUi and BroadcastGlobalMessage; the port makes the same calls on the same receivers.
        Entry("gmRadarUI's lock button: SetLockUI on CPlayerSystem +0x34, then BroadcastGlobalMessage(0xD, 0) on UIElementManager (0x0083E03C)",
            0x004D8286, "E8 45 5F 08 00 6A 01 8D 48 34 E8 AB C0 0F 00 8B 0D 3C E0 83 00 6A 00 6A 0D E8 1C 32 F8 FF", "instruction reference")
    };

    /// <summary>Checks each feature's list on its own: a mismatch, or an address that cannot be read, turns that feature off and logs it.</summary>
    internal static RetailHudNatives Validate(Func<uint, int, byte[]> read, Action<string> log) => new(
        Check("vitals takeover", VitalsTakeover, read, log), Check("radar takeover", RadarTakeover, read, log),
        Check("player position", Player, read, log), Check("radar objects", Objects, read, log), Check("radar lock", Lock, read, log));

    // NativeUiCatalogue.Validate catches failed reads itself, so nothing here throws.
    private static bool Check(string feature, IEnumerable<NativeUiEntry> entries, Func<uint, int, byte[]> read, Action<string> log) =>
        NativeUiCatalogue.Validate(read, log, entries, $"Dereth HUD {feature} is off: the running client does not match its native entries.");

    private static NativeUiEntry Entry(string name, uint address, string bytes, string convention) =>
        new(name, address, NativeUiCatalogue.Bytes(bytes), Source, convention);

    private static byte[] Rgba((float R, float G, float B) colour) =>
        new[] { colour.R, colour.G, colour.B, 1f }.SelectMany(BitConverter.GetBytes).ToArray();
}

/// <summary>Which HUD features the running client supports. The radar window needs its takeover, the player and the objects.</summary>
internal sealed record RetailHudNatives(bool Vitals, bool Radar, bool Player, bool Objects, bool Lock)
{
    public bool RadarWindow => Radar && Player && Objects;
}

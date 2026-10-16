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
internal static class RetailHudCatalogue
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb";
    private const string Dat = "installed client_local_English.dat, LayoutDesc 0x21000005";

    /// <summary>Retail's stacked vitals (tag &lt;VITS&gt;, gmFloatyVitalsUI 0x1000004D), placed from 0x2100006C/0x100005F9.</summary>
    internal const uint StackedVitals = 0x100005FA;
    /// <summary>Retail's side-by-side vitals (tag &lt;SVIT&gt;, gmFloatySideVitalsUI 0x10000056), placed from 0x21000075/0x100006D4. Retail
    /// shows one of the two, by its "side by side vitals" option.</summary>
    internal const uint SideVitals = 0x100006D5;
    /// <summary>Retail's radar (tag &lt;RADA&gt;, gmRadarUI 0x10000010), placed from 0x21000074/0x100006D3.</summary>
    internal const uint Radar = 0x100006D2;

    /// <summary>The ids the vitals takeover holds: <c>new RetailHeldWindows(VitalsWindows)</c>.</summary>
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
    // CObjectMaint (the physics objects by id): CPhysicsObj::GetObjectA reads it from 0x00844D64; its hash's mask +0x88, buckets +0x90.
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
    // PlayerModule::SetLockUI (ThisCall PlayerModule*, bool), UIElementManager::BroadcastGlobalMessage (ThisCall manager, 0xD, 0).
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
        NativeUiCatalogue.Root("StackedVitals", StackedVitals, $"{Dat} (placed from 0x2100006C/0x100005F9)"),
        NativeUiCatalogue.Root("SideVitals", SideVitals, $"{Dat} (placed from 0x21000075/0x100006D4)"),
        Entry("LoadScreenLayout maps <VITS> to the stacked vitals", 0x004EB7E4, "68 FA 05 00 10", "instruction reference (push 0x100005FA)"),
        Entry("LoadScreenLayout maps <SVIT> to the side-by-side vitals", 0x004EB80B, "68 D5 06 00 10", "instruction reference (push 0x100006D5)"),
        Entry("gmFloatyVitalsUI and gmFloatySideVitalsUI::SetVisible are UIElement::SetVisible", 0x004CF950, "E9 3B 2A F9 FF",
            "jmp 0x00462390 (the vtable +0x18 thunk both share)")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> RadarTakeover = new[]
    {
        NativeUiCatalogue.Root("Radar", Radar, $"{Dat} (placed from 0x21000074/0x100006D3)"),
        Entry("LoadScreenLayout maps <RADA> to the radar", 0x004EB913, "68 D2 06 00 10", "instruction reference (push 0x100006D2)")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Player = new[]
    {
        Entry("CPlayerSystem::IsOutside reads SmartBox::smartbox (0x0083DA58)", 0x0055E5C0, "8B 0D 58 DA 83 00 85 C9 74 0B E8 F1 38 EF FF", "Cdecl () -> bool"),
        Entry("SmartBox::is_player_outside: the player at +0xF8, its cell at +0x4C, outdoors below cell 0x100", 0x00451EC0,
            "8B 81 F8 00 00 00 85 C0 74 12 8B 40 4C 25 FF FF 00 00 3D 00 01 00 00", "ThisCall (SmartBox*) -> bool"),
        Entry("gmRadarUI::AddObject skips the player's id (SmartBox +0xF4)", 0x004DA681, "A1 58 DA 83 00 85 C0 74 08 8B 80 F4 00 00 00",
            "instruction reference"),
        Entry("CPhysicsObj::get_heading: the frame at +0x50", 0x00512010, "83 C1 50 E9 88 44 02 00", "ThisCall -> Frame::get_heading"),
        Entry("Frame::get_heading: atan2 of the forward axis at frame +0x1C, +0x20", 0x005364A0,
            "51 D9 41 28 D8 41 10 D8 0D 44 63 79 00 D8 41 1C D9 41 2C D8 41 14 D8 0D 44 63 79 00 D8 41 20 D9 C9 D9 F3", "ThisCall (Frame*) -> float"),
        Entry("Position::localtolocal reads the origin x at Position +0x3C", 0x00452850, "D9 40 30 D8 49 08 DE C1 D8 40 3C", "instruction reference"),
        Entry("Position::localtolocal reads the origin y at Position +0x40", 0x0045286C, "D9 40 34 D8 49 08 DE C1 D8 40 40", "instruction reference"),
        Entry("Position::localtolocal reads the origin z at Position +0x44 and the cell at +0x04", 0x00452888,
            "D9 40 38 D8 49 08 8B 4E 04 DE C1 D8 40 44 8B 40 04", "instruction reference")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Objects = new[]
    {
        Entry("CPhysicsObj::GetObjectA reads CObjectMaint from 0x00844D64", 0x0050FF60,
            "8B 0D 64 4D 84 00 85 C9 74 0B 8B 44 24 04 50 E8 1C 89 FF FF C3", "Cdecl (uint id) -> CPhysicsObj*"),
        Entry("CObjectMaint::GetObjectA: mask +0x88, shift +0x8C, buckets +0x90; a node's next +0x04, its id +0x08", 0x00508890,
            "8B 54 24 04 56 8B C1 8B 88 8C 00 00 00 8B F2 D3 EE 8B 88 88 00 00 00 8B 80 90 00 00 00 33 F2 23 F1 8B 04 B0 85 C0 5E 74 13 8D A4 24 00 00 00 00 3B 50 08 74 09 8B 40 04",
            "ThisCall (CObjectMaint*, uint id) -> CPhysicsObj*"),
        Entry("gmRadarUI::DrawObjects skips an object with no cell (+0x90) or cell id (+0x4C)", 0x004DA0D7,
            "E8 B4 E7 02 00 85 C0 0F 84 11 02 00 00 8B 88 90 00 00 00 85 C9 0F 84 03 02 00 00 8B 48 4C 85 C9 0F 86 F8 01 00", "instruction reference"),
        Entry("ACCObjectMaint::GetWeenieObject reads ACCObjectMaint from 0x00842ADC", GetWeenieObject,
            "8B 0D DC 2A 84 00 85 C9 74 0B 8B 44 24 04 50 E8 DC 04 FB FF C3", "Cdecl (uint id) -> ACCWeenieObject*"),
        Entry("ACCWeenieObject::InqShowableOnRadar: a physics object (+0x94) and radar behaviour (+0x108) 2, 3 or 4", 0x0058D080,
            "8B 81 94 00 00 00 85 C0 75 05 33 C0 C2 04 00 8B 81 08 01 00 00 83 F8 02 74 0A 83 F8 04 74 05 83 F8 03 75 E6 B8 01 00 00 00 C2 04 00",
            "ThisCall (ACCWeenieObject*, void*) -> bool"),
        Entry("gmRadarUI::GetBlipColor: description flags +0x100 (UiHidden 0x80), radar colour +0x104, cases 1 to 10", GetBlipColor,
            "51 56 8B 74 24 10 85 F6 0F 84 02 04 00 00 8B 8E 00 01 00 00 84 C9 0F 88 F4 03 00 00 8B 86 04 01 00 00 85 C0 0F 84 F5 01 00 00 48 83 F8 09 0F 87 BF 01 00 00 FF 24 85 8C 87 4D 00",
            "Cdecl (RGBAColor* out, ACCWeenieObject*) -> RGBAColor*"),
        Entry("gmRadarUI::GetBlipColor's jump table (colour 1 to 10)", 0x004D878C,
            "8B 83 4D 00 B7 83 4D 00 E3 83 4D 00 0F 84 4D 00 3B 84 4D 00 67 84 4D 00 93 84 4D 00 BF 84 4D 00 EB 84 4D 00 17 85 4D 00", "data reference")
    }.Concat(RadarColors.Retail.Select(colour => new NativeUiEntry($"Retail radar colour {colour.Key}", ColourAddress[colour.Key],
        Rgba(colour.Value), Source, "static RGBAColor (4 floats)"))).ToArray();

    internal static readonly IReadOnlyList<NativeUiEntry> Lock = new[]
    {
        // Past each function's first instruction, which a hook would change.
        Entry("PlayerModule::SetLockUI body: CharacterOptions2 (+0x90) bit 24", SetLockUi + 6,
            "8A 44 24 04 80 E2 01 3A D0 74 29 84 C0 8B 81 90 00 00 00", "ThisCall (PlayerModule*, bool) at 0x005D4340"),
        Entry("UIElementManager::BroadcastGlobalMessage body", BroadcastGlobalMessage + 1, "8B 5C 24 08 33 D2 8B C3 F7 B1 8C 01 00 00",
            "ThisCall (UIElementManager*, uint message, int) at 0x0045B4C0"),
        Entry("gmRadarUI's lock button: SetLockUI on PlayerModule (+0x34), then BroadcastGlobalMessage(0xD, 0)", 0x004D8286,
            "E8 45 5F 08 00 6A 01 8D 48 34 E8 AB C0 0F 00 8B 0D 3C E0 83 00 6A 00 6A 0D E8 1C 32 F8 FF", "instruction reference")
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

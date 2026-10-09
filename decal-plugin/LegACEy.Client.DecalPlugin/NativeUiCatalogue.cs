using System;
using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.DecalPlugin;

internal sealed class NativeUiEntry
{
    public NativeUiEntry(string name, uint? address, byte[] expectedBytes, string source, string callingConvention, uint? constantValue = null)
    { Name = name; Address = address; ExpectedBytes = expectedBytes; Source = source; CallingConvention = callingConvention; ConstantValue = constantValue; }
    public string Name { get; }
    public uint? Address { get; }
    public byte[] ExpectedBytes { get; }
    public string Source { get; }
    public string CallingConvention { get; }
    /// <summary>Compile-time identifier value. It is not a process address and is not byte-checked.</summary>
    public uint? ConstantValue { get; }
}

/// <summary>Version gate for every fixed native UI entry used by this plugin.</summary>
internal static class NativeUiCatalogue
{
    private const string Chorizite = "Chorizite AcClient bindings / installed end-of-retail acclient.exe";
    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        Function("UIElementManager::GetElement", 0x00459A00, "53 56 57 8B 79 1C 33 C0 33 D2 85 FF 76 25", "ThisCall"),
        Function("UIElement::IsVisible", 0x004603A0, "A1 3C E0 83 00 85 C0 56 8B F1 75 04 32 C0", "ThisCall"),
        Function("UIElement::SetVisible", 0x00462390, "51 53 56 57 8B 3D 3C E0 83 00 8B F1", "ThisCall"),
        Function("UIElement::GetCurrentPosition", 0x00460180, "8B 54 24 04 8D 41 7C 3B D0 74 27 56 8B 30", "ThisCall"),
        new("UIElement::SetSaveLocation", 0x0045FA10, Bytes("0F B6 44 24 04 8B 91 54 05 00 00 C1 E0 04 33 C2 83 E0 10 33 D0 89 91 54 05 00 00 C2 04 00"), "https://actypes.utilitybelt.me/type/UIElement; our disassembly of installed end-of-retail acclient.exe", "ThisCall"),
        Function("UIElement::MoveTo", 0x004634C0, "83 EC 30 53 56 57 8B F1 E8 33 C9 23 00 8B CE", "ThisCall"),
        new("gmFloatyIndicatorsUI::MoveTo vtable reference", 0x007BCE3C, Bytes("F0 44 4D 00"), "Our installed-executable disassembly and read-only live vtable: MoveTo slot +0x2C", "virtual ThisCall reference"),
        new("gmFloatyIndicatorsUI::MoveTo", 0x004D44F0, Bytes("83 EC 10 53 8B 5C 24 18 55 56 8B F1 8B 06 57 8B 7C 24 28 FF 90 A0 00 00 00"), "Our disassembly of installed end-of-retail acclient.exe; native override clamps, calls base MoveTo, writes PlayerModule panel properties", "ThisCall via element vtable +0x2C"),
        new("gmFloatyIndicatorsUI::MoveTo saved X writeback", 0x004D45FB, Bytes("8B 8E FC 05 00 00 8D 44 24 18 50 83 C7 04 51 8B CF E8 AF 20 10 00"), "Our installed-executable disassembly: panel key +0x5FC and PlayerModule::SetPanelProperty", "ThisCall reference"),
        new("gmFloatyIndicatorsUI::MoveTo saved Y writeback", 0x004D4646, Bytes("8B 8E FC 05 00 00 8D 44 24 10 50 51 8B CF E8 67 20 10 00"), "Our installed-executable disassembly: panel key +0x5FC and PlayerModule::SetPanelProperty", "ThisCall reference"),
        Function("CM_UI::SendNotice_EndCharacterSession", 0x00479F40, "E8 CB 08 00 00 8B 10 68 E2 D1 4D 00 8B C8 FF 52 10", "Cdecl"),
        new("PlayerModule::LockUI receiver adjustment", 0x004D0213, Bytes("E8 B8 DF 08 00 8D 48 34 E8 10 41 10 00"), "Our disassembly of installed end-of-retail acclient.exe: retail UI lock handler gets CPlayerSystem, adds 0x34, calls LockUI", "ThisCall receiver reference"),
        new("PlayerModule::LockUI", 0x005D4330, Bytes("33 C0 8A 81 93 00 00 00 83 E0 01 C3"), "https://actypes.utilitybelt.me/type/PlayerModule; verified against installed acclient.exe", "ThisCall"),
        new("RenderDeviceD3D::EndScene", 0x005A0E10, PostUiDrawHook.Signature, "Our capstone read of end-of-retail acclient.exe; unique executable-section signature", "MicrosoftThiscall; signature scan; expected RVA 0x1A0E10"),
        new("UIElementManager::s_pInstance reference", 0x004603A1, new byte[] { 0x3C, 0xE0, 0x83, 0x00 }, Chorizite, "data reference (absolute VA)"),
        new("CPlayerSystem::s_pPlayerSystem reference", 0x0055E1D0, Bytes("A1 9C 11 87 00 C3"), "Our disassembly of installed end-of-retail acclient.exe: player-system getter", "data reference (absolute VA)"),
        new("CPlayerSystem::s_pPlayerSystem", 0x0087119C, Array.Empty<byte>(), "https://actypes.utilitybelt.me/type/CPlayerSystem", "runtime pointer; expected readable 4-byte slot"),
        Root("Indicators", 0x10000611), Root("CharacterInfo", 0x10000183), Root("PositiveEffects", 0x10000184),
        Root("NegativeEffects", 0x10000185), Root("LinkStatus", 0x10000187), Root("MiniGame", 0x10000188), Root("Vitae", 0x1000018A),
        Root("InventoryPanel", 0x1000018B)
    };

    internal static bool Validate(Func<uint, int, byte[]> read, Action<string> log, IEnumerable<NativeUiEntry>? entries = null)
    {
        var failures = new List<string>();
        foreach (var entry in entries ?? Entries)
        {
            if (entry.Address is not uint address) continue;
            byte[] actual;
            // The PlayerSystem global is a runtime pointer, so validate that its four bytes are readable;
            // its contents vary with the character-session lifecycle and have no stable expected value.
            var expectedLength = entry.ExpectedBytes.Length == 0 ? 4 : entry.ExpectedBytes.Length;
            try { actual = read(address, expectedLength); }
            catch { failures.Add(entry.Name); continue; }
            if (actual.Length != expectedLength ||
                (entry.ExpectedBytes.Length != 0 && !actual.SequenceEqual(entry.ExpectedBytes))) failures.Add(entry.Name);
        }
        foreach (var name in failures) log($"Native UI compatibility check failed: {name}.");
        if (failures.Count != 0) log("Retail takeover disabled: the running client does not match the reviewed native UI catalogue.");
        return failures.Count == 0;
    }

    private static NativeUiEntry Function(string name, uint address, string bytes, string convention) =>
        new(name, address, Bytes(bytes), Chorizite, convention);

    private static byte[] Bytes(string bytes) => bytes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(value => Convert.ToByte(value, 16)).ToArray();

    private static NativeUiEntry Root(string name, uint id) =>
        new($"RootElementId::{name}", null, Array.Empty<byte>(), "Chorizite.Common RootElementId", "compile-time constant; not called", id);
}

using System;
using System.Collections.Generic;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The native entries the Dereth chat window's port uses: retail's chat windows (takeover), the line every chat window is shown (incoming),
/// the path retail's own chat box sends through (send), the chat timestamp option, the channel options, fellowship membership and the
/// keyboard focus of the chat entry and of retail's other fields (Enter). Each feature has its own list, checked on its own, because Decal patches call sites and prologues at
/// runtime: a mismatch costs that feature only. One dependency is shared on purpose: the CPlayerSystem getter is in both the timestamp and
/// the channel lists, so its mismatch turns both off. Every address and byte list was read from the installed end-of-retail acclient.exe
/// (SHA-256 bca95bbe...); acclient.pdb (a near build) gave the names. The recipes are in the chat spike note; the port adds the constants it reads.
/// </summary>
internal static class RetailChatCatalogue
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb";
    private const string Dat = "installed client_local_English.dat, LayoutDesc 0x21000005";

    /// <summary>Retail's main chat window (tag &lt;CHAT&gt;): placed in 0x21000005 from 0x2100006F/0x10000600; it embeds gmMainChatUI.</summary>
    internal const uint MainChatWindow = 0x10000601;
    /// <summary>gmMainChatUI (type 0x10000041), the root of LayoutDesc 0x21000006 that the main chat window embeds. A ChatInterface: the UIElement is at +4.</summary>
    internal const uint MainChat = 0x1000000E;
    /// <summary>
    /// ChatInterface::OnAction, at slot +0x04 of the vtable at a ChatInterface's +0. The main chat is the element under the main chat window
    /// whose ChatInterface (the word before the element) has a vtable in .rdata with this there: GetElement finds top-level windows only,
    /// and the element's id inside the window is not its layout's.
    /// </summary>
    internal const uint ChatInterfaceOnAction = 0x004F62F0;
    /// <summary>acclient.exe's .rdata, where vtables are: a word outside it is not followed.</summary>
    internal const uint RdataStart = 0x00793000, RdataEnd = 0x0080B000;
    /// <summary>The floating chat windows 1-4 (tags &lt;FCH1&gt;-&lt;FCH4&gt;, gmFloatyChatUI, type 0x10000040), placed in 0x21000005 from 0x2100005B/0x100004F7.</summary>
    internal static readonly IReadOnlyList<uint> FloatingChats = new uint[] { 0x10000505, 0x1000050E, 0x1000050F, 0x10000510 };
    /// <summary>NoticeHandler's own vtable: every slot a no-op of the right size. A handler of ours copies it and replaces slot +0x38.</summary>
    internal const uint NoticeHandlerVtable = 0x007CCB60;
    /// <summary>ChatInterface::ProcessCommand's call to OnChatCommand: <c>mov ecx, [ChatInterface+0x634]</c>, skipped when null, then the
    /// E8 at +0x16, whose live target is called.</summary>
    internal const uint ProcessCommandSendCall = 0x004F5D95;
    /// <summary>ChatInterface::DeactivateChatEntry, ThisCall on the ChatInterface: drops the entry's focus and sends ToggleChatEntry(false).</summary>
    internal const uint DeactivateChatEntry = 0x004F3C20;
    /// <summary>
    /// UIElementManager::SetFocusElement, ThisCall (manager, element): retail's keyboard focus. Decal patches its prologue to note the focus,
    /// and its ChatState is true while the focused element is one of retail's text entries.
    /// </summary>
    internal const uint SetFocusElement = 0x0045B970;

    internal static readonly IReadOnlyList<NativeUiEntry> Takeover = new[]
    {
        NativeUiCatalogue.Root("ChatMainWindow", MainChatWindow, $"{Dat} (placed from 0x2100006F/0x10000600)"),
        NativeUiCatalogue.Root("ChatMain", MainChat, "installed client_local_English.dat, LayoutDesc 0x21000006 root, embedded by 0x2100006F/0x10000014"),
        NativeUiCatalogue.Root("ChatFloating1", FloatingChats[0], $"{Dat} (placed from 0x2100005B/0x100004F7)"),
        NativeUiCatalogue.Root("ChatFloating2", FloatingChats[1], $"{Dat} (placed from 0x2100005B/0x100004F7)"),
        NativeUiCatalogue.Root("ChatFloating3", FloatingChats[2], $"{Dat} (placed from 0x2100005B/0x100004F7)"),
        NativeUiCatalogue.Root("ChatFloating4", FloatingChats[3], $"{Dat} (placed from 0x2100005B/0x100004F7)"),
        Entry("LoadScreenLayout maps <CHAT> to the main chat window", 0x004EB6FA, "68 01 06 00 10", "instruction reference (push 0x10000601)"),
        Entry("LoadScreenLayout maps <FCH1> to floating chat 1", 0x004EB721, "68 05 05 00 10", "instruction reference (push 0x10000505)"),
        Entry("LoadScreenLayout maps <FCH2> to floating chat 2", 0x004EB748, "68 0E 05 00 10", "instruction reference (push 0x1000050E)"),
        Entry("LoadScreenLayout maps <FCH3> to floating chat 3", 0x004EB76F, "68 0F 05 00 10", "instruction reference (push 0x1000050F)"),
        Entry("LoadScreenLayout maps <FCH4> to floating chat 4", 0x004EB796, "68 10 05 00 10", "instruction reference (push 0x10000510)")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Incoming = new[]
    {
        Entry("NoticeRegistrar getter (0x00838374)", 0x0043C680, "A1 74 83 83 00 C3", "Cdecl () -> NoticeRegistrar*"),
        Entry("NoticeRegistrar vtable +0x04 is RegisterNoticeHandler", 0x00799FC8, "10 CA 43 00", "data reference (vtable 0x00799FC4 slot)"),
        Entry("NoticeRegistrar vtable +0x0C is UnRegisterAllNoticeHandlers", 0x00799FD0, "A0 C8 43 00", "data reference (vtable 0x00799FC4 slot)"),
        Entry("ChatInterface::PostInit registers for DisplayFinalStringInfo (0x186B6) through slot +0x04", 0x004F4B0F, "53 68 B6 86 01 00 8B CF FF 50 04",
            "instruction reference (push handler; push 0x186B6; call [eax+4])"),
        Entry("ECM_UI::SendNotice_DisplayFinalStringInfo calls handler slot +0x38 (type, text, prefix, target)", 0x006934D9,
            "8B 54 24 14 8B 01 57 53 55 52 FF 50 38", "instruction reference (the notice loop's call)"),
        Entry("NoticeHandler vtable +0x38 is the no-op DisplayFinalStringInfo", NoticeHandlerVtable + 0x38, "F0 15 40 00",
            "data reference (vtable slot)"),
        Entry("NoticeHandler::RecvNotice_DisplayFinalStringInfo no-op", 0x004015F0, "C2 10 00", "ThisCall, ret 0x10"),
        Entry("ClientSystem::AddTextToScroll passes type, text, timestamp prefix and target to the notice", 0x00564CB5,
            "8B 94 24 68 09 00 00 52 8D 44 24 3C 50 8D 8C 24 D0 00 00 00 51 56", "instruction reference (the pushes before the call)"),
        Entry("StringInfo::SetLiteralValue stores the literal at +0x80", 0x0042CC34, "8B 03 89 86 80 00 00 00",
            "instruction reference (mov [esi+0x80], eax)")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Send = new[]
    {
        // Retail's box loads its receiver from [ChatInterface+0x634] and skips the send when it is null; PostInit stored
        // GetCommunicationSystem() there, so the port may use the getter's [0x008717FC] directly (the same object).
        // Ends at the E8 opcode: its rel32 is read live, since Decal may point it at its own hook.
        Entry("ChatInterface::ProcessCommand loads [this+0x634], skips when null, calls OnChatCommand (text, [this+0x5FC])", ProcessCommandSendCall,
            "8B 8E 34 06 00 00 85 C9 74 11 8B 86 FC 05 00 00 50 8D 54 24 08 52 E8", "instruction reference; the call's target is read live"),
        Entry("ChatInterface::PostInit stores GetCommunicationSystem() at +0x634", 0x004F4B58, "E8 D3 A6 07 00 3B C5 89 86 30 06 00 00",
            "instruction reference (UIElement-relative +0x630 = ChatInterface+0x634)"),
        // Past the prologue, which a hook would change.
        Entry("ClientCommunicationSystem::OnChatCommand body stores the source window at +0x8C", 0x005821E6,
            "8B 44 24 24 6A 00 8D 4C 24 24 51 8B CE 89 87 8C 00 00 00", "ThisCall (PStringBase<wchar_t>*, uint), ret 8"),
        Entry("ClientCommunicationSystem::GetCommunicationSystem", 0x0056F230, "A1 FC 17 87 00 C3", "Cdecl () -> [0x008717FC]"),
        Entry("PStringBase<wchar_t>::PStringBase(const wchar_t*)", 0x00402730, "56 57 8B 7C 24 0C 85 FF 8B F1 74 2C 66 83 3F 00", "ThisCall, ret 4"),
        Entry("PStringBase<wchar_t>::~PStringBase", 0x004011B0, "56 8B 31 83 EE 14 8D 46 04 50 FF 15 F8 31 79 00", "ThisCall"),
        Entry("StringInfo::StringInfo reads the empty wide string (0x00818340)", 0x0042DCBA, "A1 40 83 81 00", "instruction reference (mov eax, [0x818340])")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Timestamp = new[]
    {
        Entry("PlayerModule::DisplayTimeStamps (CharacterOptions2 bit 6)", 0x005D4A00, "8B 81 90 00 00 00 C1 E8 06 83 E0 01 C3", "ThisCall () -> bool"),
        Entry("ClientSystem::AddTextToScroll asks DisplayTimeStamps on PlayerModule (+0x34) before the time prefix", 0x00564B91,
            "8D 48 34 E8 67 FE 06 00 84 C0 74 41", "instruction reference"),
        Entry("CPlayerSystem getter (0x0087119C)", 0x0055E1D0, "A1 9C 11 87 00 C3", "Cdecl () -> CPlayerSystem*")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Channels = new[]
    {
        Entry("PlayerModule::HearGeneralChat (CharacterOptions2 bit 8)", 0x005D45B0, "8B 81 90 00 00 00 C1 E8 08 83 E0 01 C3", "ThisCall () -> bool"),
        Entry("PlayerModule::HearTradeChat (CharacterOptions2 bit 9)", 0x005D4600, "8B 81 90 00 00 00 C1 E8 09 83 E0 01 C3", "ThisCall () -> bool"),
        Entry("PlayerModule::HearLFGChat (CharacterOptions2 bit 10)", 0x005D4650, "8B 81 90 00 00 00 C1 E8 0A 83 E0 01 C3", "ThisCall () -> bool"),
        Entry("PlayerModule::HearRoleplayChat (CharacterOptions2 bit 11)", 0x005D46A0, "8B 81 90 00 00 00 C1 E8 0B 83 E0 01 C3", "ThisCall () -> bool"),
        Entry("PlayerModule::HearSocietyChat (CharacterOptions2 bit 19)", 0x005D46F0, "8B 81 90 00 00 00 C1 E8 13 83 E0 01 C3", "ThisCall () -> bool"),
        Entry("PlayerModule::HearAllegianceChat (CharacterOptions1 bit 30)", 0x005D4560, "8B 81 8C 00 00 00 C1 E8 1E 83 E0 01 C3", "ThisCall () -> bool"),
        Entry("CCommunicationSystem::IsUsingTurbineChat ([0x00870BE4]+0x0C)", 0x00556ED0, "A1 E4 0B 87 00 85 C0 75 01 C3 8B 40 0C C3", "Cdecl () -> int"),
        Entry("CPlayerSystem getter (0x0087119C)", 0x0055E1D0, "A1 9C 11 87 00 C3", "Cdecl () -> CPlayerSystem*")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Fellowship = new[]
    {
        Entry("ClientFellowshipSystem::GetFellowshipSystem (0x0087150C)", 0x00569E30, "A1 0C 15 87 00 C3", "Cdecl () -> ClientFellowshipSystem*"),
        Entry("ClientFellowshipSystem::IsFellowshipLeader reads the fellowship at +0x10", 0x00569EF0, "8B 41 10 85 C0 74 0D 8B 40 1C 3B 44 24 04",
            "ThisCall (uint id) -> bool")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Enter = new[]
    {
        // The UIElementManager instance the port reads, and its active window at +0x32C: a function's first instructions (padding before it).
        Entry("UIElementManager instance (0x0083E03C) and its active window (+0x32C)", 0x004F3D00, "A1 3C E0 83 00 56 8B B0 2C 03 00 00 85 F6",
            "instruction reference (mov eax,[0x0083E03C]; mov esi,[eax+0x32C])"),
        // Past the prologue (push esi; mov esi, ecx; mov ecx, [esi+0x60C]), which a hook would change.
        Entry("ChatInterface::DeactivateChatEntry body", DeactivateChatEntry + 9, "8B 01 FF 90 08 01 00 00 8B 8E 0C 06 00 00 8B 11 FF 92 00 01 00 00",
            "ThisCall (ChatInterface*) at 0x004F3C20: the entry's vtable +0x108 and +0x100"),
        // The child list the port walks: count at +0x120, first link at +0x124; a link is its node + 8, the node holds the child at +0x10 and
        // the next link at +0x08. Past the prologue.
        Entry("UIElement::GetChild walks the child list (+0xB4 +0x6C count, +0x70 links, node child +0x10, next +0x08)", 0x00463BA5,
            "81 C2 B4 00 00 00 56 8B 72 6C 33 C0 33 C9 85 F6 76 0E 8B 4A 70 85 C9 74 05 83 C1 F8", "instruction reference"),
        Entry("UIElement::GetChild reads the child (+0x10) and the next link (+0x08)", 0x00463BD1,
            "8B 51 10 85 D2 74 08 39 B2 E4 02 00 00 74 15 8B 49 08 85 C9 74 10 83 C1 F8", "instruction reference"),
        Entry("A ChatInterface vtable: slot +0x04 is ChatInterface::OnAction (0x004F62F0)", 0x007BA3EC, "F0 62 4F 00", "vtable in .rdata"),
        Entry("ChatInterface::HandleEnterKey reads the active window (UIElementManager+0x32C)", 0x004F5F37, "8B B8 2C 03 00 00 85 FF 8B F1 74 5B",
            "instruction reference"),
        Entry("ChatInterface::HandleEnterKey compares the window's focused child with the entry (+0x60C)", 0x004F5F4F,
            "8B CF E8 DA A3 F6 FF 3B 86 0C 06 00 00 75 40", "instruction reference"),
        Entry("UIElement focused child (+0x1A4)", 0x00460330, "8B 81 A4 01 00 00 C3", "ThisCall () -> UIElement*"),
        // Retail's keyboard focus element, read so Enter is left to a retail field that has the keyboard. Past each function's first
        // instruction, which a hook would change.
        Entry("UIElement::GetHasFocus compares the element with UIElementManager's focus element (+0x328)", 0x0046031A,
            "8B B0 28 03 00 00 33 D2 3B F1 0F 94 C2 8A C2 5E C3", "ThisCall () -> bool at 0x00460310 (instance [0x0083E03C])"),
        Entry("UIElementManager::SetFocusElement body reads and stores the focus element at +0x328", 0x0045B992,
            "8B 8E 28 03 00 00 8B 01 FF 90 B4 00 00 00 85 FF 89 BE 28 03 00 00", "ThisCall (UIElementManager*, UIElement*) at 0x0045B970, called")
    };

    /// <summary>Checks each feature's list on its own: a mismatch, or an address that cannot be read, turns that feature off and logs it.</summary>
    internal static RetailChatNatives Validate(Func<uint, int, byte[]> read, Action<string> log) => new(
        Check("takeover", Takeover, read, log), Check("incoming lines", Incoming, read, log), Check("send", Send, read, log),
        Check("timestamps", Timestamp, read, log), Check("channel options", Channels, read, log), Check("fellowship", Fellowship, read, log),
        Check("Enter", Enter, read, log));

    // NativeUiCatalogue.Validate catches failed reads itself, so nothing here throws.
    private static bool Check(string feature, IEnumerable<NativeUiEntry> entries, Func<uint, int, byte[]> read, Action<string> log) =>
        NativeUiCatalogue.Validate(read, log, entries, $"Dereth chat {feature} is off: the running client does not match its native entries.");

    private static NativeUiEntry Entry(string name, uint address, string bytes, string convention) =>
        new(name, address, NativeUiCatalogue.Bytes(bytes), Source, convention);
}

/// <summary>Which chat features the running client supports. The chat window needs its takeover, incoming lines and send.</summary>
internal sealed record RetailChatNatives(bool Takeover, bool Incoming, bool Send, bool Timestamp, bool Channels, bool Fellowship, bool Enter)
{
    public bool ChatWindow => Takeover && Incoming && Send;
}

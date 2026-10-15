using System;
using System.Collections.Generic;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The native entries the Dereth chat window's port uses: retail's chat windows (takeover), the line every chat window is shown (incoming),
/// the path retail's own chat box sends through (send), the chat timestamp option, the channel options, fellowship membership and the chat
/// entry's keyboard focus (Enter). Each feature has its own list, checked on its own, because Decal patches call sites and prologues at
/// runtime: a mismatch costs that feature only. Every address and byte list was read from the installed end-of-retail acclient.exe
/// (SHA-256 bca95bbe...); acclient.pdb (a near build) gave the names. See the chat spike note for the recipes.
/// </summary>
internal static class RetailChatCatalogue
{
    private const string Source = "our disassembly of the installed end-of-retail acclient.exe; names from acclient.pdb";
    private const string Dat = "installed client_local_English.dat, LayoutDesc 0x21000005";

    // Takeover: the placed ids. LoadScreenLayout (0x004EB580) maps the UI layout file's tags to the same ids.
    /// <summary>Retail's main chat window (tag &lt;CHAT&gt;): placed in 0x21000005 from 0x2100006F/0x10000600; it embeds gmMainChatUI.</summary>
    internal const uint MainChatWindow = 0x10000601;
    /// <summary>gmMainChatUI (type 0x10000041), the root of LayoutDesc 0x21000006 that the main chat window embeds. A ChatInterface: the UIElement is at +4.</summary>
    internal const uint MainChat = 0x1000000E;
    /// <summary>The floating chat windows 1-4 (tags &lt;FCH1&gt;-&lt;FCH4&gt;, gmFloatyChatUI, type 0x10000040), placed in 0x21000005 from 0x2100005B/0x100004F7.</summary>
    internal static readonly IReadOnlyList<uint> FloatingChats = new uint[] { 0x10000505, 0x1000050E, 0x1000050F, 0x10000510 };

    // Incoming: a NoticeHandler registered for notice 0x186B6 hears every line any chat window is shown.
    /// <summary>The NoticeRegistrar getter: returns [0x00838374].</summary>
    internal const uint NoticeRegistrarGetter = 0x0043C680;
    /// <summary>The registrar's vtable slot RegisterNoticeHandler (uint noticeId, NoticeHandler* handler), ThisCall, ret 8.</summary>
    internal const int RegisterNoticeHandlerSlot = 0x04;
    /// <summary>The registrar's vtable slot UnRegisterAllNoticeHandlers (NoticeHandler* handler), ThisCall, ret 4.</summary>
    internal const int UnRegisterAllNoticeHandlersSlot = 0x0C;
    /// <summary>DisplayFinalStringInfo: the notice AddTextToScroll sends for each line shown in chat.</summary>
    internal const uint DisplayFinalStringInfoNotice = 0x000186B6;
    /// <summary>The handler slot that notice calls: (uint chatType, StringInfo* text, StringInfo* timestampPrefix, uint targetWindow), ThisCall, ret 0x10.</summary>
    internal const int DisplayFinalStringInfoSlot = 0x38;
    /// <summary>NoticeHandler's own vtable: every slot a no-op of the right size. A handler of ours copies it and replaces one slot.</summary>
    internal const uint NoticeHandlerVtable = 0x007CCB60;
    /// <summary>A StringInfo's literal text: a wchar_t* (PStringBase&lt;wchar_t&gt; data), NUL-terminated.</summary>
    internal const int StringInfoLiteral = 0x80;

    // Send: the call retail's chat box makes, so Decal and its plugins see the text as typed.
    /// <summary>ChatInterface::ProcessCommand's call to ClientCommunicationSystem::OnChatCommand (the E8 at this address + 0x16). Its live
    /// target is called, whatever Decal patched in, with (PStringBase&lt;wchar_t&gt;* text, uint sourceWindow), ThisCall, ret 8.</summary>
    internal const uint ProcessCommandSendCall = 0x004F5D95;
    internal const int ProcessCommandSendCallOpcode = 0x16;
    /// <summary>ClientCommunicationSystem::OnChatCommand, the call's target on disk. Its prologue may be patched; it is never byte-checked.</summary>
    internal const uint OnChatCommand = 0x005821A0;
    /// <summary>ClientCommunicationSystem::GetCommunicationSystem, Cdecl: returns [0x008717FC], OnChatCommand's receiver.</summary>
    internal const uint GetCommunicationSystem = 0x0056F230;
    /// <summary>PStringBase&lt;wchar_t&gt;::PStringBase(const wchar_t*), ThisCall, ret 4. Start the string as <see cref="EmptyWideString"/>.</summary>
    internal const uint WideStringFromChars = 0x00402730;
    /// <summary>PStringBase&lt;wchar_t&gt;::~PStringBase, ThisCall: releases the buffer.</summary>
    internal const uint WideStringRelease = 0x004011B0;
    /// <summary>The empty PStringBase&lt;wchar_t&gt; buffer pointer (a global holding it).</summary>
    internal const uint EmptyWideString = 0x00818340;

    // Options and membership, read from PlayerModule (CPlayerSystem 0x0087119C + 0x34) and the fellowship system.
    /// <summary>PlayerModule's CharacterOptions2 (DisplayTimeStamps bit 6; HearGeneral 8, Trade 9, LFG 10, Roleplay 11, Society 19).</summary>
    internal const int CharacterOptions2 = 0x90;
    /// <summary>PlayerModule's CharacterOptions1 (HearAllegianceChat bit 30).</summary>
    internal const int CharacterOptions1 = 0x8C;
    /// <summary>The Turbine chat rooms are up when [0x00870BE4] is set and its +0x0C is not zero (retail dims the channel targets otherwise).</summary>
    internal const uint IsUsingTurbineChat = 0x00556ED0;
    /// <summary>ClientFellowshipSystem's instance pointer; the player's fellowship is at +0x10, zero outside one.</summary>
    internal const uint FellowshipSystem = 0x0087150C;
    internal const int FellowshipOffset = 0x10;

    // Enter: whether retail's chat entry has the keyboard, and the call that takes it away.
    /// <summary>UIElementManager's active window (+0x32C); a window's focused child is at +0x1A4.</summary>
    internal const int ActiveWindowOffset = 0x32C;
    internal const int FocusedChildOffset = 0x1A4;
    /// <summary>A ChatInterface's entry field (relative to the ChatInterface, the UIElement - 4).</summary>
    internal const int ChatEntryOffset = 0x60C;
    /// <summary>ChatInterface::DeactivateChatEntry, ThisCall on the ChatInterface: drops the entry's focus and sends ToggleChatEntry(false).</summary>
    internal const uint DeactivateChatEntry = 0x004F3C20;

    internal static readonly IReadOnlyList<NativeUiEntry> Takeover = new[]
    {
        Id("MainChatWindow", MainChatWindow, $"{Dat} (placed from 0x2100006F/0x10000600)"),
        Id("MainChat", MainChat, "installed client_local_English.dat, LayoutDesc 0x21000006 root, embedded by 0x2100006F/0x10000014"),
        Id("FloatingChat1", FloatingChats[0], $"{Dat} (placed from 0x2100005B/0x100004F7)"),
        Id("FloatingChat2", FloatingChats[1], $"{Dat} (placed from 0x2100005B/0x100004F7)"),
        Id("FloatingChat3", FloatingChats[2], $"{Dat} (placed from 0x2100005B/0x100004F7)"),
        Id("FloatingChat4", FloatingChats[3], $"{Dat} (placed from 0x2100005B/0x100004F7)"),
        Entry("LoadScreenLayout maps <CHAT> to the main chat window", 0x004EB6FA, "68 01 06 00 10", "instruction reference (push 0x10000601)"),
        Entry("LoadScreenLayout maps <FCH1> to floating chat 1", 0x004EB721, "68 05 05 00 10", "instruction reference (push 0x10000505)"),
        Entry("LoadScreenLayout maps <FCH2> to floating chat 2", 0x004EB748, "68 0E 05 00 10", "instruction reference (push 0x1000050E)"),
        Entry("LoadScreenLayout maps <FCH3> to floating chat 3", 0x004EB76F, "68 0F 05 00 10", "instruction reference (push 0x1000050F)"),
        Entry("LoadScreenLayout maps <FCH4> to floating chat 4", 0x004EB796, "68 10 05 00 10", "instruction reference (push 0x10000510)")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Incoming = new[]
    {
        Entry("NoticeRegistrar getter (0x00838374)", NoticeRegistrarGetter, "A1 74 83 83 00 C3", "Cdecl () -> NoticeRegistrar*"),
        Entry("NoticeRegistrar vtable +0x04 is RegisterNoticeHandler", 0x00799FC8, "10 CA 43 00", "data reference (vtable 0x00799FC4 slot)"),
        Entry("NoticeRegistrar vtable +0x0C is UnRegisterAllNoticeHandlers", 0x00799FD0, "A0 C8 43 00", "data reference (vtable 0x00799FC4 slot)"),
        Entry("ChatInterface::PostInit registers for DisplayFinalStringInfo (0x186B6) through slot +0x04", 0x004F4B0F, "53 68 B6 86 01 00 8B CF FF 50 04",
            "instruction reference (push handler; push 0x186B6; call [eax+4])"),
        Entry("ECM_UI::SendNotice_DisplayFinalStringInfo calls handler slot +0x38 (type, text, prefix, target)", 0x006934D9,
            "8B 54 24 14 8B 01 57 53 55 52 FF 50 38", "instruction reference (the notice loop's call)"),
        Entry("NoticeHandler vtable +0x38 is the no-op DisplayFinalStringInfo", NoticeHandlerVtable + DisplayFinalStringInfoSlot, "F0 15 40 00",
            "data reference (vtable slot)"),
        Entry("NoticeHandler::RecvNotice_DisplayFinalStringInfo no-op", 0x004015F0, "C2 10 00", "ThisCall, ret 0x10"),
        Entry("ClientSystem::AddTextToScroll passes type, text, timestamp prefix and target to the notice", 0x00564CB5,
            "8B 94 24 68 09 00 00 52 8D 44 24 3C 50 8D 8C 24 D0 00 00 00 51 56", "instruction reference (the pushes before the call)"),
        Entry("StringInfo::SetLiteralValue stores the literal at +0x80", 0x0042CC34, "8B 03 89 86 80 00 00 00",
            "instruction reference (mov [esi+0x80], eax)")
    };

    internal static readonly IReadOnlyList<NativeUiEntry> Send = new[]
    {
        // Ends at the E8 opcode: its rel32 is read live, since Decal may point it at its own hook.
        Entry("ChatInterface::ProcessCommand calls OnChatCommand (text, [this+0x5FC])", ProcessCommandSendCall,
            "8B 8E 34 06 00 00 85 C9 74 11 8B 86 FC 05 00 00 50 8D 54 24 08 52 E8", "instruction reference; the call's target is read live"),
        // Past the prologue, which a hook would change.
        Entry("ClientCommunicationSystem::OnChatCommand body stores the source window at +0x8C", 0x005821E6,
            "8B 44 24 24 6A 00 8D 4C 24 24 51 8B CE 89 87 8C 00 00 00", "ThisCall (PStringBase<wchar_t>*, uint), ret 8"),
        Entry("ClientCommunicationSystem::GetCommunicationSystem", GetCommunicationSystem, "A1 FC 17 87 00 C3", "Cdecl () -> [0x008717FC]"),
        Entry("PStringBase<wchar_t>::PStringBase(const wchar_t*)", WideStringFromChars, "56 57 8B 7C 24 0C 85 FF 8B F1 74 2C 66 83 3F 00", "ThisCall, ret 4"),
        Entry("PStringBase<wchar_t>::~PStringBase", WideStringRelease, "56 8B 31 83 EE 14 8D 46 04 50 FF 15 F8 31 79 00", "ThisCall"),
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
        Entry("CCommunicationSystem::IsUsingTurbineChat ([0x00870BE4]+0x0C)", IsUsingTurbineChat, "A1 E4 0B 87 00 85 C0 75 01 C3 8B 40 0C C3", "Cdecl () -> int"),
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
        Entry("ChatInterface::DeactivateChatEntry", DeactivateChatEntry, "56 8B F1 8B 8E 0C 06 00 00 8B 01 FF 90 08 01 00 00", "ThisCall (ChatInterface*)"),
        Entry("ChatInterface::HandleEnterKey reads the active window (UIElementManager+0x32C)", 0x004F5F37, "8B B8 2C 03 00 00 85 FF 8B F1 74 5B",
            "instruction reference"),
        Entry("ChatInterface::HandleEnterKey compares the window's focused child with the entry (+0x60C)", 0x004F5F4F,
            "8B CF E8 DA A3 F6 FF 3B 86 0C 06 00 00 75 40", "instruction reference"),
        Entry("UIElement focused child (+0x1A4)", 0x00460330, "8B 81 A4 01 00 00 C3", "ThisCall () -> UIElement*")
    };

    /// <summary>Checks each feature's list on its own: a mismatch, or a check that throws, turns that feature off and logs it.</summary>
    internal static RetailChatNatives Validate(Func<uint, int, byte[]> read, Action<string> log) => new(
        Check("takeover", Takeover, read, log), Check("incoming lines", Incoming, read, log), Check("send", Send, read, log),
        Check("timestamps", Timestamp, read, log), Check("channel options", Channels, read, log), Check("fellowship", Fellowship, read, log),
        Check("Enter", Enter, read, log));

    private static bool Check(string feature, IEnumerable<NativeUiEntry> entries, Func<uint, int, byte[]> read, Action<string> log)
    {
        try
        {
            if (NativeUiCatalogue.Validate(read, log, entries)) return true;
        }
        catch (Exception exception)
        {
            log($"Dereth chat {feature}: native entries could not be checked: {exception.Message}");
            return false;
        }
        log($"Dereth chat {feature} is off: the running client does not match its native entries.");
        return false;
    }

    private static NativeUiEntry Entry(string name, uint address, string bytes, string convention) =>
        new(name, address, NativeUiCatalogue.Bytes(bytes), Source, convention);

    private static NativeUiEntry Id(string name, uint id, string source) =>
        new($"ChatElementId::{name}", null, Array.Empty<byte>(), source, "compile-time constant; not called", id);
}

/// <summary>Which chat features the running client supports. The chat window needs its takeover, incoming lines and send.</summary>
internal sealed record RetailChatNatives(bool Takeover, bool Incoming, bool Send, bool Timestamp, bool Channels, bool Fellowship, bool Enter)
{
    public bool ChatWindow => Takeover && Incoming && Send;
}

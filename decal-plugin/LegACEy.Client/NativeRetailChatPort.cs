using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's chat over the installed client, the port the Dereth chat window uses. Incoming lines come through our own notice handler on
/// retail's chat notice (0x186B6): its strings are copied and queued in the callback, and <see cref="Poll"/> raises them on the game thread
/// between frames. A send calls the live target of retail's own chat box call, so the chat parser and plugins' command handlers see it.
/// Membership comes from the native reads that were checked, and from Decal's allegiance and society facts through the bridge.
/// </summary>
/// <remarks>
/// Every address was checked against the installed acclient.exe by <see cref="RetailChatCatalogue"/>. The chat features that do not check
/// are off: their reads return what the window shows without them (timestamps on, as retail's default; room targets and the fellowship
/// target dimmed; Enter left to retail).
/// </remarks>
internal sealed class NativeRetailChatPort : IRetailChatPort, IDisposable
{
    private const uint RegistrarGetter = 0x00838374;
    private const uint PlayerSystem = 0x0087119C;
    private const uint FellowshipSystem = 0x0087150C;
    private const uint TurbineChatHolder = 0x00870BE4;
    private const uint CommunicationSystem = 0x008717FC;
    private const uint EmptyWide = 0x00818340;
    private const uint UIElementManager = 0x0083E03C;
    // The E8 that calls OnChatCommand from ChatInterface::ProcessCommand; its rel32 is read live (see Send).
    private const uint SendCallSite = 0x004F5DAB;
    private const uint NoticeChatLine = 0x186B6;
    // The notice handler's slot the notice loop calls, and the vtable copy's size: only this slot is ever called.
    private const int NoticeSlot = 0x38;
    private const int VtableBytes = 0x40;
    // The notice's text is a StringInfo whose literal sits at +0x80.
    private const int StringLiteral = 0x80;
    // The chat object's source window, read from the main chat: [ChatInterface+0x5FC]. Sends fall back to 1 when it cannot be read.
    private const int SourceWindowOffset = 0x5FC;
    private const uint DefaultSourceWindow = 1;
    // UIElementManager's keyboard focus element: SetFocusElement stores it, UIElement::GetHasFocus compares with it.
    private const int FocusElementOffset = 0x328;
    // The PlayerModule inside CPlayerSystem: CharacterOptions1 is at +0x8C of it, CharacterOptions2 at +0x90.
    private const int PlayerModule = 0x34;
    private static readonly TimeSpan TimestampCheckInterval = TimeSpan.FromSeconds(1);
    // Ports whose handler could not be unregistered: retail may still call it, so the port, its callback delegate and its memory live on.
    private static readonly List<NativeRetailChatPort> Stranded = new();

    // Retail's own lines close the tag with <\Tell>; Decal escapes a plugin's backslash, so plugin links close with <\\Tell>.
    private static readonly Regex TellTag = new(@"<Tell:IIDString:([^:>]*):([^>]*)>(.*?)<\\{1,2}Tell>", RegexOptions.Compiled);
    // Retail's text escapes: \n is a line break, \q a quote mark, and a backslash before any other character is that character (\[, \], \\).
    private static readonly Regex Escape = new(@"\\(.)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ChannelPrefix = new(@"^\[([^\]]+)\]", RegexOptions.Compiled);

    // Delegates to fixed retail functions, made once: their addresses never change.
    private static readonly DeactivateChatEntryFn DeactivateEntry = Marshal.GetDelegateForFunctionPointer<DeactivateChatEntryFn>(At(RetailChatCatalogue.DeactivateChatEntry));
    private static readonly PStringBaseFromWideFn ConstructString = Marshal.GetDelegateForFunctionPointer<PStringBaseFromWideFn>(At(0x00402730));
    private static readonly PStringBaseReleaseFn ReleaseString = Marshal.GetDelegateForFunctionPointer<PStringBaseReleaseFn>(At(0x004011B0));

    private readonly Action<string> _log;
    private readonly RetailChatNatives _natives;
    private readonly ConcurrentQueue<RetailChatLine> _pending = new();
    // The channel names already logged as matching no tab channel, so each is logged once.
    private readonly HashSet<string> _unmatchedChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly NoticeCallback _callback;
    private IntPtr _handler;
    private IntPtr _vtable;
    private bool _attached;
    private bool _timestamps = true;
    private DateTime _lastTimestampCheck = DateTime.MinValue;
    // The OnChatCommand delegate for the call site's target last read, so a send reuses it.
    private IntPtr _sendTarget;
    private OnChatCommandFn? _send;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void RegisterNoticeHandlerFn(IntPtr registrar, uint notice, IntPtr handler);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UnRegisterAllNoticeHandlersFn(IntPtr registrar, IntPtr handler);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void NoticeCallback(IntPtr handler, uint chatType, IntPtr text, IntPtr timestampPrefix, uint targetWindow);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void OnChatCommandFn(IntPtr communicationSystem, IntPtr command, uint sourceWindow);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void PStringBaseFromWideFn(IntPtr self, IntPtr text);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void PStringBaseReleaseFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void DeactivateChatEntryFn(IntPtr chat);

    /// <param name="log">Writes a line to the client's log.</param>
    /// <param name="natives">Which chat features the running client matched; a feature that did not match is off.</param>
    /// <param name="sendAction">Sends a retail game action (opcode, payload) through the client's own send; false when it was not sent.</param>
    public NativeRetailChatPort(Action<string> log, RetailChatNatives natives, Func<uint, byte[], bool> sendAction)
    {
        _sendAction = sendAction ?? throw new ArgumentNullException(nameof(sendAction));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _natives = natives;
        _callback = OnNotice;
    }

    private bool _membershipFailLogged;

    private readonly Func<uint, byte[], bool> _sendAction;

    public event Action<RetailChatLine>? LineReceived;
    public event Action? TimestampsChanged;

    /// <summary>
    /// Registers the incoming-line handler with retail's notice registrar. Called once the takeover holds retail's chat, and again after a
    /// detach. Must run on the game thread.
    /// </summary>
    public void Attach()
    {
        if (_attached) return;
        if (_vtable == IntPtr.Zero) BuildHandler();
        var registrar = Marshal.ReadIntPtr(At(RegistrarGetter));
        if (registrar == IntPtr.Zero) throw new InvalidOperationException("Retail's notice registrar is not ready.");
        NativeUi.Virtual<RegisterNoticeHandlerFn>(registrar, 0x04)(registrar, NoticeChatLine, _handler);
        _attached = true;
        _lastTimestampCheck = DateTime.UtcNow;
        var timestamps = ReadTimestamps();
        if (timestamps == _timestamps) return;
        _timestamps = timestamps;
        TimestampsChanged?.Invoke();
    }

    /// <summary>
    /// Unregisters the handler, on unload and logoff. Must run on the game thread and never from inside the callback, which the notice
    /// sender is still walking. If it throws, the handler may still be registered and <see cref="Dispose"/> keeps its memory.
    /// </summary>
    public void Detach()
    {
        if (!_attached) return;
        var registrar = Marshal.ReadIntPtr(At(RegistrarGetter));
        if (registrar != IntPtr.Zero) NativeUi.Virtual<UnRegisterAllNoticeHandlersFn>(registrar, 0x0C)(registrar, _handler);
        _attached = false;
    }

    /// <summary>Logs a channel name that no tab can pick, once, so the real names show in a play-test.</summary>
    private void NoteChannel(RetailChatLine line)
    {
        if (line.Kind != RetailChatKind.Channel || line.Channel == null) return;
        if (RetailChatTab.ChannelNames.Any(name => string.Equals(name, line.Channel, StringComparison.OrdinalIgnoreCase))) return;
        if (_unmatchedChannels.Add(line.Channel))
            _log($"Chat channel \"{line.Channel}\" matches none of the tab channel names; its lines show in All and Channels only.");
    }

    /// <summary>
    /// Raises the lines the callback queued, and the timestamp option's change, on the game thread. Called once a frame; the option is read
    /// once a second.
    /// </summary>
    public void Poll()
    {
        while (_pending.TryDequeue(out var line))
        {
            NoteChannel(line);
            LineReceived?.Invoke(line);
        }
        var now = DateTime.UtcNow;
        if (now - _lastTimestampCheck >= TimestampCheckInterval)
        {
            _lastTimestampCheck = now;
            var timestamps = ReadTimestamps();
            if (timestamps != _timestamps)
            {
                _timestamps = timestamps;
                TimestampsChanged?.Invoke();
            }
        }
        if (_natives.Enter) ReleaseRetailEntryFocus();
    }

    /// <summary>
    /// Unregisters the handler, then frees its memory. A failed unregister keeps the port alive for good, with its callback and memory,
    /// since retail may still call the handler.
    /// </summary>
    public void Dispose()
    {
        try { Detach(); }
        catch (Exception exception)
        {
            lock (Stranded) Stranded.Add(this);
            _log($"Could not unregister the retail chat handler; its memory is kept: {exception.Message}");
            return;
        }
        if (_handler != IntPtr.Zero) Marshal.FreeHGlobal(_handler);
        if (_vtable != IntPtr.Zero) Marshal.FreeHGlobal(_vtable);
        _handler = IntPtr.Zero;
        _vtable = IntPtr.Zero;
    }

    public bool TimestampsOn => _timestamps;

    public string? SelectedName
    {
        get
        {
            var id = (uint)Bridge.Fact(BridgeFact.CurrentSelection);
            return id == 0 ? null : Bridge.Selection(id)?.Name;
        }
    }

    /// <summary>
    /// What the player is in: the bridge's allegiance and society facts, the fellowship pointer and the channel options. A feature that did not
    /// match its catalogue reads as not in it, so its talk targets are dimmed.
    /// </summary>
    public RetailChatMembership Membership
    {
        get
        {
            // A membership Decal cannot read dims the targets it gates; it never fails the chat.
            int facts;
            try { facts = Bridge.Fact(BridgeFact.CharacterMembership); }
            catch (COMException exception)
            {
                if (!_membershipFailLogged) _log($"Could not read the character's allegiance and society; their talk targets are dimmed: {exception.Message}");
                _membershipFailLogged = true;
                facts = 0;
            }
            return new RetailChatMembership(
                Fellowship: _natives.Fellowship && InFellowship(),
                Allegiance: (facts & 1) != 0,
                Monarch: (facts & 2) != 0,
                Patron: (facts & 4) != 0,
                Vassals: (facts & 8) != 0,
                Society: (facts & 16) != 0,
                Olthoi: (facts & 32) != 0,
                Open: OpenChannels());
        }
    }

    /// <summary>
    /// Sends the text through retail's chat box call: its receiver is the live communication system, and the call is the one at the box's
    /// own send site, read as it is now. Returns false, sending nothing, when there is no receiver, as the box skips the send then.
    /// </summary>
    public bool TellDirect(uint objectId, string text) => _sendAction(TalkDirectAction.Opcode, TalkDirectAction.Payload(objectId, text));

    public uint SelectedId => (uint)Bridge.Fact(BridgeFact.CurrentSelection);

    public bool Send(string text)
    {
        var receiver = Marshal.ReadIntPtr(At(CommunicationSystem));
        if (receiver == IntPtr.Zero) return false;
        var target = IntPtr.Add(At(SendCallSite + 5), Marshal.ReadInt32(At(SendCallSite + 1)));
        if (target != _sendTarget)
        {
            _send = Marshal.GetDelegateForFunctionPointer<OnChatCommandFn>(target);
            _sendTarget = target;
        }

        // A PStringBase<wchar_t> starts as the empty string, then takes the text: the same steps as the box's own copy.
        var command = Marshal.AllocHGlobal(4);
        var wide = Marshal.StringToHGlobalUni(text);
        var constructed = false;
        try
        {
            Marshal.WriteIntPtr(command, Marshal.ReadIntPtr(At(EmptyWide)));
            ConstructString(command, wide);
            constructed = true;
            _send!(receiver, command, SourceWindow());
        }
        finally
        {
            if (constructed) ReleaseString(command);
            Marshal.FreeHGlobal(wide);
            Marshal.FreeHGlobal(command);
        }
        return true;
    }

    /// <summary>The handler object and its vtable: a copy of retail's own NoticeHandler vtable, with our callback in the notice slot.</summary>
    private void BuildHandler()
    {
        var copy = NativeUi.ReadMemory(RetailChatCatalogue.NoticeHandlerVtable, VtableBytes);
        _vtable = Marshal.AllocHGlobal(VtableBytes);
        Marshal.Copy(copy, 0, _vtable, VtableBytes);
        Marshal.WriteIntPtr(_vtable, NoticeSlot, Marshal.GetFunctionPointerForDelegate(_callback));
        _handler = Marshal.AllocHGlobal(4);
        Marshal.WriteIntPtr(_handler, _vtable);
    }

    /// <summary>
    /// Copies the line out of retail's strings before returning: they belong to the caller and are gone once the notice ends. Never throws
    /// into the client.
    /// </summary>
    private void OnNotice(IntPtr handler, uint chatType, IntPtr text, IntPtr timestampPrefix, uint targetWindow)
    {
        try
        {
            var kind = RetailChatTypes.KindOf(chatType);
            if (kind is not { } known) return;
            var raw = ReadWide(Marshal.ReadIntPtr(text, StringLiteral));
            _pending.Enqueue(RetailChatTypes.Parse(known, raw, DateTime.Now));
        }
        catch
        {
            // A line that cannot be read is dropped; the client keeps running.
        }
    }

    private static string ReadWide(IntPtr pointer) => pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(pointer) ?? string.Empty;

    private static bool InFellowship()
    {
        var system = Marshal.ReadIntPtr(At(FellowshipSystem));
        return system != IntPtr.Zero && Marshal.ReadIntPtr(system, 0x10) != IntPtr.Zero;
    }

    /// <summary>
    /// The rooms the player hears, from the character options: with Turbine chat down every room is off, the allegiance's and Olthoi's
    /// included, as retail's own rule says. Off when the channel option's catalogue does not match.
    /// </summary>
    private ChatChannels OpenChannels()
    {
        if (!_natives.Channels || !TurbineChatUp()) return ChatChannels.None;
        var system = Marshal.ReadIntPtr(At(PlayerSystem));
        if (system == IntPtr.Zero) return ChatChannels.None;
        var options1 = unchecked((uint)Marshal.ReadInt32(system, PlayerModule + 0x8C));
        var options2 = unchecked((uint)Marshal.ReadInt32(system, PlayerModule + 0x90));
        var open = ChatChannels.Olthoi;
        if (Bit(options2, 8)) open |= ChatChannels.General;
        if (Bit(options2, 9)) open |= ChatChannels.Trade;
        if (Bit(options2, 10)) open |= ChatChannels.Lfg;
        if (Bit(options2, 11)) open |= ChatChannels.Roleplay;
        if (Bit(options2, 19)) open |= ChatChannels.Society;
        if (Bit(options1, 30)) open |= ChatChannels.Allegiance;
        return open;
    }

    private static bool TurbineChatUp()
    {
        var holder = Marshal.ReadIntPtr(At(TurbineChatHolder));
        return holder != IntPtr.Zero && Marshal.ReadInt32(holder, 0x0C) != 0;
    }

    private bool ReadTimestamps()
    {
        if (!_natives.Timestamp) return true;
        var options = PlayerOptions2();
        return options == null || Bit(options.Value, 6);
    }

    /// <summary>CharacterOptions2 of the player, read from the PlayerModule at its options offset; null when the player is not in the world.</summary>
    private static uint? PlayerOptions2()
    {
        var system = Marshal.ReadIntPtr(At(PlayerSystem));
        if (system == IntPtr.Zero) return null;
        return unchecked((uint)Marshal.ReadInt32(system, PlayerModule + 0x90));
    }

    private static bool Bit(uint value, int bit) => ((value >> bit) & 1) != 0;

    /// <summary>
    /// The chat-window source the send carries: the main chat's [ChatInterface+0x5FC] (ChatInterface is the gmMainChatUI element less 4).
    /// Falls back to 1 when the main chat is not there or cannot be read.
    /// </summary>
    private static uint SourceWindow()
    {
        try
        {
            var main = NativeUi.GetElement(RetailChatCatalogue.MainChat);
            if (main == IntPtr.Zero) return DefaultSourceWindow;
            var source = Marshal.ReadInt32(IntPtr.Add(main, -4), SourceWindowOffset);
            return source == 0 ? DefaultSourceWindow : unchecked((uint)source);
        }
        catch (Exception)
        {
            return DefaultSourceWindow;
        }
    }

    /// <summary>
    /// Whether a retail element other than the hidden chat entry has the keyboard (a split amount, a name box): Enter is then that field's.
    /// False when the Enter entries did not match, as Enter is not taken then.
    /// </summary>
    public bool RetailFieldHasKeyboard()
    {
        if (!_natives.Enter) return false;
        var manager = Marshal.ReadIntPtr(At(UIElementManager));
        if (manager == IntPtr.Zero) return false;
        var focused = Marshal.ReadIntPtr(manager, FocusElementOffset);
        if (focused == IntPtr.Zero) return false;
        var main = NativeUi.GetElement(RetailChatCatalogue.MainChat);
        return main == IntPtr.Zero || Marshal.ReadIntPtr(IntPtr.Add(main, -4), 0x60C) != focused;
    }

    /// <summary>
    /// Retail's hidden chat entry keeps the keyboard when the player presses Enter while retail's chat is held. Each frame it is let go, so
    /// the typed keys reach our input. The chat object is the gmMainChatUI element less its UIElement offset of 4.
    /// </summary>
    private static void ReleaseRetailEntryFocus()
    {
        var main = NativeUi.GetElement(RetailChatCatalogue.MainChat);
        if (main == IntPtr.Zero) return;
        var chat = IntPtr.Add(main, -4);
        var entry = Marshal.ReadIntPtr(chat, 0x60C);
        var manager = Marshal.ReadIntPtr(At(UIElementManager));
        if (manager == IntPtr.Zero || entry == IntPtr.Zero) return;
        var window = Marshal.ReadIntPtr(manager, 0x32C);
        if (window == IntPtr.Zero || Marshal.ReadIntPtr(window, 0x1A4) != entry) return;
        DeactivateEntry(chat);
    }

    private static IntPtr At(uint address) => new(unchecked((int)address));

    /// <summary>The chat type's entry in the table of kinds: the spike note's section 3. Plugin lines have no type of their own.</summary>
    internal static class RetailChatTypes
    {
        /// <summary>The kind retail's chat type stands for, or null for a type the main chat does not show (errors, type 26).</summary>
        public static RetailChatKind? KindOf(uint chatType) => chatType switch
        {
            2 => RetailChatKind.Local,
            12 => RetailChatKind.Emote,
            3 or 31 => RetailChatKind.TellIn,
            4 => RetailChatKind.TellOut,
            19 => RetailChatKind.Fellowship,
            18 or 10 or 11 => RetailChatKind.Allegiance,
            8 or 9 or 27 or 28 or 29 or 30 or 32 => RetailChatKind.Channel,
            6 or 7 or 17 or 21 or 22 => RetailChatKind.Combat,
            26 => null,
            _ => RetailChatKind.System
        };

        /// <summary>
        /// A line from the notice's text: the sender's tag becomes the plain name, and the sender and the channel are read from the text. An
        /// Allegiance-typed line that names Olthoi is an Olthoi room line.
        /// </summary>
        public static RetailChatLine Parse(RetailChatKind kind, string raw, DateTime time)
        {
            string? sender = null;
            uint senderId = 0;
            var text = TellTag.Replace(raw, match =>
            {
                // A player's tag names them twice; a plugin's link (GoArrow's "Update Now") names a command, not a sender.
                if (match.Groups[2].Value == match.Groups[3].Value && sender == null)
                {
                    sender = match.Groups[3].Value;
                    // Retail writes the id with %d, so an id past 0x7FFFFFFF is negative.
                    if (long.TryParse(match.Groups[1].Value, out var id)) senderId = unchecked((uint)id);
                }
                return match.Groups[3].Value;
            });
            text = Escape.Replace(text, match => match.Groups[1].Value switch { "n" => "\n", "q" => "\"", var other => other });
            var channel = ChannelPrefix.Match(text) is { Success: true } prefix ? prefix.Groups[1].Value : null;
            if (channel == "Olthoi") kind = RetailChatKind.Channel;
            return new RetailChatLine(kind, text, time, sender, channel, senderId);
        }
    }
}

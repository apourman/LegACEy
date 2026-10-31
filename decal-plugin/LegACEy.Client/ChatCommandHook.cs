using System;
using System.Collections.Generic;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X86;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The lines the player enters in chat, from retail itself: ClientCommunicationSystem::OnChatCommand takes every line before retail runs
/// it, whether it is said, told or a command such as /saveui. A line the client takes is eaten, so retail never runs or sends it. RynthCore's
/// ChatCallbackHooks catches the same function.
/// </summary>
internal sealed unsafe class ChatCommandHook : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build) for names; our objdump read of installed acclient.exe";
    private const uint OnChatCommand = 0x005821A0;

    // Retail's first 14 bytes: the prologue and a call. Decal patches them in the running client to catch chat lines itself, so they are
    // only logged when changed, and the hook goes on top of Decal's: LegACEy sees each line first.
    private static readonly byte[] RetailStart = NativeUiCatalogue.Bytes("83 EC 0C 53 55 56 57 8B F9 E8 F2 8E FD FF");

    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("ClientCommunicationSystem::OnChatCommand body: the line's text read by SysAllocString", OnChatCommand + 0x0E,
            NativeUiCatalogue.Bytes("85 C0 8B 74 24 20 74 30 8B 06 50 FF 15 FC 33 79 00"), Source,
            "ThisCall (ClientCommunicationSystem*, PStringBase<wchar_t>*, source) -> bool, ret 8; the string's buffer is its text"),
    };

    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate byte OnChatCommandFn(IntPtr system, IntPtr text, uint source);

    private readonly Func<string, bool> _command;
    private readonly Action<Exception> _failed;
    private readonly Action<string> _log;
    private IHook<OnChatCommandFn>? _hook;

    private ChatCommandHook(Func<string, bool> command, Action<Exception> failed, Action<string> log)
    {
        _command = command;
        _failed = failed;
        _log = log;
    }

    /// <summary>The hook in place, or null with the reason logged when the running client doesn't match.</summary>
    public static ChatCommandHook? Place(Func<string, bool> command, Action<Exception> failed, Action<string> log)
    {
        try
        {
            if (NativeUi.Ready && NativeUi.ReadMemory(OnChatCommand, RetailStart.Length) is var start && !start.AsSpan().SequenceEqual(RetailStart))
                log($"ClientCommunicationSystem::OnChatCommand starts {Convert.ToHexString(start)}, not retail's: LegACEy's hook goes on top.");
        }
        catch (InvalidOperationException exception) { log(exception.Message); }
        var hook = new ChatCommandHook(command, failed, log);
        hook._hook = RetailHook.Place<OnChatCommandFn>(OnChatCommand, hook.OnCommand, Entries,
            "LegACEy's chat commands are off: the running client does not match the chat command hook's entries.", log);
        return hook._hook != null ? hook : null;
    }

    private byte OnCommand(IntPtr system, IntPtr text, uint source)
    {
        try
        {
            var chars = text == IntPtr.Zero ? null : *(char**)text;
            if (chars != null && _command(new string(chars))) return 1;
        }
        catch (Exception exception)
        {
            try { _failed(exception); }
            catch { /* Reporting must never escape the native callback. */ }
        }
        return _hook!.OriginalFunction(system, text, source);
    }

    public void Dispose() => RetailHook.Remove(_hook, "chat command", _log);
}

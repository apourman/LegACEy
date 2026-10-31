using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        // The prologue is left out: Decal replaces its first 5 bytes with a jump to its own hook.
        new NativeUiEntry("ClientCommunicationSystem::OnChatCommand body after the prologue", OnChatCommand + 5,
            NativeUiCatalogue.Bytes("56 57 8B F9 E8 F2 8E FD FF 85 C0 8B 74 24 20 74 30 8B 06 50 FF 15 FC 33 79 00"), Source,
            "ThisCall (ClientCommunicationSystem*, PStringBase<wchar_t>*, source) -> bool, ret 8; the string's buffer, read by SysAllocString, is its text"),
    };

    private static readonly NativeUiEntry Prologue = new("ClientCommunicationSystem::OnChatCommand prologue, with no other hook on it",
        OnChatCommand, NativeUiCatalogue.Bytes("83 EC 0C 53 55"), Source, "the bytes Reloaded moves");

    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate byte OnChatCommandFn(IntPtr system, IntPtr text, uint source);

    // The hook over another hook's jump, which retail reaches with no instance of its own.
    private static ChatCommandHook? _overJump;

    private readonly Func<string, bool> _command;
    private readonly Action<Exception> _failed;
    private readonly Action<string> _log;
    private IHook<OnChatCommandFn>? _hook;
    // The other hook's function, when this hook took its jump.
    private delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, byte> _next;

    private ChatCommandHook(Func<string, bool> command, Action<Exception> failed, Action<string> log)
    {
        _command = command;
        _failed = failed;
        _log = log;
    }

    /// <summary>The hook in place, or null with the reason logged when the running client doesn't match.</summary>
    public static ChatCommandHook? Place(Func<string, bool> command, Action<Exception> failed, Action<string> log)
    {
        const string off = "LegACEy's chat commands are off: the running client does not match the chat command hook's entries.";
        var hook = new ChatCommandHook(command, failed, log);
        if (!NativeUi.Ready) return null;
        try
        {
            if (!NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries, off)) return null;
            _overJump = hook;
            if (RetailHook.TakeJump(OnChatCommand, (IntPtr)(delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, byte>)&OnCommandOverJump) is { } next)
            {
                hook._next = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, byte>)next;
                log($"LegACEy's chat command hook went in front of the hook at 0x{next:X8}.");
                return hook;
            }
            _overJump = null;
            hook._hook = RetailHook.Place<OnChatCommandFn>(OnChatCommand, hook.OnCommand, new[] { Prologue }, off, log);
            return hook._hook != null ? hook : null;
        }
        catch (InvalidOperationException exception)
        {
            _overJump = null;
            log($"{off} {exception.Message}");
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static byte OnCommandOverJump(IntPtr system, IntPtr text, uint source) => _overJump!.OnCommand(system, text, source);

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
        return _hook != null ? _hook.OriginalFunction(system, text, source) : _next(system, text, source);
    }

    public void Dispose()
    {
        RetailHook.Remove(_hook, "chat command", _log);
        if (_next == null) return;
        // A jump still leading here keeps the hook, which forwards, reachable.
        if (RetailHook.GiveJumpBack(OnChatCommand, (IntPtr)(delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, byte>)&OnCommandOverJump, (IntPtr)_next, "chat command", _log) && _overJump == this) _overJump = null;
    }
}

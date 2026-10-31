using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X86;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The client's frame, from retail itself: SceneTool::EndFrame ends every frame Client::UseTime draws, from character select on, and draws
/// retail's UI (RenderUI::RenderObjects) before it ends the scene. The frame runs on entry, before that UI, where Decal's RenderFrame ran: a
/// retail panel the frame hides in answer to a key is gone before retail draws it. The post-UI draw stays at the EndScene that follows.
/// </summary>
internal sealed unsafe class FrameHook : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build) for names; our objdump read of installed acclient.exe";
    private const uint EndFrame = 0x0043FCD0;

    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        // The prologue is left out: another hook may have replaced its first bytes with a jump.
        new NativeUiEntry("SceneTool::EndFrame body after the prologue", EndFrame + 6,
            NativeUiCatalogue.Bytes("8A 81 AC 00 00 00 83 EC 0C 84 C0 0F 84 D8 00 00 00"), Source,
            "Cdecl (bool drawUi), called with 1 by Client::UseTime 0x0041200D and Client::KeepUIAlive 0x0041168D"),
        new NativeUiEntry("SceneTool::EndFrame draws retail's UI through RenderUI::RenderObjects", 0x0043FD47,
            NativeUiCatalogue.Bytes("E8 54 8B 00 00"), Source, "call reference"),
    };

    private static readonly NativeUiEntry Prologue = new("SceneTool::EndFrame prologue, with no other hook on it", EndFrame,
        NativeUiCatalogue.Bytes("8B 0D 40 03 87 00"), Source, "the bytes Reloaded moves: mov ecx, [the render device]");

    [Function(CallingConventions.Cdecl)]
    private delegate void EndFrameFn(int drawUi);

    // The hook over another hook's jump, which retail reaches with no instance of its own.
    private static FrameHook? _overJump;

    private readonly Action _frame;
    private readonly Action<Exception> _failed;
    private readonly Action<string> _log;
    private IHook<EndFrameFn>? _hook;
    private delegate* unmanaged[Cdecl]<int, void> _next;

    private FrameHook(Action frame, Action<Exception> failed, Action<string> log)
    {
        _frame = frame;
        _failed = failed;
        _log = log;
    }

    /// <summary>The hook in place, or null with the reason logged when the running client doesn't match.</summary>
    public static FrameHook? Place(Action frame, Action<Exception> failed, Action<string> log)
    {
        const string off = "LegACEy is off: the running client does not match the frame hook's entries.";
        var hook = new FrameHook(frame, failed, log);
        if (!NativeUi.Ready) return null;
        try
        {
            if (!NativeUiCatalogue.Validate(NativeUi.ReadMemory, log, Entries, off)) return null;
            _overJump = hook;
            if (RetailHook.TakeJump(EndFrame, (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&OnEndFrameOverJump) is { } next)
            {
                hook._next = (delegate* unmanaged[Cdecl]<int, void>)next;
                log($"LegACEy's frame hook went in front of the hook at 0x{next:X8}.");
                return hook;
            }
            _overJump = null;
            hook._hook = RetailHook.Place<EndFrameFn>(EndFrame, hook.OnEndFrame, new[] { Prologue }, off, log);
            return hook._hook != null ? hook : null;
        }
        catch (InvalidOperationException exception)
        {
            _overJump = null;
            log($"{off} {exception.Message}");
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnEndFrameOverJump(int drawUi) => _overJump!.OnEndFrame(drawUi);

    private void OnEndFrame(int drawUi)
    {
        try { _frame(); }
        catch (Exception exception)
        {
            try { _failed(exception); }
            catch { /* Reporting must never escape the native callback. */ }
        }
        if (_hook != null) _hook.OriginalFunction(drawUi);
        else _next(drawUi);
    }

    public void Dispose()
    {
        RetailHook.Remove(_hook, "frame", _log);
        if (_next == null) return;
        // A jump still leading here keeps the hook, which forwards, reachable.
        if (RetailHook.GiveJumpBack(EndFrame, (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&OnEndFrameOverJump, (IntPtr)_next, "frame", _log) && _overJump == this) _overJump = null;
    }
}

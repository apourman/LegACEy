using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Retail's text mode while a LegACEy text box has the keyboard. Retail reads its key bindings (Enter opens chat, W runs) through DirectInput,
/// which eating the window messages does not stop. Its own text elements turn <c>CInputManager</c>'s text mode on when they gain focus and off
/// when they lose it (UIElement_Text::ListenToElementMessage, message 0x2F), so keys type instead of acting. <see cref="Hold"/> does the
/// same for ours, through the input manager's own SetTextMode slot. Call on the game thread.
/// </summary>
internal sealed class RetailTextMode
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build; +0x1A0 at 0x00431xxx, +0x290 at 0x00468xxx) for names; our objdump read of installed acclient.exe";
    private static readonly IntPtr InputManagerInstance = new(0x00837FF4);
    private const int SetTextModeSlot = 0xA8, TextModeOffset = 0x1A;

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        // Past the prologue, which a hook would change: compare with +0x1A, store it, and call OnSwitchTextMode (vtable +0xB4).
        new NativeUiEntry("CInputManager::SetTextMode body", 0x00431597, NativeUiCatalogue.Bytes("3A 46 1A 74 0E 8B 16 50 8B CE 88 46 1A FF 92 B4 00 00 00"),
            Source, "ThisCall (bool): the text mode byte at +0x1A"),
        new NativeUiEntry("UIElement_Text gaining focus turns text mode on", 0x00468AC0, NativeUiCatalogue.Bytes("8B 0D F4 7F 83 00 8B 01 6A 01 FF 90 A8 00 00 00"),
            Source, "the retail pattern followed here: the input manager at 0x00837FF4, SetTextMode at vtable +0xA8"),
        new NativeUiEntry("UIElement_Text losing focus turns text mode off", 0x00468AED, NativeUiCatalogue.Bytes("8B 0D F4 7F 83 00 8B 11 6A 00 FF 92 A8 00 00 00"),
            Source, "the retail pattern followed here")
    };

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetTextModeFn(IntPtr inputManager, [MarshalAs(UnmanagedType.U1)] bool on);

    private readonly bool _ready;
    // The input manager whose text mode we turned on, or zero when we hold none.
    private IntPtr _held;

    public RetailTextMode(Func<uint, int, byte[]> readMemory, Action<string> log)
    {
        _ready = NativeUiCatalogue.Validate(readMemory, log, Entries);
        if (!_ready) log("Keys typed in LegACEy text boxes also reach the game: the running client does not match its text mode entries.");
    }

    /// <summary>Holds text mode on while <paramref name="typing"/> is true, and turns it off when it turns false. Call each frame.</summary>
    public void Hold(bool typing)
    {
        if (!_ready) return;
        var manager = Marshal.ReadIntPtr(InputManagerInstance);
        if (typing && _held == IntPtr.Zero)
        {
            // A retail field that has text mode on already keeps it; we take nothing to give back.
            if (manager == IntPtr.Zero || Marshal.ReadByte(manager, TextModeOffset) != 0) return;
            Set(manager, true);
            _held = manager;
        }
        else if (!typing && _held != IntPtr.Zero)
        {
            // Given back to the same input manager it was taken from.
            if (manager == _held) Set(manager, false);
            _held = IntPtr.Zero;
        }
    }

    private static void Set(IntPtr manager, bool on)
    {
        var setTextMode = Marshal.ReadIntPtr(Marshal.ReadIntPtr(manager), SetTextModeSlot);
        Marshal.GetDelegateForFunctionPointer<SetTextModeFn>(setTextMode)(manager, on);
    }
}

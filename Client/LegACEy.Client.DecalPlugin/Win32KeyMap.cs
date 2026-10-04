using Avalonia.Input;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Maps Win32 virtual keys to the keys used by Avalonia controls.</summary>
internal static class Win32KeyMap
{
    internal static Key ToAvaloniaKey(int virtualKey)
    {
        if (virtualKey >= 'A' && virtualKey <= 'Z')
            return (Key)((int)Key.A + virtualKey - 'A');
        if (virtualKey >= '0' && virtualKey <= '9')
            return (Key)((int)Key.D0 + virtualKey - '0');
        return virtualKey switch
        {
            0x08 => Key.Back,
            0x09 => Key.Tab,
            0x0d => Key.Enter,
            0x1b => Key.Escape,
            0x20 => Key.Space,
            0x23 => Key.End,
            0x24 => Key.Home,
            0x25 => Key.Left,
            0x26 => Key.Up,
            0x27 => Key.Right,
            0x28 => Key.Down,
            0x2e => Key.Delete,
            0x10 or 0xA0 or 0xA1 => Key.LeftShift,
            0x11 or 0xA2 or 0xA3 => Key.LeftCtrl,
            0x12 or 0xA4 or 0xA5 => Key.LeftAlt,
            _ => Key.None
        };
    }
}

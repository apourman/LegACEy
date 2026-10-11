using System;

namespace LegACEy.Client.InputRouter;

/// <summary>A raw Win32 message.</summary>
public readonly struct NativeInputMessage
{
    public NativeInputMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        Message = message;
        WParam = wParam;
        LParam = lParam;
    }

    public int Message { get; }
    public IntPtr WParam { get; }
    public IntPtr LParam { get; }
}

public enum InputAction
{
    None,
    PointerMove,
    PointerDown,
    PointerUp,
    RightClick,
    MouseWheel,
    KeyDown,
    KeyUp,
    TextInput,
    ClearFocus,
    Reset
}

[Flags]
public enum InputModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Meta = 8
}

/// <summary>The action for the host to perform and the input-suppression decision for Decal.</summary>
public sealed class InputRoute
{
    internal InputRoute(InputAction action = InputAction.None, string? surfaceId = null, bool eat = false,
        int x = 0, int y = 0, int wheelDelta = 0, int keyCode = 0, InputModifiers modifiers = InputModifiers.None,
        string? releaseCaptureSurfaceId = null, string? clearFocusSurfaceId = null)
    {
        Action = action;
        SurfaceId = surfaceId;
        Eat = eat;
        X = x;
        Y = y;
        WheelDelta = wheelDelta;
        KeyCode = keyCode;
        Modifiers = modifiers;
        ReleaseCaptureSurfaceId = releaseCaptureSurfaceId;
        ClearFocusSurfaceId = clearFocusSurfaceId;
    }

    public InputAction Action { get; }
    public string? SurfaceId { get; }
    public bool Eat { get; }
    public int X { get; }
    public int Y { get; }
    public int WheelDelta { get; }
    public int KeyCode { get; }
    public InputModifiers Modifiers { get; }
    public string? ReleaseCaptureSurfaceId { get; }
    public string? ClearFocusSurfaceId { get; }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.InputRouter;

/// <summary>Routes Win32 input to visible Avalonia surfaces without depending on Decal or Direct3D.</summary>
public sealed class InputRouterService
{
    public const int WmActivate = 0x0006;
    public const int WmKillFocus = 0x0008;
    public const int WmMouseMove = 0x0200;
    public const int WmLButtonDown = 0x0201;
    public const int WmLButtonUp = 0x0202;
    public const int WmLButtonDoubleClick = 0x0203;
    public const int WmRButtonDown = 0x0204;
    public const int WmRButtonUp = 0x0205;
    public const int WmMouseWheel = 0x020A;
    public const int WmKeyDown = 0x0100;
    public const int WmKeyUp = 0x0101;
    public const int WmChar = 0x0102;
    public const int WmSysKeyDown = 0x0104;
    public const int WmSysKeyUp = 0x0105;
    public const int WmSysChar = 0x0106;
    public const int WmLogoff = 0x0016;

    private string? _capturedSurfaceId;
    private string? _focusedSurfaceId;
    private InputModifiers _modifiers;

    public string? CapturedSurfaceId => _capturedSurfaceId;
    public string? FocusedSurfaceId => _focusedSurfaceId;

    public InputRoute Route(NativeInputMessage message, IReadOnlyList<InputSurface> surfaces)
    {
        if (surfaces == null) throw new ArgumentNullException(nameof(surfaces));

        if (message.Message == WmKillFocus || message.Message == WmLogoff ||
            (message.Message == WmActivate && (message.WParam.ToInt64() & 0xffff) == 0))
            return Reset();

        var code = message.Message;
        if (code == WmMouseMove || code == WmLButtonDown || code == WmLButtonUp ||
            code == WmLButtonDoubleClick || code == WmRButtonDown || code == WmRButtonUp || code == WmMouseWheel)
            return RouteMouse(message, surfaces);

        if (code == WmKeyDown || code == WmSysKeyDown)
            return RouteKey(message, surfaces, InputAction.KeyDown, keyDown: true);
        if (code == WmKeyUp || code == WmSysKeyUp)
            return RouteKey(message, surfaces, InputAction.KeyUp, keyDown: false);
        if (code == WmChar || code == WmSysChar)
        {
            var target = FindFocusedKeyboardSurface(surfaces);
            if (target == null) return new InputRoute();
            // Editing keys and shortcuts are handled by key-down; their character messages
            // must still be eaten, but must not insert control characters into the text.
            if (char.IsControl(unchecked((char)message.WParam.ToInt64())))
                return new InputRoute(eat: true);
            return new InputRoute(InputAction.TextInput, target.Id, true,
                keyCode: unchecked((int)message.WParam.ToInt64()), modifiers: _modifiers);
        }

        return new InputRoute();
    }

    private InputRoute RouteMouse(NativeInputMessage message, IReadOnlyList<InputSurface> surfaces)
    {
        var isWheel = message.Message == WmMouseWheel;
        var x = LowSigned(message.LParam);
        var y = HighSigned(message.LParam);
        var target = message.Message == WmMouseMove && _capturedSurfaceId != null
            ? Find(_capturedSurfaceId, surfaces)
            : _capturedSurfaceId != null && (message.Message == WmLButtonUp || message.Message == WmRButtonUp)
                ? Find(_capturedSurfaceId, surfaces)
                : HitTest(x, y, surfaces);

        if (isWheel)
        {
            var delta = HighSigned(message.WParam);
            return target == null ? new InputRoute() : new InputRoute(InputAction.MouseWheel, target.Id, true,
                x - target.X, y - target.Y, delta, modifiers: RouteModifiers(message.WParam));
        }

        if (message.Message == WmMouseMove)
            return target == null ? new InputRoute() : new InputRoute(InputAction.PointerMove, target.Id, false, x - target.X, y - target.Y);

        if (message.Message == WmLButtonDown || message.Message == WmLButtonDoubleClick || message.Message == WmRButtonDown)
        {
            if (target == null)
            {
                var previous = _focusedSurfaceId;
                _focusedSurfaceId = null;
                return previous == null ? new InputRoute() : new InputRoute(InputAction.ClearFocus, clearFocusSurfaceId: previous);
            }
            if (message.Message == WmRButtonDown)
                return new InputRoute(eat: true);
            var left = _focusedSurfaceId != target.Id ? _focusedSurfaceId : null;
            _capturedSurfaceId = target.Id;
            _focusedSurfaceId = target.Id;
            return new InputRoute(InputAction.PointerDown, target.Id, true, x - target.X, y - target.Y, modifiers: RouteModifiers(message.WParam),
                clearFocusSurfaceId: left);
        }

        if (message.Message == WmLButtonUp)
        {
            if (_capturedSurfaceId == null) return new InputRoute();
            var captured = Find(_capturedSurfaceId, surfaces);
            _capturedSurfaceId = null;
            return captured == null ? new InputRoute() : new InputRoute(InputAction.PointerUp, captured.Id, true, x - captured.X, y - captured.Y);
        }

        return target == null ? new InputRoute() : new InputRoute(eat: true);
    }

    private InputRoute RouteKey(NativeInputMessage message, IReadOnlyList<InputSurface> surfaces, InputAction action, bool keyDown)
    {
        var vk = unchecked((int)message.WParam.ToInt64());
        UpdateModifiers(vk, keyDown);
        var target = FindFocusedKeyboardSurface(surfaces);
        if (target == null) return new InputRoute();
        return new InputRoute(action, target.Id, true, keyCode: vk, modifiers: _modifiers);
    }

    private InputSurface? FindFocusedKeyboardSurface(IReadOnlyList<InputSurface> surfaces)
    {
        if (_focusedSurfaceId == null) return null;
        var surface = Find(_focusedSurfaceId, surfaces);
        return surface?.WantsKeyboard == true ? surface : null;
    }

    private InputRoute Reset()
    {
        var captured = _capturedSurfaceId;
        var focused = _focusedSurfaceId;
        _capturedSurfaceId = null;
        _focusedSurfaceId = null;
        _modifiers = InputModifiers.None;
        return new InputRoute(InputAction.Reset, releaseCaptureSurfaceId: captured, clearFocusSurfaceId: focused);
    }

    private static InputSurface? HitTest(int x, int y, IReadOnlyList<InputSurface> surfaces) =>
        surfaces.Where(surface => surface.Contains(x, y)).OrderByDescending(surface => surface.ZOrder).FirstOrDefault();

    /// <summary>The id of the surface a point routes to when nothing has captured the pointer, or null over no surface.</summary>
    public static string? SurfaceAt(int x, int y, IReadOnlyList<InputSurface> surfaces) => HitTest(x, y, surfaces)?.Id;

    private static InputSurface? Find(string id, IReadOnlyList<InputSurface> surfaces) =>
        surfaces.FirstOrDefault(surface => string.Equals(surface.Id, id, StringComparison.Ordinal));

    private void UpdateModifiers(int key, bool down)
    {
        var modifier = key switch
        {
            0x10 or 0xA0 or 0xA1 => InputModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => InputModifiers.Control,
            0x12 or 0xA4 or 0xA5 => InputModifiers.Alt,
            0x5B or 0x5C => InputModifiers.Meta,
            _ => InputModifiers.None
        };
        if (down) _modifiers |= modifier;
        else _modifiers &= ~modifier;
    }

    private static int LowSigned(IntPtr packed) => unchecked((short)(packed.ToInt64() & 0xffff));
    private static int HighSigned(IntPtr packed) => unchecked((short)((packed.ToInt64() >> 16) & 0xffff));

    /// <summary>
    /// A mouse message's modifiers. Shift and Control come only from its key state, which is authoritative, so a key-up the
    /// router missed cannot stick. Alt and Meta have no key state in the message, so they come from the keys seen.
    /// </summary>
    private InputModifiers RouteModifiers(IntPtr wParam) => (_modifiers & (InputModifiers.Alt | InputModifiers.Meta)) | MouseKeyModifiers(wParam);

    /// <summary>The Shift and Control held, from a mouse message's key state (MK_SHIFT and MK_CONTROL in the low word).</summary>
    private static InputModifiers MouseKeyModifiers(IntPtr wParam)
    {
        var keyState = unchecked((int)wParam.ToInt64()) & 0xffff;
        var modifiers = InputModifiers.None;
        if ((keyState & 0x0004) != 0) modifiers |= InputModifiers.Shift;
        if ((keyState & 0x0008) != 0) modifiers |= InputModifiers.Control;
        return modifiers;
    }
}

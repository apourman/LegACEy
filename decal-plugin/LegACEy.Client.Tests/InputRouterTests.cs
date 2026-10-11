using LegACEy.Client.InputRouter;

namespace LegACEy.Client.Tests;

public sealed class InputRouterTests
{
    [Fact]
    public void Press_uses_topmost_surface_and_release_returns_to_captured_surface()
    {
        var router = new InputRouterService();
        var surfaces = new[] { Surface("back", 0, 0), Surface("front", 1, 10) };

        var down = router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(15, 15)), surfaces);
        var up = router.Route(new NativeInputMessage(InputRouterService.WmLButtonUp, 0, Pack(90, 90)), surfaces);

        Assert.Equal("front", down.SurfaceId);
        Assert.True(down.Eat);
        Assert.Equal(InputAction.PointerDown, down.Action);
        Assert.Equal("front", up.SurfaceId);
        Assert.True(up.Eat);
        Assert.Equal(InputAction.PointerUp, up.Action);
    }

    [Fact]
    public void A_right_press_over_a_surface_is_a_right_click_there_and_takes_no_capture()
    {
        var router = new InputRouterService();
        var surfaces = new[] { Surface("back", 0, 0), Surface("front", 1, 10) };

        var down = router.Route(new NativeInputMessage(InputRouterService.WmRButtonDown, 0, Pack(15, 15)), surfaces);
        var move = router.Route(new NativeInputMessage(InputRouterService.WmMouseMove, 0, Pack(5, 5)), surfaces);

        Assert.Equal(InputAction.RightClick, down.Action);
        Assert.Equal("front", down.SurfaceId);
        Assert.True(down.Eat);
        Assert.Equal("back", move.SurfaceId);
    }

    [Fact]
    public void Key_messages_are_eaten_and_routed_only_when_the_focused_surface_wants_keyboard()
    {
        var router = new InputRouterService();
        var surfaces = new[] { Surface("panel", 0, 0, wantsKeyboard: true) };
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(20, 20)), surfaces);
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonUp, 0, Pack(20, 20)), surfaces);

        var keyDown = router.Route(new NativeInputMessage(InputRouterService.WmKeyDown, (nint)0x41, 0), surfaces);
        var sysKeyUp = router.Route(new NativeInputMessage(InputRouterService.WmSysKeyUp, (nint)0x41, 0), surfaces);
        var charInput = router.Route(new NativeInputMessage(InputRouterService.WmChar, (nint)'a', 0), surfaces);

        Assert.Equal(InputAction.KeyDown, keyDown.Action);
        Assert.True(keyDown.Eat);
        Assert.Equal(InputAction.KeyUp, sysKeyUp.Action);
        Assert.True(sysKeyUp.Eat);
        Assert.Equal(InputAction.TextInput, charInput.Action);
        Assert.True(charInput.Eat);
    }

    [Fact]
    public void Wheel_uses_screen_coordinates_and_finds_the_top_surface()
    {
        var router = new InputRouterService();
        var surface = new InputSurface("panel", 100, 200, 80, 60, 0);
        var message = new NativeInputMessage(InputRouterService.WmMouseWheel, (nint)(((int)120 << 16) | 0x0004), Pack(110, 210));

        var result = router.Route(message, new[] { surface });

        Assert.Equal("panel", result.SurfaceId);
        Assert.Equal(InputAction.MouseWheel, result.Action);
        Assert.Equal(120, result.WheelDelta);
        Assert.Equal(InputModifiers.Shift, result.Modifiers);
        Assert.Equal(10, result.X);
        Assert.Equal(10, result.Y);
        Assert.True(result.Eat);
    }

    [Fact]
    public void Outside_press_clears_focus_and_deactivation_or_logoff_releases_capture_and_focus()
    {
        var router = new InputRouterService();
        var surfaces = new[] { Surface("panel", 0, 0, wantsKeyboard: false) };
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(10, 10)), surfaces);
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonUp, 0, Pack(10, 10)), surfaces);
        var outside = router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(200, 200)), surfaces);
        Assert.Equal(InputAction.ClearFocus, outside.Action);
        Assert.Equal("panel", outside.ClearFocusSurfaceId);
        Assert.False(outside.Eat);

        router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(10, 10)), surfaces);
        var lostFocus = router.Route(new NativeInputMessage(InputRouterService.WmKillFocus, 0, 0), surfaces);
        Assert.Equal(InputAction.Reset, lostFocus.Action);
        Assert.Equal("panel", lostFocus.ReleaseCaptureSurfaceId);
        Assert.Equal("panel", lostFocus.ClearFocusSurfaceId);

        var noLongerCaptured = router.Route(new NativeInputMessage(InputRouterService.WmLButtonUp, 0, Pack(10, 10)), surfaces);
        Assert.Equal(InputAction.None, noLongerCaptured.Action);
    }

    [Fact]
    public void Surface_focus_is_not_keyboard_focus_until_the_host_reports_text_focus()
    {
        var router = new InputRouterService();
        var surfaces = new[] { Surface("panel", 0, 0) };
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(10, 10)), surfaces);
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonUp, 0, Pack(10, 10)), surfaces);

        var ordinaryKey = router.Route(new NativeInputMessage(InputRouterService.WmKeyDown, (nint)0x57, 0), surfaces);
        var textSurface = Surface("panel", 0, 0, wantsKeyboard: true);
        var textKey = router.Route(new NativeInputMessage(InputRouterService.WmKeyDown, (nint)0x57, 0), new[] { textSurface });

        Assert.False(ordinaryKey.Eat);
        Assert.True(textKey.Eat);
    }

    [Fact]
    public void System_keys_preserve_modifier_state_and_inactive_activation_resets_it()
    {
        var router = new InputRouterService();
        var surfaces = new[] { Surface("panel", 0, 0, wantsKeyboard: true) };
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(10, 10)), surfaces);
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonUp, 0, Pack(10, 10)), surfaces);

        var controlDown = router.Route(new NativeInputMessage(InputRouterService.WmKeyDown, (nint)0x11, 0), surfaces);
        var systemKey = router.Route(new NativeInputMessage(InputRouterService.WmSysKeyDown, (nint)0x41, 0), surfaces);
        var inactive = router.Route(new NativeInputMessage(InputRouterService.WmActivate, IntPtr.Zero, IntPtr.Zero), surfaces);

        Assert.Equal(InputModifiers.Control, controlDown.Modifiers);
        Assert.Equal(InputModifiers.Control, systemKey.Modifiers);
        Assert.Equal(InputAction.Reset, inactive.Action);
        Assert.Null(router.FocusedSurfaceId);
        Assert.Equal(InputModifiers.None, router.Route(new NativeInputMessage(InputRouterService.WmKeyDown, (nint)0x41, 0), surfaces).Modifiers);
    }

    [Fact]
    public void A_left_press_carries_the_Ctrl_and_Shift_held_with_it()
    {
        var router = new InputRouterService();
        var surfaces = new[] { Surface("panel", 0) };
        // The press's low word is the mouse key state: MK_SHIFT (0x4) and MK_CONTROL (0x8).
        var press = router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, (nint)0x0c, Pack(10, 10)), surfaces);

        Assert.Equal(InputAction.PointerDown, press.Action);
        Assert.Equal(InputModifiers.Shift | InputModifiers.Control, press.Modifiers);
    }

    [Fact]
    public void A_missed_key_up_does_not_leave_Ctrl_on_later_clicks()
    {
        var router = new InputRouterService();
        var surfaces = new[] { Surface("panel", 0, wantsKeyboard: true) };
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(10, 10)), surfaces);
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonUp, 0, Pack(10, 10)), surfaces);
        // Control goes down, and its key-up never reaches the router.
        router.Route(new NativeInputMessage(InputRouterService.WmKeyDown, (nint)0x11, 0), surfaces);

        // A plain click: the mouse message's key state holds no Control.
        var press = router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(10, 10)), surfaces);

        Assert.Equal(InputModifiers.None, press.Modifiers);
    }

    [Fact]
    public void Logoff_releases_a_captured_pointer_and_clears_keyboard_focus()
    {
        var router = new InputRouterService();
        var surfaces = new[] { Surface("panel", 0, 0, wantsKeyboard: true) };
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(10, 10)), surfaces);

        var reset = router.Route(new NativeInputMessage(InputRouterService.WmLogoff, 0, 0), surfaces);

        Assert.Equal("panel", reset.ReleaseCaptureSurfaceId);
        Assert.Equal("panel", reset.ClearFocusSurfaceId);
        Assert.Null(router.CapturedSurfaceId);
        Assert.Null(router.FocusedSurfaceId);
    }

    private static InputSurface Surface(string id, int zOrder, int left = 0, bool wantsKeyboard = false) => new(id, left, 0, 100, 100, zOrder, wantsKeyboard);
    private static nint Pack(int x, int y) => (nint)(((y & 0xffff) << 16) | (x & 0xffff));
}

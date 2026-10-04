using Avalonia.Controls;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.InputRouter;
using LegACEy.Client.PanelHost;

namespace LegACEy.Client.Tests;

public sealed class KeyboardInputTests
{
    [Theory]
    [InlineData(InputRouterService.WmChar, '\b')]
    [InlineData(InputRouterService.WmChar, '\r')]
    [InlineData(InputRouterService.WmChar, '\t')]
    [InlineData(InputRouterService.WmChar, '\u0001')]
    [InlineData(InputRouterService.WmChar, '\u0016')]
    [InlineData(InputRouterService.WmSysChar, '\b')]
    public void Control_character_messages_are_eaten_without_text_input(int message, char character)
    {
        var router = new InputRouterService();
        var surfaces = new[] { new InputSurface("text", 0, 0, 200, 60, 0, wantsKeyboard: true) };
        var input = new NativeInputMessage(message, (nint)character, 0);
        Assert.False(router.Route(input, surfaces).Eat);

        router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(20, 20)), surfaces);
        var result = router.Route(input, surfaces);

        Assert.True(result.Eat);
        Assert.Equal(InputAction.None, result.Action);
    }

    [Fact]
    public void Backspace_key_and_character_sequence_deletes_without_inserting_control_text() => RenderThread.Run(() =>
    {
        TextBox? textBox = null;
        using var panel = AvaloniaPanel.Create(() => textBox = new TextBox { Width = 180 }, 200, 60);
        var router = FocusTextBox(panel);
        panel.TextInput("hello");
        var surfaces = new[] { new InputSurface("text", 0, 0, 200, 60, 0, panel.WantsKeyboard) };
        var key = router.Route(new NativeInputMessage(InputRouterService.WmKeyDown, (nint)0x08, 0), surfaces);
        panel.KeyDown(Win32KeyMap.ToAvaloniaKey(key.KeyCode));
        var character = router.Route(new NativeInputMessage(InputRouterService.WmChar, (nint)'\b', 0), surfaces);
        if (character.Action == InputAction.TextInput)
            panel.TextInput(((char)character.KeyCode).ToString());
        var release = router.Route(new NativeInputMessage(InputRouterService.WmKeyUp, (nint)0x08, 0), surfaces);
        panel.KeyUp(Win32KeyMap.ToAvaloniaKey(release.KeyCode));

        Assert.True(key.Eat && character.Eat && release.Eat);
        Assert.Equal("hell", textBox!.Text);
    });

    [Theory]
    [InlineData(0x24, 0)]
    [InlineData(0x23, 5)]
    public void Native_navigation_keys_move_the_focused_text_box_caret(int virtualKey, int expectedCaret) => RenderThread.Run(() =>
    {
        TextBox? textBox = null;
        using var panel = AvaloniaPanel.Create(() => textBox = new TextBox { Width = 180 }, 200, 60);
        var router = FocusTextBox(panel);
        panel.TextInput("hello");
        textBox!.CaretIndex = 2;
        var surfaces = new[] { new InputSurface("text", 0, 0, 200, 60, 0, panel.WantsKeyboard) };
        var key = router.Route(new NativeInputMessage(InputRouterService.WmKeyDown, (nint)virtualKey, 0), surfaces);
        panel.KeyDown(Win32KeyMap.ToAvaloniaKey(key.KeyCode));
        var release = router.Route(new NativeInputMessage(InputRouterService.WmKeyUp, (nint)virtualKey, 0), surfaces);
        panel.KeyUp(Win32KeyMap.ToAvaloniaKey(release.KeyCode));

        Assert.True(key.Eat && release.Eat);
        Assert.Equal(expectedCaret, textBox.CaretIndex);
        Assert.Equal("hello", textBox.Text);
    });

    private static InputRouterService FocusTextBox(AvaloniaPanel panel)
    {
        var router = new InputRouterService();
        var surfaces = new[] { new InputSurface("text", 0, 0, 200, 60, 0) };
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonDown, 0, Pack(20, 20)), surfaces);
        panel.PointerDown(20, 20);
        panel.PointerUp(20, 20);
        router.Route(new NativeInputMessage(InputRouterService.WmLButtonUp, 0, Pack(20, 20)), surfaces);
        Assert.True(panel.WantsKeyboard);
        return router;
    }

    private static nint Pack(int x, int y) => (nint)(((y & 0xffff) << 16) | (x & 0xffff));
}

using LegACEy.Client.DecalPlugin;
using LegACEy.Client.Demo;
using LegACEy.Client.InputRouter;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>The Market's keys through the input router, as the native hook sends them: Esc closes, "/" searches, and other keys go to the game.</summary>
public sealed class MarketKeyboardTests
{
    private const int VkEscape = 0x1B;
    private const int VkOemSlash = 0xBF;
    private const int VkW = 'W';
    private const string SurfaceId = "market/market";

    private static InputSurface[] Surfaces(MarketFixture market)
    {
        var shortcuts = (IWindowShortcuts)market.Window;
        return new[] { new InputSurface(SurfaceId, 0, 0, 980, 640, 0, market.Host.WantsKeyboard, vk => shortcuts.WantsShortcut(Win32KeyMap.ToAvaloniaKey(vk))) };
    }

    private static InputRoute Key(InputRouterService router, InputSurface[] surfaces, int message, int virtualKey) =>
        router.Route(new NativeInputMessage(message, (nint)virtualKey, 0), surfaces);

    [Fact]
    public void Escape_through_the_router_closes_the_market() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        var router = new InputRouterService();
        var surfaces = Surfaces(market);
        router.FocusSurface(SurfaceId);

        var route = Key(router, surfaces, InputRouterService.WmKeyDown, VkEscape);
        Assert.Equal(InputAction.KeyDown, route.Action);
        Assert.True(route.Eat);
        market.Host.KeyDown(Win32KeyMap.ToAvaloniaKey(route.KeyCode));

        Assert.Equal(1, market.CloseRequests);
    });

    [Fact]
    public void Slash_through_the_router_opens_the_search_and_is_not_typed_into_it() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        var router = new InputRouterService();
        var surfaces = Surfaces(market);
        router.FocusSurface(SurfaceId);

        var key = Key(router, surfaces, InputRouterService.WmKeyDown, VkOemSlash);
        market.Host.KeyDown(Win32KeyMap.ToAvaloniaKey(key.KeyCode));
        Assert.True(market.Panel.SearchHasFocus);
        // the character comes after the key down, and the search has the focus by then: it must still not be typed
        var character = router.Route(new NativeInputMessage(InputRouterService.WmChar, (nint)'/', 0), Surfaces(market));
        Assert.True(character.Eat);
        Assert.Equal(string.Empty, market.Panel.SearchText);
        Key(router, Surfaces(market), InputRouterService.WmKeyUp, VkOemSlash);

        // the next character is the search's
        var letter = router.Route(new NativeInputMessage(InputRouterService.WmChar, (nint)'a', 0), Surfaces(market));
        Assert.Equal(InputAction.TextInput, letter.Action);
        market.Host.TextInput("a");
        Assert.Equal("a", market.Panel.SearchText);
    });

    [Fact]
    public void Keys_the_market_does_not_use_still_reach_the_game() => RenderThread.Run(() =>
    {
        using var market = new MarketFixture(980);
        var router = new InputRouterService();
        router.FocusSurface(SurfaceId);

        var route = Key(router, Surfaces(market), InputRouterService.WmKeyDown, VkW);

        Assert.Equal(InputAction.None, route.Action);
        Assert.False(route.Eat);
    });
}

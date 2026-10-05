using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

public sealed class RetailSurfaceRendererTests
{
    [Fact]
    public void EntryFallsBackToEarlyDrawingWhenLateHookIsUnavailable()
    {
        var draws = 0;
        var renderer = new RetailSurfaceRenderer(() => { }, () => draws++);
        renderer.RenderFrame(isSessionReady: false, postUiAvailable: false);
        Assert.Equal(1, draws);
        renderer.DrawAfterRetailUi();
        Assert.Equal(1, draws);
    }

    [Fact]
    public void LogoutUsesLateDrawingAgainAfterNormalGameplay()
    {
        var pixels = "world";
        var renderer = new RetailSurfaceRenderer(() => { }, () => pixels = "replacement");
        renderer.RenderFrame(isSessionReady: true, postUiAvailable: true);
        renderer.DrawAfterRetailUi();
        renderer.RenderFrame(isSessionReady: false, postUiAvailable: true);
        pixels = "retail logout transition";
        renderer.DrawAfterRetailUi();
        Assert.Equal("replacement", pixels);
    }

    [Fact]
    public void EntryFrameRemainsVisibleAfterRetailPortalPaintsOverEarlyDrawing()
    {
        var pixels = "world";
        var prepared = false;
        var renderer = new RetailSurfaceRenderer(() => prepared = true, () =>
        {
            Assert.True(prepared);
            pixels = "replacement";
        });
        renderer.RenderFrame(isSessionReady: false, postUiAvailable: true);
        pixels = "retail portal";
        renderer.DrawAfterRetailUi();
        Assert.Equal("replacement", pixels);
    }

    [Fact]
    public void NormalFramesKeepRetailWindowsAboveTheReplacementWithoutDuplicateDrawing()
    {
        var draws = 0;
        var pixels = "world";
        var renderer = new RetailSurfaceRenderer(() => { }, () => { draws++; pixels = "replacement"; });
        renderer.RenderFrame(isSessionReady: true, postUiAvailable: true);
        pixels = "retail window";
        renderer.DrawAfterRetailUi();
        Assert.Equal("retail window", pixels);
        Assert.Equal(1, draws);
    }

    [Fact]
    public void PreparedTransitionDrawStillRunsIfLoginCompletesBeforeTheLateCallback()
    {
        var draws = 0;
        var renderer = new RetailSurfaceRenderer(() => { }, () => draws++);
        renderer.RenderFrame(isSessionReady: false, postUiAvailable: true);
        Assert.Equal(0, draws);
        renderer.DrawAfterRetailUi();
        renderer.DrawAfterRetailUi();
        Assert.Equal(1, draws);
        renderer.RenderFrame(isSessionReady: true, postUiAvailable: true);
        renderer.DrawAfterRetailUi();
        Assert.Equal(2, draws);
    }
}

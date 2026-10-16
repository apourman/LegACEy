using System;
using System.Collections.Generic;
using System.Drawing;
using LegACEy.Client.DecalPlugin;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class RetailVitalsTakeoverTests
{
    private static readonly Size Viewport = new(1000, 800);
    private static readonly Point Place = new(640, 24);
    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>The player's vitals: retail's stacked one shows at first; the switch and the window are what this drives.</summary>
    private sealed class Harness
    {
        public FakeRoot Stacked { get; } = new(true);
        public FakeRoot Side { get; } = new(false);
        public List<Point> Opened { get; } = new();
        public List<string> Log { get; } = new();
        public FakeWindow? Window { get; private set; }
        public Exception? OpenFailure { get; set; }
        public RetailVitalsTakeover Takeover { get; }

        public Harness()
        {
            Takeover = new RetailVitalsTakeover(
                () => new RetailHeldWindows(new IRetailTakeoverPort[] { Stacked, Side }, oneShows: true),
                () => Place,
                at =>
                {
                    if (OpenFailure != null) throw OpenFailure;
                    Opened.Add(at);
                    return Window = new FakeWindow();
                },
                Log.Add);
        }

        public void Tick(bool switchOn = true, bool canDraw = true, bool inGame = true, double seconds = 0) =>
            Takeover.Tick(switchOn, canDraw, inGame, Viewport, Seconds(seconds));
    }

    [Fact]
    public void Without_the_switch_retail_vitals_are_untouched_and_nothing_draws()
    {
        var harness = new Harness();

        harness.Tick(switchOn: false);
        harness.Tick(switchOn: false, seconds: 1);

        Assert.True(harness.Stacked.Visible);
        Assert.False(harness.Side.Visible);
        Assert.Empty(harness.Opened);
        Assert.Null(harness.Window);
    }

    [Fact]
    public void With_the_switch_retail_vitals_are_held_and_ours_opens_where_retail_was_and_refreshes_each_frame()
    {
        var harness = new Harness();

        harness.Tick(seconds: 1);
        harness.Tick(seconds: 2);

        Assert.False(harness.Stacked.Visible);
        Assert.Equal(new[] { Place }, harness.Opened);
        Assert.Equal(new[] { Seconds(1), Seconds(2) }, harness.Window!.Refreshes);
    }

    [Fact]
    public void A_window_that_fails_to_open_gives_retail_back_and_stays_off_until_the_switch_is_turned_off_and_on()
    {
        var harness = new Harness { OpenFailure = new InvalidOperationException("the window could not open") };

        harness.Tick();
        Assert.True(harness.Stacked.Visible);
        Assert.Single(harness.Log);

        harness.OpenFailure = null;
        harness.Tick();
        Assert.Empty(harness.Opened);

        harness.Tick(switchOn: false);
        harness.Tick();
        Assert.Equal(new[] { Place }, harness.Opened);
        Assert.False(harness.Stacked.Visible);
    }

    [Fact]
    public void A_window_failure_while_drawing_gives_retail_back()
    {
        var harness = new Harness();
        harness.Tick();

        harness.Takeover.Fail(new InvalidOperationException("the panel threw"));

        Assert.True(harness.Stacked.Visible);
        Assert.Equal(1, harness.Window!.Disposals);
    }

    [Fact]
    public void Losing_the_post_UI_draw_gives_retail_back()
    {
        var harness = new Harness();
        harness.Tick();

        harness.Tick(canDraw: false, seconds: 1);

        Assert.True(harness.Stacked.Visible);
        Assert.Equal(1, harness.Window!.Disposals);
    }

    [Fact]
    public void Logoff_gives_retail_back_and_the_next_login_holds_and_opens_again()
    {
        var harness = new Harness();
        harness.Tick();

        harness.Takeover.Release();
        Assert.True(harness.Stacked.Visible);
        Assert.Equal(1, harness.Window!.Disposals);

        harness.Tick(seconds: 5);
        Assert.False(harness.Stacked.Visible);
        Assert.Equal(2, harness.Opened.Count);
    }

    [Fact]
    public void Unload_gives_retail_back()
    {
        var harness = new Harness();
        harness.Tick();

        harness.Takeover.Dispose();

        Assert.True(harness.Stacked.Visible);
        Assert.Equal(1, harness.Window!.Disposals);
    }

    private sealed class FakeWindow : IRetailVitalsWindow
    {
        public List<TimeSpan> Refreshes { get; } = new();
        public int Disposals { get; private set; }
        public void Refresh(TimeSpan now) => Refreshes.Add(now);
        public void Dispose() => Disposals++;
    }

    /// <summary>A retail vitals root: its visibility is the state the takeover hides and restores.</summary>
    private sealed class FakeRoot : IRetailTakeoverPort
    {
        public FakeRoot(bool visible) => Visible = visible;

        public bool Visible { get; private set; }
        public bool Exists => true;
        public IntPtr ElementIdentity => new(1);
        public bool IsVisible => Visible;
        public Rectangle GetBounds() => new(0, 0, 10, 10);
        public void SetVisible(bool visible) => Visible = visible;
        public void SetSaveLocation(bool save) { }
        public void MoveTo(Point location) { }
        public bool IsUiLocked => false;
    }
}

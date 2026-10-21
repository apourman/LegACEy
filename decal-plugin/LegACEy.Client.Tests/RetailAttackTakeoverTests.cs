using System;
using System.Collections.Generic;
using System.Drawing;
using LegACEy.Client.Demo;
using LegACEy.Client.DecalPlugin;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class RetailAttackTakeoverTests
{
    private static readonly Size Viewport = new(1000, 800);
    private static readonly Point Place = new(340, 700);
    
    /// <summary>Retail's attack panel shown at first, the window and the switch are what this drives.</summary>
    private sealed class Harness
    {
        public FakeRoot Panel { get; } = new(true);
        public List<Point> Opened { get; } = new();
        public List<string> Log { get; } = new();
        public FakeWindow? Window { get; private set; }
        public Exception? OpenFailure { get; set; }
        public bool NotReady { get; set; }
        public bool? Natives { get; set; } = true;
        public RetailAttackTakeover Takeover { get; }

        public Harness()
        {
            Takeover = new RetailAttackTakeover(
                () => Natives,
                () => new RetailHeldWindows(new IRetailTakeoverPort[] { Panel }),
                () => Place,
                at =>
                {
                    if (OpenFailure != null) throw OpenFailure;
                    if (NotReady) return null;
                    Opened.Add(at);
                    return Window = new FakeWindow();
                },
                Log.Add);
        }

        /// <summary>One frame: retail changes stance first, as the player's key does, then the takeover sees it.</summary>
        public void Tick(RetailStance stance = RetailStance.Melee, bool switchOn = true, bool inGame = true)
        {
            Panel.RetailTakes(stance);
            Takeover.Tick(switchOn, true, inGame, stance, Viewport);
        }
    }

    [Fact]
    public void Without_the_switch_retail_attack_panel_is_untouched_and_nothing_opens()
    {
        var harness = new Harness();

        harness.Tick(RetailStance.Melee, switchOn: false);

        Assert.True(harness.Panel.Visible);
        Assert.False(harness.Takeover.Holds);
        Assert.Empty(harness.Opened);
    }

    [Theory]
    [InlineData(RetailStance.Melee)]
    [InlineData(RetailStance.Missile)]
    public void In_melee_and_missile_retail_panel_is_held_and_the_bar_opens_at_its_place(RetailStance stance)
    {
        var harness = new Harness();

        harness.Tick(stance);

        Assert.False(harness.Panel.Visible);
        Assert.True(harness.Takeover.Holds);
        Assert.Equal(new[] { Place }, harness.Opened);
    }

    [Theory]
    [InlineData(RetailStance.Peace)]
    [InlineData(RetailStance.Magic)]
    public void In_peace_and_magic_retail_panel_is_left_alone(RetailStance stance)
    {
        var harness = new Harness();

        harness.Tick(stance);

        Assert.True(harness.Panel.Visible);
        Assert.False(harness.Takeover.Holds);
        Assert.Empty(harness.Opened);
    }

    [Theory]
    [InlineData(RetailStance.Peace, false)]
    [InlineData(RetailStance.Magic, true)]
    public void Peace_or_magic_closes_the_bar_and_gives_retail_panel_back_as_retail_shows_it_there_and_melee_opens_the_bar_again(
        RetailStance stance, bool retailShows)
    {
        var harness = new Harness();
        harness.Tick(RetailStance.Melee);
        var first = harness.Window!;

        // Retail hides its panel in peace and shows its spell bar in magic: the panel comes back as retail would have it.
        harness.Tick(stance);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(retailShows, harness.Panel.Visible);
        Assert.False(harness.Takeover.Holds);

        harness.Tick(RetailStance.Melee);
        Assert.Equal(new[] { Place, Place }, harness.Opened);
        Assert.NotSame(first, harness.Window);
        Assert.False(harness.Panel.Visible);
    }

    [Fact]
    public void A_bar_that_is_not_ready_to_open_yet_leaves_retail_panel_and_opens_on_a_later_frame()
    {
        var harness = new Harness { NotReady = true };

        harness.Tick(RetailStance.Melee);
        Assert.True(harness.Panel.Visible);
        Assert.False(harness.Takeover.Holds);
        Assert.Empty(harness.Opened);
        Assert.Empty(harness.Log);

        harness.NotReady = false;
        harness.Tick(RetailStance.Melee);

        Assert.Equal(new[] { Place }, harness.Opened);
        Assert.False(harness.Panel.Visible);
        Assert.Empty(harness.Log);
    }

    [Fact]
    public void Leaving_melee_gives_retail_panel_back_and_missile_holds_it_again()
    {
        var harness = new Harness();
        harness.Tick(RetailStance.Melee);

        harness.Tick(RetailStance.Magic);
        Assert.True(harness.Panel.Visible);
        Assert.False(harness.Takeover.Holds);

        harness.Tick(RetailStance.Missile);
        Assert.False(harness.Panel.Visible);
    }

    [Fact]
    public void Retail_showing_its_panel_again_in_melee_is_hidden_again_on_the_next_frame()
    {
        var harness = new Harness();
        harness.Tick(RetailStance.Melee);

        harness.Panel.RetailShows();
        harness.Tick(RetailStance.Melee);

        Assert.False(harness.Panel.Visible);
    }

    [Fact]
    public void Switching_off_gives_retail_panel_back_and_closes_the_window()
    {
        var harness = new Harness();
        harness.Tick(RetailStance.Melee);

        harness.Tick(RetailStance.Melee, switchOn: false);

        Assert.True(harness.Panel.Visible);
        Assert.Equal(1, harness.Window!.Disposals);
    }

    [Fact]
    public void Unload_gives_retail_panel_back_and_closes_the_window()
    {
        var harness = new Harness();
        harness.Tick(RetailStance.Melee);

        harness.Takeover.Dispose();

        Assert.True(harness.Panel.Visible);
        Assert.False(harness.Takeover.Holds);
        Assert.Equal(1, harness.Window!.Disposals);
    }

    [Fact]
    public void A_give_back_that_throws_once_is_still_held_and_the_retry_gives_retail_back()
    {
        var harness = new Harness();
        harness.Tick(RetailStance.Melee);
        harness.Panel.FailNextShow = true;

        harness.Takeover.Release();

        Assert.False(harness.Panel.Visible);
        Assert.True(harness.Takeover.Holds);
        Assert.Single(harness.Log);

        harness.Takeover.Dispose();

        Assert.True(harness.Panel.Visible);
        Assert.False(harness.Takeover.Holds);
        Assert.Single(harness.Log);
    }

    [Fact]
    public void A_failed_native_check_of_any_feature_turns_the_takeover_off_and_says_why_and_retail_panel_stays()
    {
        // The options list failed, not the hold: the bar would show toggles that do nothing, so the whole takeover stays off.
        var harness = new Harness { Natives = false };

        harness.Tick(RetailStance.Melee);
        harness.Tick(RetailStance.Melee);

        Assert.True(harness.Panel.Visible);
        Assert.Empty(harness.Opened);
        Assert.Single(harness.Log);
        Assert.Contains("takeover is off", harness.Log[0]);
    }

    [Fact]
    public void A_failure_while_opening_gives_retail_panel_back_and_stays_off_until_the_switch_is_turned_off_and_on()
    {
        var harness = new Harness { OpenFailure = new InvalidOperationException("the window could not open") };

        harness.Tick(RetailStance.Melee);
        Assert.True(harness.Panel.Visible);
        Assert.Single(harness.Log);

        harness.OpenFailure = null;
        harness.Tick(RetailStance.Melee);
        Assert.Empty(harness.Opened);

        harness.Tick(RetailStance.Melee, switchOn: false);
        harness.Tick(RetailStance.Melee);
        Assert.Equal(new[] { Place }, harness.Opened);
        Assert.False(harness.Panel.Visible);
    }

    public sealed class FakeWindow : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    /// <summary>
    /// A retail attack panel root: its visibility is the state the takeover hides and restores. Retail shows it when the stance changes to melee,
    /// missile (the attack bar) or magic (the spell bar) and hides it when the stance changes to peace.
    /// </summary>
    private sealed class FakeRoot : IRetailTakeoverPort
    {
        private RetailStance? _stance;

        public FakeRoot(bool visible) => Visible = visible;

        public bool Visible { get; private set; }
        /// <summary>Makes the next call that shows retail's panel throw, as a native call that fails once does.</summary>
        public bool FailNextShow { get; set; }
        public bool Exists => true;
        public IntPtr ElementIdentity => new(1);
        public bool IsVisible => Visible;
        public Rectangle GetBounds() => new(0, 0, 10, 10);
        public void SetVisible(bool visible)
        {
            if (visible && FailNextShow)
            {
                FailNextShow = false;
                throw new InvalidOperationException("the native call failed");
            }
            Visible = visible;
        }
        public void RetailShows() => Visible = true;
        /// <summary>Retail's own reaction to a stance change; the first stance seen is where retail starts, so it changes nothing.</summary>
        public void RetailTakes(RetailStance stance)
        {
            if (_stance is { } before && before != stance) Visible = stance != RetailStance.Peace;
            _stance = stance;
        }
        public void SetSaveLocation(bool save) { }
        public void MoveTo(Point location) { }
        public bool IsUiLocked => false;
    }
}

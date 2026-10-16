using System;
using System.Linq;
using LegACEy.Client.Demo;

namespace LegACEy.Client.Tests;

public sealed class DerethVitalsModelTests
{
    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private static GameStateSnapshot State(int health, int maxHealth = 100, int stamina = 100, int mana = 100) =>
        new("Alice", "Thistledown", health, maxHealth, stamina, 100, mana, 100);

    [Fact]
    public void Each_vital_shows_its_current_and_max_in_retail_order()
    {
        var model = new VitalsModel();

        model.Update(new GameStateSnapshot("Alice", "Thistledown", 86, 100, 64, 100, 37, 100), TimeSpan.Zero);

        Assert.Equal(new[] { Vital.Health, Vital.Stamina, Vital.Mana }, model.Bars.Select(bar => bar.Vital));
        Assert.Equal(new[] { "86 / 100", "64 / 100", "37 / 100" }, model.Bars.Select(bar => bar.Label));
        Assert.Equal(0.86, model.Bars[0].Fraction, 3);
    }

    [Fact]
    public void A_drop_leaves_a_pale_trail_that_holds_a_moment_and_then_drains_to_the_current_value()
    {
        var model = new VitalsModel();
        model.Update(State(100), TimeSpan.Zero);

        model.Update(State(60), Seconds(1));
        Assert.Equal(1.0, model.Bars[0].TrailFraction, 3);

        // Still holding: the lost part stays pale for a moment before it drains.
        model.Update(State(60), Seconds(1.5));
        Assert.Equal(1.0, model.Bars[0].TrailFraction, 3);

        model.Update(State(60), Seconds(1.9));
        Assert.InRange(model.Bars[0].TrailFraction, 0.61, 0.99);

        model.Update(State(60), Seconds(3));
        Assert.Equal(0.6, model.Bars[0].TrailFraction, 3);
    }

    [Fact]
    public void Health_pulses_once_it_is_under_a_quarter_and_the_other_bars_stay_still()
    {
        var model = new VitalsModel();

        model.Update(State(25, stamina: 0, mana: 0), TimeSpan.Zero);
        Assert.False(model.Bars[0].Pulsing);

        model.Update(State(24, stamina: 0, mana: 0), Seconds(0.1));
        Assert.True(model.Bars[0].Pulsing);
        Assert.False(model.Bars[1].Pulsing);
        Assert.False(model.Bars[2].Pulsing);
    }
}

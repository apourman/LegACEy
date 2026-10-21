using LegACEy.Client.Demo;
using LegACEy.Client.DecalPlugin;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class NativeAttackBarPortTests
{
    [Theory]
    [InlineData(-0.2, 0)]
    [InlineData(0.62, 0.62)]
    [InlineData(1.4, 1)]
    [InlineData(double.NaN, 0)]
    public void The_set_point_is_clamped_to_zero_and_one_as_retails_power_bar_click_clamps_it(double value, double expected) =>
        Assert.Equal(expected, NativeAttackBarPort.ClampUnit(value), 6);

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void An_unknown_attack_height_value_reads_as_High(int native) =>
        Assert.Equal(AttackHeight.High, NativeAttackBarPort.HeightOf(native));
}

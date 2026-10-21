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
    public void The_set_point_is_clamped_to_zero_and_one_as_retails_power_bar_click_clamps_it(double value, double expected) =>
        Assert.Equal(expected, NativeAttackBarPort.ClampUnit(value), 6);

    [Theory]
    [InlineData(AttackHeight.High, 1)]
    [InlineData(AttackHeight.Medium, 2)]
    [InlineData(AttackHeight.Low, 3)]
    public void The_attack_height_is_retails_one_to_three_from_high_to_low(AttackHeight height, int native)
    {
        Assert.Equal(native, NativeAttackBarPort.NativeHeight(height));
        Assert.Equal(height, NativeAttackBarPort.HeightOf(native));
    }
}

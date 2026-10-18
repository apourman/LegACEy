using System.Numerics;
using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

public sealed class StormFogTests
{
    [Theory]
    // A zone whose safe landblock spans x 28992-29184, y 6528-6720; the storm is the ring of landblocks around it.
    [InlineData(29088, 6624, -96)]   // the safe landblock's centre
    [InlineData(29184 + 10, 6624, 10)] // 10 m into the storm past the safe edge
    [InlineData(29184 + 96, 6624, 96)] // halfway across the ring
    [InlineData(29376 - 10, 6624, 10)] // 10 m short of the ring's outer edge
    [InlineData(29376 + 50, 6624, -50)] // past the ring
    public void Storm_depth_is_metres_to_the_nearer_edge_of_the_ring(float x, float y, float depth) =>
        Assert.Equal(depth, StormFog.StormDepth(new Vector3(x, y, 0), (0x97, 0x22)), 3);

    [Fact]
    public void A_snowflake_wraps_into_the_box_around_the_camera_and_falls_only_inside_the_snow()
    {
        var middle = new Vector3(29088, 6624, 60);
        var flake = Snowfall.Wrap(new Vector3(5, 7, -1000), middle);
        Assert.InRange(flake.X, middle.X - 20, middle.X + 20);
        Assert.InRange(flake.Z, middle.Z - 12, middle.Z + 12);
        Assert.True(Vector3.Distance(flake, Snowfall.Wrap(flake + new Vector3(40, -80, 24), middle)) < 0.01f);
        // The snow covers landblock 0x9722 and the 8 around it: 288 m each way from its centre.
        var centre = new Vector2(29088, 6624);
        Assert.Equal(288, Snowfall.Depth(middle, centre), 3);
        Assert.True(Snowfall.Depth(middle + new Vector3(300, 0, 0), centre) < 0);
    }
}

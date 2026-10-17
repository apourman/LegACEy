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
}

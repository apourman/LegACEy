using LegACEy.Client.Demo;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class StormProtocolTests
{
    [Fact]
    public void A_zone_reads_as_its_landblock_and_no_zone_as_null()
    {
        Assert.Equal((0xA9, 0xB4), StormProtocol.ReadZone(new byte[] { 1, 0xA9, 0xB4 }));
        Assert.Null(StormProtocol.ReadZone(new byte[] { 0 }));
    }

    [Fact]
    public void Snow_reads_as_its_intensity_and_landblock_and_none_as_null()
    {
        Assert.Equal((SnowIntensity.Blizzard, 0xA9, 0xB4), SnowProtocol.ReadSnow(new byte[] { 2, 0xA9, 0xB4 }));
        Assert.Null(SnowProtocol.ReadSnow(new byte[] { 0 }));
    }
}

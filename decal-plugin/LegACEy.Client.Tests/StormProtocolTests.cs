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
    public void Weather_reads_as_its_kind_and_landblock_and_clear_skies_as_null()
    {
        Assert.Equal((WeatherKind.Thunderstorm, 0xA9, 0xB4), WeatherProtocol.Read(new byte[] { 3, 0xA9, 0xB4 }));
        Assert.Null(WeatherProtocol.Read(new byte[] { 0 }));
    }
}

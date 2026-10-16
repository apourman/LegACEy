using LegACEy.Client.DecalPlugin;
using LegACEy.Client.Demo;

namespace LegACEy.Client.Tests;

public sealed class RadarTests
{
    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(1f, 0f, 90f)]
    [InlineData(0f, -1f, 180f)]
    [InlineData(-1f, 0f, 270f)]
    public void HeadingReadsTheForwardAxisAsACompass(float forwardX, float forwardY, float heading) =>
        Assert.Equal(heading, NativeRadarPort.Heading(forwardX, forwardY), 3);

    [Fact]
    public void OffsetCrossesLandblocksEastAndNorth()
    {
        // Arwic's landblock 0xC6A9 and the one north-east of it.
        var here = new RadarPosition(0xC6A90031, 190f, 2f, 0f);
        var there = new RadarPosition(0xC7AA0001, 4f, 1f, 0f);

        Assert.Equal((6f, 191f), here.OffsetTo(there));
    }

    [Fact]
    public void PatchedObjectEntryTurnsOnlyTheRadarOff()
    {
        var messages = new List<string>();

        var natives = RetailHudCatalogue.Validate(Client(patched: RetailHudCatalogue.GetWeenieObject), messages.Add);

        Assert.Equal(new RetailHudNatives(true, true, true, false, true), natives);
        Assert.False(natives.RadarWindow);
        Assert.Contains(messages, message => message.Contains("radar objects is off"));
    }

    // A running client that matches the catalogue, with one address's first byte changed as a runtime patch would.
    private static Func<uint, int, byte[]> Client(uint? patched = null)
    {
        var bytes = new[] { RetailHudCatalogue.VitalsTakeover, RetailHudCatalogue.RadarTakeover, RetailHudCatalogue.Player, RetailHudCatalogue.Objects, RetailHudCatalogue.Lock }
            .SelectMany(list => list).Where(entry => entry.Address != null).ToDictionary(entry => entry.Address!.Value, entry => entry.ExpectedBytes);
        return (address, count) =>
        {
            var copy = bytes[address].Take(count).ToArray();
            if (address == patched) copy[0] = 0xE9;
            return copy;
        };
    }
}

/// <summary>The radar's game for window tests: set the player and the objects, read what was selected.</summary>
internal sealed class FakeRadarPort : IRadarPort
{
    public RadarPlayer? Player { get; set; }
    public List<RadarObject> Objects { get; } = new();
    IReadOnlyList<RadarObject> IRadarPort.Objects => Objects;
    public List<uint> Selected { get; } = new();
    public void Select(uint id) => Selected.Add(id);
    public bool UiLocked { get; set; }
}

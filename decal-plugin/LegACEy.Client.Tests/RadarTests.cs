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

    [Theory]
    [InlineData(0xC6A90014u, 60f, 84f, "33.6N, 56.7E")] // Arwic: landblock 0xC6A9, the cell 2 east and 3 north of its corner
    [InlineData(0x00000001u, 0f, 0f, "101.9S, 101.9W")]
    public void CoordinatesAreRetailsByThe24MetreCell(uint cell, float x, float y, string coordinates) =>
        Assert.Equal(coordinates, new RadarPosition(cell, x, y, 0f).Coordinates);

    // Decal hooks a function by rewriting its first instruction; the port calls these at their entries, so a hook there must change nothing.
    [Theory]
    [InlineData(RetailHudCatalogue.GetWeenieObject)]
    [InlineData(RetailHudCatalogue.GetBlipColor)]
    [InlineData(RetailHudCatalogue.GetPlayerSystem)]
    [InlineData(RetailHudCatalogue.SetLockUi)]
    [InlineData(RetailHudCatalogue.BroadcastGlobalMessage)]
    public void HookedEntryOfACalledFunctionKeepsEveryFeature(uint entry) =>
        Assert.Equal(new RetailHudNatives(true, true, true, true, true), RetailHudCatalogue.Validate(Client(patched: entry), _ => { }));

    [Fact]
    public void PatchedObjectBodyTurnsOnlyTheRadarOff()
    {
        var messages = new List<string>();

        // Inside GetWeenieObject's checked body (its call through ACCObjectMaint).
        var natives = RetailHudCatalogue.Validate(Client(patched: RetailHudCatalogue.GetWeenieObject + 15), messages.Add);

        Assert.Equal(new RetailHudNatives(true, true, true, false, true), natives);
        Assert.False(natives.RadarWindow);
        Assert.Contains(messages, message => message.Contains("radar objects is off"));
    }

    // A running client that holds the catalogue's bytes, with the byte at one address patched as a hook would (a jmp opcode).
    private static Func<uint, int, byte[]> Client(uint? patched = null)
    {
        var memory = new Dictionary<uint, byte>();
        foreach (var entry in new[] { RetailHudCatalogue.VitalsTakeover, RetailHudCatalogue.RadarTakeover, RetailHudCatalogue.Player, RetailHudCatalogue.Objects, RetailHudCatalogue.Lock }
            .SelectMany(list => list))
            for (var i = 0; i < entry.ExpectedBytes.Length; i++) memory[entry.Address!.Value + (uint)i] = entry.ExpectedBytes[i];
        if (patched is uint at) memory[at] = 0xE9;
        return (address, count) => Enumerable.Range(0, count).Select(i => memory.TryGetValue(address + (uint)i, out var value) ? value : (byte)0xCC).ToArray();
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

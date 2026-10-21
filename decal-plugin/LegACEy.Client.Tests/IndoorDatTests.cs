using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class IndoorDatTests
{
    [Fact]
    public void Real_dat_plans_read_0099_and_the_C6A9_building_as_the_prototype_did_when_the_dat_paths_are_provided()
    {
        var cellPath = Environment.GetEnvironmentVariable("LEGACEY_CELL_DAT");
        var portalPath = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        if (string.IsNullOrWhiteSpace(cellPath) || string.IsNullOrWhiteSpace(portalPath)) return;

        using var dat = new IndoorDat(portalPath, cellPath);
        var dungeon = dat.ReadPlan(0x00, 0x99);
        Assert.Equal(239, dungeon.Cells.Count);
        Assert.Equal(6, dungeon.Levels.Count);

        // Cell 0x0168 is inside a building: its place is the building's own cells, not the town's other buildings.
        var building = dat.ReadPlan(0xC6, 0xA9).Place(0x0168);
        Assert.Equal(27, building.Cells.Count);
        Assert.Equal(3, building.Levels.Count);
    }
}

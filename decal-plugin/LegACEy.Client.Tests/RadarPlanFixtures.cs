using System.Collections.Generic;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Tests;

/// <summary>
/// Hand-made indoor plans for the radar's tests, in Arwic's landblock 0xC6A9: square rooms of one floor each, joined by doorways, as
/// the DATs would give them. The player's cells are given as full cell ids.
/// </summary>
internal static class RadarPlanFixtures
{
    public const ushort Outside = 0xFFFF;
    // The dungeon's middle room, which a player stands in; the house's ground floor.
    public const uint DungeonCell = 0xC6A90101, HouseCell = 0xC6A90200;
    // The rooms' west edge, near the player (60, 84) and far from it.
    private const float Near = 58, Far = 120;
    private const float Y0 = 82;

    /// <summary>Three rooms stacked 4 m apart: the lowest (0x0100) and the top (0x0102) each a stair from the middle (0x0101).</summary>
    public static IndoorPlan Dungeon(int x = 0xC6, int y = 0xA9) => IndoorPlan.Build(x, y, new[]
    {
        Room(0x0100, Near, 0, 0x0101),
        Room(0x0101, Near, 4, 0x0100, 0x0102),
        Room(0x0102, Near, 8, 0x0101),
    });

    /// <summary>
    /// A house with a door out on its ground floor and an upstairs, and a second building with a door out, far off and on a level of its
    /// own, so the landblock has three levels and the house's place only two.
    /// </summary>
    public static IndoorPlan Town() => IndoorPlan.Build(0xC6, 0xA9, new[]
    {
        Room(0x0200, Near, 0, Outside, 0x0201),
        Room(0x0201, Near, 4, 0x0200),
        Room(0x0300, Far, 12, Outside),
    });

    // A 4 m square room on the floor at height z, with a doorway in its east wall to each cell in beyond (0xFFFF is outside).
    private static IndoorCellShape Room(ushort id, float x0, float z, params ushort[] beyond)
    {
        var polygons = new Dictionary<ushort, Vec3[]> { [1] = new[] { V(x0, Y0, z), V(x0 + 4, Y0, z), V(x0 + 4, Y0 + 4, z), V(x0, Y0 + 4, z) } };
        var portals = new List<(ushort Polygon, ushort OtherCell)>();
        for (var i = 0; i < beyond.Length; i++)
        {
            var polygon = (ushort)(7 + i);
            var y = Y0 + i;
            polygons[polygon] = new[] { V(x0 + 4, y, z), V(x0 + 4, y + 1, z), V(x0 + 4, y + 1, z + 4), V(x0 + 4, y, z + 4) };
            portals.Add((polygon, beyond[i]));
        }
        return new IndoorCellShape(id, Frame.Identity, polygons, portals);
    }

    private static Vec3 V(float x, float y, float z) => new(x, y, z);
}

using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class IndoorPlanTests
{
    [Fact]
    public void Two_cells_give_floors_walls_without_the_doorway_a_door_each_way_and_two_levels()
    {
        // A 4 m room on the floor, with a doorway (polygon 7) in its east wall that is a stair up to the cell beyond.
        var room = new IndoorCellShape(0x0100, Frame.Identity, new Dictionary<ushort, Vec3[]>
        {
            [1] = new[] { V(0, 0, 0), V(4, 0, 0), V(4, 4, 0), V(0, 4, 0) }, // floor
            [2] = new[] { V(0, 0, 0), V(0, 4, 0), V(0, 4, 3), V(0, 0, 3) }, // west wall
            [3] = new[] { V(0, 4, 0), V(4, 4, 0), V(4, 4, 3), V(0, 4, 3) }, // north wall
            [4] = new[] { V(0, 0, 0), V(4, 0, 0), V(4, 0, 3), V(0, 0, 3) }, // south wall
            [5] = new[] { V(4, 0, 0), V(4, 1, 0), V(4, 1, 3), V(4, 0, 3) }, // east wall, south of the doorway
            [6] = new[] { V(4, 3, 0), V(4, 4, 0), V(4, 4, 3), V(4, 3, 3) }, // east wall, north of the doorway
            [7] = new[] { V(4, 1, 0), V(4, 3, 0), V(4, 3, 4), V(4, 1, 4) }, // the doorway
        }, new[] { ((ushort)7, (ushort)0x0101) });

        // The cell beyond stands 4 m up, its floor to the east, and its side of the same doorway.
        var upstairs = new IndoorCellShape(0x0101, Frame.Identity, new Dictionary<ushort, Vec3[]>
        {
            [1] = new[] { V(4, 0, 4), V(8, 0, 4), V(8, 4, 4), V(4, 4, 4) },
            [7] = new[] { V(4, 1, 0), V(4, 3, 0), V(4, 3, 4), V(4, 1, 4) },
        }, new[] { ((ushort)7, (ushort)0x0100) });

        var plan = IndoorPlan.Build(0x00, 0x99, new[] { room, upstairs });
        var lower = plan.Cell(0x0100)!;
        var upper = plan.Cell(0x0101)!;

        Assert.Single(lower.Floors);
        Assert.Single(upper.Floors);
        Assert.Equal(5, lower.Walls.Count); // the doorway is not a wall
        var door = Assert.Single(lower.Doors);
        Assert.Equal((ushort)0x0101, door.Beyond);
        Assert.Equal(new[] { 4f, 2f, 2f }, new[] { door.Centre.X, door.Centre.Y, door.Centre.Z });
        Assert.Equal((ushort)0x0100, Assert.Single(upper.Doors).Beyond);

        Assert.Equal(2, plan.Levels.Count);
        Assert.Equal(0, plan.LevelOf(0x0101));
        Assert.Equal(1, plan.LevelOf(0x0100));
    }

    [Fact]
    public void A_cell_frame_turns_and_moves_its_polygons_into_landblock_metres()
    {
        // A quarter turn about z, then 10 m east: the local corner (1, 0) lands at (10, 1).
        var turn = 0.70710678f;
        var frame = new Frame(new Vec3(10, 0, 0), turn, 0, 0, turn);
        var cell = new IndoorCellShape(0x0100, frame, new Dictionary<ushort, Vec3[]>
        {
            [1] = new[] { V(0, 0, 0), V(1, 0, 0), V(1, 1, 0), V(0, 1, 0) },
        }, Array.Empty<(ushort Polygon, ushort OtherCell)>());

        var corner = IndoorPlan.Build(0x00, 0x99, new[] { cell }).Cell(0x0100)!.Floors.Single()[1];

        Assert.Equal(10f, corner.X, 3);
        Assert.Equal(1f, corner.Y, 3);
    }

    [Fact]
    public void A_place_is_the_cells_reachable_through_doors_and_leaves_out_the_rest()
    {
        var lone = new IndoorCellShape(0x0102, Frame.Identity, new Dictionary<ushort, Vec3[]>
        {
            [1] = new[] { V(20, 0, 0), V(24, 0, 0), V(24, 4, 0), V(20, 4, 0) },
        }, Array.Empty<(ushort Polygon, ushort OtherCell)>());
        var lower = new IndoorCellShape(0x0100, Frame.Identity, new Dictionary<ushort, Vec3[]>
        {
            [1] = new[] { V(0, 0, 0), V(4, 0, 0), V(4, 4, 0), V(0, 4, 0) },
            [7] = new[] { V(4, 1, 0), V(4, 3, 0), V(4, 3, 4), V(4, 1, 4) },
        }, new[] { ((ushort)7, (ushort)0x0101) });
        var upper = new IndoorCellShape(0x0101, Frame.Identity, new Dictionary<ushort, Vec3[]>
        {
            [1] = new[] { V(4, 0, 4), V(8, 0, 4), V(8, 4, 4), V(4, 4, 4) },
            [7] = new[] { V(4, 1, 0), V(4, 3, 0), V(4, 3, 4), V(4, 1, 4) },
            [8] = new[] { V(4, 0, 4), V(8, 0, 4), V(8, 0, 7), V(4, 0, 7) },
        }, new[] { ((ushort)7, (ushort)0x0100), ((ushort)8, Outside) });

        var plan = IndoorPlan.Build(0x00, 0x99, new[] { lone, lower, upper });

        Assert.Equal(new ushort[] { 0x0100, 0x0101 }, plan.Place(0x0100).Cells.Select(c => c.Id));
        Assert.Equal(new ushort[] { 0x0102 }, plan.Place(0x0102).Cells.Select(c => c.Id));
    }

    [Fact]
    public async Task Plans_are_built_on_the_thread_pool_and_only_the_current_and_previous_landblock_are_kept()
    {
        var caller = Thread.CurrentThread.ManagedThreadId;
        var built = new List<(int X, int Y)>();
        var builders = new List<int>();
        var cache = new IndoorPlanCache((x, y) =>
        {
            lock (built)
            {
                built.Add((x, y));
                builders.Add(Thread.CurrentThread.ManagedThreadId);
            }
            return IndoorPlan.Build(x, y, Array.Empty<IndoorCellShape>());
        });

        // A, B, B (kept), C (A drops out), B (kept as the previous), A (built again).
        foreach (var (x, y) in new[] { (0xC6, 0xA9), (0x00, 0x99), (0x00, 0x99), (0x01, 0x02), (0x00, 0x99), (0xC6, 0xA9) })
            await cache.PlanAsync(x, y);

        Assert.Equal(new[] { (0xC6, 0xA9), (0x00, 0x99), (0x01, 0x02), (0xC6, 0xA9) }, built);
        Assert.DoesNotContain(caller, builders);
    }

    [Fact]
    public void A_floor_whose_first_three_corners_are_in_a_line_is_still_a_floor()
    {
        var cell = new IndoorCellShape(0x0100, Frame.Identity, new Dictionary<ushort, Vec3[]>
        {
            [1] = new[] { V(0, 0, 0), V(2, 0, 0), V(4, 0, 0), V(4, 4, 0), V(0, 4, 0) },
        }, Array.Empty<(ushort Polygon, ushort OtherCell)>());

        Assert.Single(IndoorPlan.Build(0x00, 0x99, new[] { cell }).Cell(0x0100)!.Floors);
    }

    [Fact]
    public async Task A_failed_plan_is_kept_only_while_it_is_current_and_is_built_again_after_the_player_leaves()
    {
        var attempts = 0;
        var logged = new List<string>();
        var cache = new IndoorPlanCache((x, y) =>
        {
            if (x == 0xC6 && Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("the DAT is busy");
            return IndoorPlan.Build(x, y, Array.Empty<IndoorCellShape>());
        }, logged.Add);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.PlanAsync(0xC6, 0xA9));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.PlanAsync(0xC6, 0xA9)); // still current: no rebuild each frame
        Assert.Equal(1, attempts);
        Assert.Single(logged);

        await cache.PlanAsync(0x00, 0x99); // the failed plan leaves the current slot, so it is dropped
        var plan = await cache.PlanAsync(0xC6, 0xA9);

        Assert.Equal(2, attempts);
        Assert.Equal(0xC6, plan.X);
    }

    [Fact]
    public async Task The_owned_DAT_is_disposed_only_after_the_reads_in_flight_finish_and_no_new_plan_starts()
    {
        var release = new ManualResetEventSlim(false);
        var owned = new CountingDisposable();
        var cache = new IndoorPlanCache((x, y) =>
        {
            release.Wait();
            return IndoorPlan.Build(x, y, Array.Empty<IndoorCellShape>());
        }, owned: owned);
        var read = cache.PlanAsync(0xC6, 0xA9);

        cache.Dispose();
        Assert.Equal(0, owned.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.PlanAsync(0x00, 0x99));
        release.Set();
        await read;

        var until = DateTime.UtcNow.AddSeconds(5);
        while (owned.Count == 0 && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.Equal(1, owned.Count);
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int Count;
        public void Dispose() => Interlocked.Increment(ref Count);
    }

    private const ushort Outside = 0xFFFF;

    private static Vec3 V(float x, float y, float z) => new(x, y, z);
}

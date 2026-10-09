using System;
using System.Collections.Generic;
using LegACEy.Client.Demo;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class InventoryRefreshTests
{
    [Fact]
    public void Many_marks_before_a_flush_rebuild_once_and_raise_changed_once()
    {
        var reads = 0;
        var refresh = new InventoryRefresh(() => { reads++; return Snapshot(burden: 10); }, _ => { });
        var changes = 0;
        refresh.Changed += () => changes++;

        refresh.MarkStale();
        refresh.MarkStale();
        refresh.MarkStale();
        refresh.Flush();
        refresh.Flush();

        Assert.Equal(1, reads);
        Assert.Equal(1, changes);
        Assert.Equal(10, refresh.Snapshot.Burden);
    }

    [Fact]
    public void A_rebuild_with_the_same_content_raises_no_change()
    {
        var refresh = new InventoryRefresh(() => Snapshot(burden: 10), _ => { });
        var changes = 0;
        refresh.Changed += () => changes++;

        refresh.MarkStale();
        refresh.Flush();
        refresh.MarkStale();
        refresh.Flush();

        Assert.Equal(1, changes);
    }

    [Fact]
    public void A_failing_read_keeps_the_last_snapshot_stays_stale_and_logs_once_per_failure_streak()
    {
        var fail = true;
        var logged = new List<string>();
        var refresh = new InventoryRefresh(() => fail ? throw new InvalidOperationException("not readable yet") : Snapshot(burden: 20), logged.Add);
        var changes = 0;
        refresh.Changed += () => changes++;

        refresh.MarkStale();
        refresh.Flush();
        refresh.Flush();
        refresh.Flush();

        Assert.Equal(0, changes);
        Assert.Single(logged);
        Assert.Equal(0, refresh.Snapshot.Burden);

        fail = false;
        refresh.Flush();

        Assert.Equal(1, changes);
        Assert.Equal(20, refresh.Snapshot.Burden);

        fail = true;
        refresh.MarkStale();
        refresh.Flush();

        Assert.Equal(2, logged.Count);
    }

    private static InventorySnapshot Snapshot(int burden) => new(new InventoryPack(1, "Main Pack", 0, 96), Array.Empty<InventoryPack>(),
        Array.Empty<InventoryItem>(), Array.Empty<WieldedItem>(), burden, 0, 0, 0, 0);
}

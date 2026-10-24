using System;
using System.Collections.Generic;
using LegACEy.Client.Demo;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class InventoryMoveQueueTests
{
    [Fact]
    public void A_set_of_moves_goes_one_at_a_time_as_each_request_clears()
    {
        var pending = false;
        var sent = new List<uint>();
        // Sending a move puts it in flight, as retail does.
        var queue = new InventoryMoveQueue((item, _, _) => { sent.Add(item); pending = true; }, () => TimeSpan.Zero, () => pending);

        queue.Move(1, 9, 0);
        queue.Move(2, 9, 1);
        queue.Move(3, 9, 2);
        queue.Pump();
        Assert.Equal(new uint[] { 1 }, sent);

        pending = false;
        queue.Pump();
        queue.Pump();
        Assert.Equal(new uint[] { 1, 2 }, sent);

        pending = false;
        queue.Pump();
        Assert.Equal(new uint[] { 1, 2, 3 }, sent);
    }

    [Fact]
    public void A_request_that_never_clears_lets_the_next_move_go_after_the_timeout()
    {
        var now = TimeSpan.Zero;
        var sent = new List<uint>();
        // Without retail's flag every request reads as pending: the first move still goes at once.
        var queue = new InventoryMoveQueue((item, _, _) => sent.Add(item), () => now, () => true);

        queue.Move(1, 9, 0);
        queue.Move(2, 9, 1);
        now = InventoryMoveQueue.Timeout - TimeSpan.FromMilliseconds(1);
        queue.Pump();
        Assert.Equal(new uint[] { 1 }, sent);

        now = InventoryMoveQueue.Timeout;
        queue.Pump();
        Assert.Equal(new uint[] { 1, 2 }, sent);
    }
}

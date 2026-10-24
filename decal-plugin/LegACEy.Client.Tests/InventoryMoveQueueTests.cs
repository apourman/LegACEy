using System;
using System.Collections.Generic;
using LegACEy.Client.Demo;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class InventoryMoveQueueTests
{
    [Fact]
    public void A_set_of_moves_goes_one_at_a_time_as_each_is_answered()
    {
        var sent = new List<uint>();
        var queue = new InventoryMoveQueue((item, _, _) => sent.Add(item), () => TimeSpan.Zero);

        queue.Move(1, 9, 0);
        queue.Move(2, 9, 1);
        queue.Move(3, 9, 2);
        queue.Pump();
        Assert.Equal(new uint[] { 1 }, sent);

        queue.Answered();
        queue.Pump();
        queue.Pump();
        Assert.Equal(new uint[] { 1, 2 }, sent);

        queue.Answered();
        queue.Pump();
        Assert.Equal(new uint[] { 1, 2, 3 }, sent);
    }

    [Fact]
    public void An_unanswered_move_lets_the_next_go_after_the_timeout()
    {
        var now = TimeSpan.Zero;
        var sent = new List<uint>();
        var queue = new InventoryMoveQueue((item, _, _) => sent.Add(item), () => now);

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

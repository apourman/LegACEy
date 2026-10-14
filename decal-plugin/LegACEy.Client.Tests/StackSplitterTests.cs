using System.Collections.Generic;
using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

public sealed class StackSplitterTests
{
    [Fact]
    public void A_move_of_the_selected_stack_splits_by_retails_amount_and_anything_else_moves_whole()
    {
        var port = new FakeSplitPort { SelectedId = 0x50000003, StackSize = 50, SplitAmount = 10 };
        var splitter = new StackSplitter(port);

        Assert.True(splitter.TrySplitToContainer(0x50000003, 0x80000001, 4));
        Assert.True(splitter.TrySplitTo3D(0x50000003));
        // Another object is moved whole, as is the selected stack once the amount reaches its size.
        Assert.False(splitter.TrySplitTo3D(0x50000009));
        port.SplitAmount = 50;
        Assert.False(splitter.TrySplitTo3D(0x50000003));

        Assert.Equal(new[] { "container 0x50000003 0x80000001 4 10", "ground 0x50000003 10" }, port.Sent);
    }

    /// <summary>Retail's selection, split amount and senders: records each send as retail receives it.</summary>
    private sealed class FakeSplitPort : IRetailSplitPort
    {
        public uint SelectedId { get; set; }
        public int SplitAmount { get; set; }
        public int StackSize { get; set; }
        public List<string> Sent { get; } = new();

        public void SplitTo3D(uint stackId, int amount) => Sent.Add($"ground 0x{stackId:X8} {amount}");

        public void SplitToContainer(uint stackId, uint containerId, int place, int amount) =>
            Sent.Add($"container 0x{stackId:X8} 0x{containerId:X8} {place} {amount}");
    }
}

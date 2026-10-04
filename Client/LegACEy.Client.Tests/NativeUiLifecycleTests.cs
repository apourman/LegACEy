using LegACEy.Client.DecalPlugin;
using System.Drawing;

namespace LegACEy.Client.Tests;

public sealed class NativeUiLifecycleTests
{
    [Fact]
    public void CatalogueMismatchFailsClosedAndNamesTheEntry()
    {
        var expected = new NativeUiEntry("test entry", 0x1234, new byte[] { 1, 2 }, "test source", "ThisCall");
        var messages = new List<string>();
        var allowed = NativeUiCatalogue.Validate((_, count) => new byte[count], messages.Add, new[] { expected });
        Assert.False(allowed);
        Assert.Contains(messages, message => message.Contains("test entry"));
        Assert.Contains(messages, message => message.Contains("disabled"));
    }

    [Fact]
    public void RootElementIdsAreConstantsRatherThanRuntimeAddresses()
    {
        var roots = NativeUiCatalogue.Entries.Where(entry => entry.Name.StartsWith("RootElementId::", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(roots);
        Assert.All(roots, entry =>
        {
            Assert.Null(entry.Address);
            Assert.Empty(entry.ExpectedBytes);
            Assert.NotNull(entry.ConstantValue);
            Assert.Contains("constant", entry.CallingConvention);
        });
    }

    [Fact]
    public void FrameTakesOverVisibleElementAndTracksBounds()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(10, 20, 80, 30) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);

        lifecycle.Tick();

        Assert.False(port.Visible);
        Assert.Equal(new Point(10, 20), surface.Location);
        Assert.True(surface.Visible);
        port.Visible = true;
        port.Bounds = new Rectangle(40, 50, 80, 30);
        lifecycle.Tick();
        Assert.False(port.Visible);
        Assert.Equal(new Point(40, 50), surface.Location);
    }

    [Fact]
    public void LifecycleKeepsReplacementVisibleWhileRetailElementIsHiddenByTakeover()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(10, 20, 80, 30) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);

        lifecycle.Tick();
        lifecycle.Tick();

        Assert.False(port.Visible);
        Assert.True(surface.Visible);
        Assert.True(lifecycle.MoveTo(new Point(100, 120)));
    }

    [Fact]
    public void DragMovesNativeElementOnlyWhenUiIsUnlocked()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(10, 20, 80, 30) };
        using var lifecycle = new RetailTakeoverLifecycle(port, new FakeSurface());
        lifecycle.Tick();
        port.Locked = true;
        Assert.False(lifecycle.CanDrag);
        Assert.False(lifecycle.MoveTo(new Point(100, 120)));
        Assert.Empty(port.Moves);
        port.Locked = false;
        Assert.True(lifecycle.CanDrag);
        Assert.True(lifecycle.MoveTo(new Point(100, 120)));
        Assert.Equal(new Point(100, 120), Assert.Single(port.Moves));
    }

    [Fact]
    public void DisposeRestoresNativeElement()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(10, 20, 80, 30) };
        var lifecycle = new RetailTakeoverLifecycle(port, new FakeSurface());
        lifecycle.Tick();
        lifecycle.Dispose();
        Assert.True(port.Visible);
    }

    [Fact]
    public void DisposeCanRetryTransientNativeRestoreFailure()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(10, 20, 80, 30), FailShowCount = 1 };
        var surface = new FakeSurface();
        var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick();

        Assert.Throws<InvalidOperationException>(() => lifecycle.Dispose());
        Assert.False(port.Visible);
        Assert.False(surface.Visible);

        lifecycle.Dispose();
        Assert.True(port.Visible);
    }

    [Fact]
    public void FailureRestoresNativeElement()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(10, 20, 80, 30) };
        var surface = new FakeSurface { ThrowOnMove = true };
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        Assert.Throws<InvalidOperationException>(() => lifecycle.Tick());
        Assert.True(port.Visible);
    }

    private sealed class FakePort : IRetailTakeoverPort
    {
        public bool Visible { get; set; }
        public Rectangle Bounds { get; set; }
        public bool Locked { get; set; }
        public int FailShowCount { get; set; }
        public List<Point> Moves { get; } = new();
        public bool IsVisible => Visible;
        public Rectangle GetBounds() => Bounds;
        public void SetVisible(bool visible)
        {
            if (visible && FailShowCount > 0)
            {
                FailShowCount--;
                throw new InvalidOperationException("Transient visibility failure.");
            }
            Visible = visible;
        }
        public void MoveTo(Point location) => Moves.Add(location);
        public bool IsUiLocked => Locked;
    }

    private sealed class FakeSurface : IRetailTakeoverSurface
    {
        public Point Location { get; private set; }
        public bool Visible { get; set; }
        public bool ThrowOnMove { get; set; }
        public void SetLocation(Point location) { if (ThrowOnMove) throw new InvalidOperationException(); Location = location; }
    }
}

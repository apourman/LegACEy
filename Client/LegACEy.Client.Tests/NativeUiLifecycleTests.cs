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
        Assert.False(port.SaveLocation);
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

    [Fact]
    public void CapturedHiddenElementRemainsPositionSourceOfTruth()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(10, 20, 80, 30) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick();
        port.Bounds = new Rectangle(40, 50, 80, 30);
        lifecycle.Tick();
        Assert.Equal(port.Bounds.Location, surface.Location);
        Assert.True(surface.Visible);
    }

    [Fact]
    public void DragPreviewSurvivesFramesUntilDragIsCancelled()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(10, 20, 80, 30) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick();
        surface.SetLocation(new Point(100, 120));
        lifecycle.Tick(isDragging: true);
        Assert.Equal(new Point(100, 120), surface.Location);
        lifecycle.Tick();
        Assert.Equal(port.Bounds.Location, surface.Location);
    }

    [Fact]
    public void MissingElementHidesReplacementAndCanBeRecaptured()
    {
        var port = new FakePort { Visible = true };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick();
        port.Exists = false;
        lifecycle.Tick();
        Assert.False(surface.Visible);
        Assert.False(lifecycle.CanDrag);
        port.Exists = true;
        port.Visible = true;
        lifecycle.Tick();
        Assert.True(surface.Visible);
        Assert.False(port.Visible);
    }

    [Fact]
    public void NewSessionRetriesPendingRestoreAndStartsActiveLifecycle()
    {
        var port = new FakePort { Visible = true, FailShowCount = 1 };
        var surface = new FakeSurface();
        var previous = new RetailTakeoverLifecycle(port, surface);
        previous.Tick();
        Assert.Throws<InvalidOperationException>(() => previous.Dispose());
        using var current = RetailTakeoverLifecycle.StartSession(previous, port, surface);
        Assert.True(port.Visible);
        current.Tick();
        Assert.True(surface.Visible);
        Assert.False(port.Visible);
        Assert.True(current.CanDrag);
    }

    [Fact]
    public void LockReadFailureRestoresRetailElement()
    {
        var port = new FakePort { Visible = true };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick();
        port.ThrowOnLock = true;
        Assert.Throws<InvalidOperationException>(() => lifecycle.MoveTo(new Point(100, 120)));
        Assert.True(port.Visible);
        Assert.False(surface.Visible);
    }

    [Fact]
    public void SurfaceHideFailureStillRestoresRetailElement()
    {
        var port = new FakePort { Visible = true };
        var surface = new FakeSurface();
        var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick();
        surface.ThrowOnHide = true;
        Assert.Throws<InvalidOperationException>(() => lifecycle.Dispose());
        Assert.True(port.Visible);
    }

    [Fact]
    public void ChangedPlayerSystemReferenceDisablesTakeovers()
    {
        var reference = NativeUiCatalogue.Entries.Single(entry => entry.Name == "CPlayerSystem::s_pPlayerSystem reference");
        var messages = new List<string>();
        Assert.False(NativeUiCatalogue.Validate((address, count) =>
        {
            var entry = NativeUiCatalogue.Entries.First(item => item.Address == address);
            var bytes = entry.ExpectedBytes.Length == 0 ? new byte[count] : entry.ExpectedBytes.ToArray();
            if (address == reference.Address) bytes[1] ^= 1;
            return bytes;
        }, messages.Add));
        Assert.Contains(messages, message => message.Contains(reference.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolutionChangeRestoresRetailResetBeforeOrAfterFrame(bool delayedReset)
    {
        var original = new Point(407, 357);
        var port = new FakePort { Visible = true, Bounds = new Rectangle(original, new Size(149, 29)), Locked = true };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1024, 768));
        if (!delayedReset) port.Bounds = new Rectangle(Point.Empty, port.Bounds.Size);
        lifecycle.Tick(viewport: new Size(1280, 960));
        if (delayedReset) port.Bounds = new Rectangle(Point.Empty, port.Bounds.Size);
        lifecycle.Tick(viewport: new Size(1280, 960));
        Assert.Equal(original, port.Bounds.Location);
        Assert.Equal(original, surface.Location);
        Assert.False(lifecycle.CanDrag);
        lifecycle.Dispose();
        Assert.True(port.Visible);
        Assert.Equal(original, port.Bounds.Location);
    }

    [Fact]
    public void ResolutionShrinkClampsBothRetailAndReplacementToNewScreen()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(900, 700, 149, 29) };
        var surface = new FakeSurface { Size = new Size(250, 30) };
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1280, 960));
        port.Bounds = new Rectangle(Point.Empty, port.Bounds.Size);
        lifecycle.Tick(viewport: new Size(800, 600));
        Assert.Equal(new Point(550, 570), surface.Location);
        Assert.Equal(surface.Location, port.Bounds.Location);
    }

    [Fact]
    public void ResolutionDuringDragRestoresCommittedPositionRatherThanPreview()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(100, 120, 149, 29) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1024, 768));
        Assert.True(lifecycle.MoveTo(new Point(300, 320)));
        surface.SetLocation(new Point(500, 520));
        port.Bounds = new Rectangle(Point.Empty, port.Bounds.Size);
        Assert.True(lifecycle.Tick(isDragging: true, viewport: new Size(1280, 960)));
        Assert.Equal(new Point(300, 320), port.Bounds.Location);
        Assert.Equal(port.Bounds.Location, surface.Location);
    }

    [Fact]
    public void ResolutionRecoveryWaitsForMissingElementToReturn()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(200, 220, 149, 29) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1024, 768));
        port.Exists = false;
        lifecycle.Tick(viewport: new Size(1280, 960));
        Assert.False(surface.Visible);
        port.Exists = true;
        port.Visible = true;
        port.Bounds = new Rectangle(Point.Empty, port.Bounds.Size);
        lifecycle.Tick(viewport: new Size(1280, 960));
        Assert.Equal(new Point(200, 220), surface.Location);
        Assert.Equal(surface.Location, port.Bounds.Location);
    }

    [Fact]
    public void NativeMovementToOriginOutsideResizeStillControlsPosition()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(200, 220, 149, 29) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1024, 768));
        lifecycle.Tick(viewport: new Size(1280, 960));
        lifecycle.Tick(viewport: new Size(1280, 960));
        port.Bounds = new Rectangle(Point.Empty, port.Bounds.Size);
        lifecycle.Tick(viewport: new Size(1280, 960));
        Assert.Equal(Point.Empty, surface.Location);
    }

    [Fact]
    public void EmptyViewportDuringTransitionDoesNotOverwriteSavedNativePosition()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(407, 357, 149, 29) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1024, 768));
        port.Bounds = new Rectangle(Point.Empty, port.Bounds.Size);
        lifecycle.Tick(viewport: Size.Empty);
        Assert.Equal(new Point(407, 357), surface.Location);
        lifecycle.Tick(viewport: new Size(1280, 960));
        Assert.Equal(surface.Location, port.Bounds.Location);
    }

    [Fact]
    public void FailedResolutionWritebackRestoresRetailVisibilityAndDisablesTakeover()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(407, 357, 149, 29) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1024, 768));
        port.ThrowOnMove = true;
        Assert.Throws<InvalidOperationException>(() => lifecycle.Tick(viewport: new Size(1280, 960)));
        Assert.True(port.Visible);
        Assert.False(surface.Visible);
        Assert.False(lifecycle.CanDrag);
    }

    [Fact]
    public void RelogRestoresDraggedBarPositionThroughNativeLocationSaving()
    {
        var persisted = new FakeNativeLayout();
        var port = new FakePort { Visible = true, Bounds = new Rectangle(0, 0, 149, 29), Layout = persisted };
        var surface = new FakeSurface();
        var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick();
        Assert.True(lifecycle.MoveTo(new Point(1381, 1028)));
        lifecycle.Dispose();

        // Retail constructs a new element from its saved layout on the next login.
        var nextPort = new FakePort { Visible = true, Bounds = new Rectangle(persisted.Position, port.Bounds.Size), Layout = persisted };
        using var nextSession = RetailTakeoverLifecycle.StartSession(lifecycle, nextPort, new FakeSurface());
        nextSession.Tick();
        Assert.Equal(new Point(1381, 1028), nextPort.Bounds.Location);
        Assert.True(nextSession.MoveTo(new Point(500, 300)));
        nextSession.Dispose();
        Assert.Equal(new Point(500, 300), persisted.Position);
    }

    [Fact]
    public void ResolutionRecoverySavesClampedNativePositionForNextLogin()
    {
        var persisted = new FakeNativeLayout();
        var port = new FakePort { Visible = true, Bounds = new Rectangle(100, 120, 149, 29), Layout = persisted };
        var surface = new FakeSurface { Size = new Size(250, 30) };
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1920, 1080));
        Assert.True(lifecycle.MoveTo(new Point(1381, 1028)));
        port.Bounds = new Rectangle(Point.Empty, port.Bounds.Size);
        lifecycle.Tick(viewport: new Size(800, 600));
        Assert.Equal(new Point(550, 570), persisted.Position);
    }

    private sealed class FakeNativeLayout
    {
        public Point Position { get; set; }
    }

    private sealed class FakePort : IRetailTakeoverPort
    {
        public bool Exists { get; set; } = true;
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
        public FakeNativeLayout? Layout { get; set; }
        public bool SaveLocation { get; private set; }
        public void SetSaveLocation(bool save) => SaveLocation = save;
        public bool ThrowOnMove { get; set; }
        public void MoveTo(Point location)
        {
            if (ThrowOnMove) throw new InvalidOperationException("Native move failed.");
            Moves.Add(location);
            Bounds = new Rectangle(location, Bounds.Size);
            // Retail MoveTo emits its layout-save notification only with SaveLocation enabled.
            if (SaveLocation && Layout != null) Layout.Position = location;
        }
        public bool ThrowOnLock { get; set; }
        public bool IsUiLocked => ThrowOnLock ? throw new InvalidOperationException("Lock read failed.") : Locked;
    }

    private sealed class FakeSurface : IRetailTakeoverSurface
    {
        public Point Location { get; private set; }
        public Size Size { get; set; } = new Size(149, 29);
        private bool _visible;
        public bool ThrowOnHide { get; set; }
        public bool Visible
        {
            get => _visible;
            set
            {
                if (!value && ThrowOnHide) throw new InvalidOperationException("Surface hide failed.");
                _visible = value;
            }
        }
        public bool ThrowOnMove { get; set; }
        public void SetLocation(Point location) { if (ThrowOnMove) throw new InvalidOperationException(); Location = location; }
    }
}

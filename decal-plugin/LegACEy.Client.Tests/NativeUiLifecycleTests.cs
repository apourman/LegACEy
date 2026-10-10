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
    public void ResolutionRoundTripRestoresPreferredNativeAndReplacementPosition()
    {
        var original = new Point(1381, 1028);
        var port = new FakePort { Visible = true, Bounds = new Rectangle(original, new Size(149, 29)) };
        var surface = new FakeSurface { Size = new Size(250, 30) };
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1920, 1080));
        lifecycle.Tick(viewport: new Size(800, 600));
        lifecycle.Tick(viewport: new Size(800, 600));
        Assert.Equal(new Point(550, 570), surface.Location);
        lifecycle.Tick(viewport: new Size(1024, 768));
        lifecycle.Tick(viewport: new Size(1024, 768));
        lifecycle.Tick(viewport: new Size(1920, 1080));
        lifecycle.Tick(viewport: new Size(1920, 1080));
        Assert.Equal(original, surface.Location);
        Assert.Equal(original, port.Bounds.Location);
    }

    [Fact]
    public void DragWhileSmallReplacesPreferredNativePosition()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(1381, 1028, 149, 29) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1920, 1080));
        lifecycle.Tick(viewport: new Size(800, 600));
        lifecycle.Tick(viewport: new Size(800, 600));
        Assert.True(lifecycle.MoveTo(new Point(100, 120)));
        lifecycle.Tick(viewport: new Size(1920, 1080));
        lifecycle.Tick(viewport: new Size(1920, 1080));
        Assert.Equal(new Point(100, 120), surface.Location);
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

    [Fact]
    public void ReplacementStaysActiveThroughLogoutUntilNativeElementDisappears()
    {
        var port = new FakePort { Visible = true };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick();
        // Retail shows its element again during the logout transition.
        port.Visible = true;
        lifecycle.Tick();
        Assert.False(port.Visible);
        Assert.True(surface.Visible);
        port.Exists = false;
        lifecycle.Tick();
        Assert.False(surface.Visible);
        port.Exists = true;
        port.Instance = new IntPtr(2);
        port.Visible = true;
        port.Bounds = new Rectangle(620, 517, 149, 29);
        lifecycle.Tick();
        Assert.False(port.Visible);
        Assert.True(surface.Visible);
        Assert.Equal(new Point(620, 517), surface.Location);
    }

    [Fact]
    public void NewHiddenElementDoesNotInheritCaptureFromPreviousCharacter()
    {
        var port = new FakePort { Visible = true };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick();
        port.Instance = new IntPtr(2);
        port.Visible = false;
        lifecycle.Tick();
        Assert.False(surface.Visible);
        Assert.False(lifecycle.CanDrag);
    }

    [Fact]
    public void NewElementDoesNotInheritResizeRecoveryFromPreviousCharacter()
    {
        var port = new FakePort { Visible = true, Bounds = new Rectangle(620, 517, 149, 29) };
        var surface = new FakeSurface();
        using var lifecycle = new RetailTakeoverLifecycle(port, surface);
        lifecycle.Tick(viewport: new Size(1920, 1080));
        port.Exists = false;
        lifecycle.Tick(viewport: new Size(800, 600));
        port.Exists = true;
        port.Instance = new IntPtr(2);
        port.Visible = true;
        port.Bounds = new Rectangle(100, 120, 149, 29);
        lifecycle.Tick(viewport: new Size(800, 600));
        Assert.Equal(new Point(100, 120), surface.Location);
        Assert.Empty(port.Moves);
    }

    [Fact]
    public void UnloadDoesNotRestoreAnUncapturedReplacementNativeInstance()
    {
        var port = new FakePort { Visible = true };
        var lifecycle = new RetailTakeoverLifecycle(port, new FakeSurface());
        lifecycle.Tick();
        port.Instance = new IntPtr(2);
        port.Visible = false;
        Assert.False(lifecycle.CanDrag);
        lifecycle.Dispose();
        Assert.False(port.Visible);
    }

    [Fact]
    public void Retail_opening_its_panel_parks_it_without_saving_and_tells_our_window_it_opened()
    {
        var port = new FakeRetailPanelPort { Open = true, Location = new Point(500, 300) };
        var events = new List<bool>();
        using var takeover = new RetailPanelTakeover(port, events.Add, _ => { });

        takeover.Tick(enabled: true);
        takeover.Tick(enabled: true);

        Assert.Equal(RetailPanelTakeover.Parked, port.Location);
        // Retail's saved position is still the one it had: the park never reaches the layout.
        Assert.Equal(new Point(500, 300), port.SavedLocation);
        Assert.True(port.Open);
        Assert.Equal(new[] { true }, events);
    }

    [Fact]
    public void Our_close_box_closes_retail_through_its_own_path_and_our_window_follows()
    {
        var port = new FakeRetailPanelPort { Open = true, Location = new Point(500, 300) };
        var events = new List<bool>();
        using var takeover = new RetailPanelTakeover(port, events.Add, _ => { });
        takeover.Tick(enabled: true);

        takeover.Close();

        Assert.Equal(1, port.CloseCalls);
        Assert.False(port.Open);
        Assert.Equal(new[] { true, false }, events);
    }

    [Fact]
    public void Retail_closing_its_panel_hides_our_window_without_us_closing_anything()
    {
        var port = new FakeRetailPanelPort { Open = true, Location = new Point(500, 300) };
        var events = new List<bool>();
        using var takeover = new RetailPanelTakeover(port, events.Add, _ => { });
        takeover.Tick(enabled: true);

        // The key, the toolbar or an item: retail hides its panel, and our window follows once the gap is a transition's length.
        port.Open = false;
        for (var frame = 0; frame < RetailPanelTakeover.TransitionFrames; frame++) takeover.Tick(enabled: true);

        Assert.Equal(0, port.CloseCalls);
        Assert.Equal(new[] { true, false }, events);
        // The frame is shared with retail's other panels, so it is back in its place once the inventory closes.
        Assert.Equal(new Point(500, 300), port.Location);
    }

    [Fact]
    public void A_failed_move_gives_retail_its_position_back_keeps_its_panel_open_and_stops_the_takeover()
    {
        var port = new FakeRetailPanelPort { Open = true, Location = new Point(500, 300) };
        var events = new List<bool>();
        using var takeover = new RetailPanelTakeover(port, events.Add, _ => { });
        takeover.Tick(enabled: true);
        // Retail lays its panel out on-screen again, and the next move fails.
        port.RetailLaysOut();
        port.FailNextMove = true;

        takeover.Tick(enabled: true);

        Assert.Equal(new Point(500, 300), port.Location);
        Assert.True(port.Open);
        Assert.Equal(new[] { true, false }, events);

        // Off for the rest of the session: retail's panel is left as it is.
        takeover.Tick(enabled: true);
        Assert.Equal(new Point(500, 300), port.Location);
        Assert.Equal(new[] { true, false }, events);
    }

    [Fact]
    public void A_resize_that_retail_lays_out_on_screen_is_parked_again_and_our_window_stays_open()
    {
        var port = new FakeRetailPanelPort { Open = true, Location = new Point(500, 300) };
        var events = new List<bool>();
        using var takeover = new RetailPanelTakeover(port, events.Add, _ => { });
        takeover.Tick(enabled: true);

        port.RetailLaysOut();
        takeover.Tick(enabled: true);

        Assert.Equal(RetailPanelTakeover.Parked, port.Location);
        Assert.Equal(new[] { true }, events);
    }

    [Fact]
    public void Switching_off_or_logging_off_gives_retail_its_panel_back_where_it_was()
    {
        var port = new FakeRetailPanelPort { Open = true, Location = new Point(500, 300) };
        var events = new List<bool>();
        using var takeover = new RetailPanelTakeover(port, events.Add, _ => { });
        takeover.Tick(enabled: true);

        takeover.Tick(enabled: false);

        Assert.Equal(new Point(500, 300), port.Location);
        Assert.True(port.SaveLocation);
        Assert.Equal(new[] { true, false }, events);

        takeover.Tick(enabled: true);
        takeover.EndSession();

        Assert.Equal(new Point(500, 300), port.Location);
        Assert.Equal(new[] { true, false, true, false }, events);
    }

    [Fact]
    public void With_the_switch_off_the_takeover_never_touches_the_retail_panel()
    {
        var port = new FakeRetailPanelPort { Open = true, Location = new Point(500, 300) };
        var events = new List<bool>();
        using var takeover = new RetailPanelTakeover(port, events.Add, _ => { });

        for (var frame = 0; frame < 3; frame++) takeover.Tick(enabled: false);
        takeover.Close();
        takeover.EndSession();

        Assert.Empty(port.Calls);
        Assert.Empty(events);
    }

    [Fact]
    public void A_short_gap_in_the_panel_or_its_open_state_does_not_close_our_window()
    {
        var port = new FakeRetailPanelPort { Open = true, Location = new Point(500, 300) };
        var events = new List<bool>();
        using var takeover = new RetailPanelTakeover(port, events.Add, _ => { });
        takeover.Tick(enabled: true);

        // A portal relayout: the element goes missing for a few frames, then the panel reads closed for a few more.
        port.Panel = IntPtr.Zero;
        for (var frame = 0; frame < 3; frame++) takeover.Tick(enabled: true);
        port.Panel = new IntPtr(1);
        port.Open = false;
        for (var frame = 0; frame < 3; frame++) takeover.Tick(enabled: true);
        port.Open = true;
        takeover.Tick(enabled: true);

        Assert.Equal(new[] { true }, events);
        Assert.Equal(RetailPanelTakeover.Parked, port.Location);
    }

    [Fact]
    public void A_move_that_keeps_failing_still_restores_the_save_bit_and_is_retried_until_it_succeeds()
    {
        var port = new FakeRetailPanelPort { Open = true, Location = new Point(500, 300) };
        var events = new List<bool>();
        using var takeover = new RetailPanelTakeover(port, events.Add, _ => { });
        takeover.Tick(enabled: true);

        port.FailAllMoves = true;
        takeover.Tick(enabled: false);
        takeover.Tick(enabled: false);

        // Still parked, the save bit back where the layout had it, and our window still held: nothing is reported closed yet.
        Assert.Equal(RetailPanelTakeover.Parked, port.Location);
        Assert.True(port.SaveLocation);
        Assert.Equal(new[] { true }, events);

        // The move works again: the next frame gives the place back, which the park never recaptured.
        port.FailAllMoves = false;
        takeover.Tick(enabled: false);

        Assert.Equal(new Point(500, 300), port.Location);
        Assert.Equal(new Point(500, 300), port.SavedLocation);
        Assert.True(port.SaveLocation);
        Assert.Equal(new[] { true, false }, events);
    }

    [Fact]
    public void The_catalogue_for_the_panel_takeover_is_well_formed()
    {
        var entries = RetailPanelCatalogue.Entries;
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrEmpty(entry.Name));
            Assert.NotNull(entry.Address);
            Assert.NotEmpty(entry.ExpectedBytes);
        });
    }

    private sealed class FakeRetailPanelPort : IRetailPanelPort
    {
        private Point _location;
        private bool _save = true;

        public List<string> Calls { get; } = new();
        public IntPtr Panel { get; set; } = new IntPtr(1);
        public bool Open { get; set; }
        public bool FailNextMove { get; set; }
        public bool FailAllMoves { get; set; }
        public int OpenCalls { get; private set; }
        public int CloseCalls { get; private set; }
        /// <summary>The position retail saved in its layout: moved only while save-location is on.</summary>
        public Point SavedLocation { get; private set; }

        public Point Location
        {
            get => _location;
            set { _location = value; SavedLocation = value; }
        }

        public IntPtr Identity { get { Calls.Add("identity"); return Panel; } }
        public bool IsOpen { get { Calls.Add("open"); return Open; } }
        public bool SaveLocation { get { Calls.Add("save?"); return _save; } }

        public void SetSaveLocation(bool save)
        {
            Calls.Add($"save {save}");
            _save = save;
        }

        public void MoveTo(Point location)
        {
            Calls.Add($"move {location.X},{location.Y}");
            if (FailAllMoves || FailNextMove)
            {
                FailNextMove = false;
                throw new InvalidOperationException("Native move failed.");
            }
            _location = location;
            if (_save) SavedLocation = location;
        }

        public void OpenPanel()
        {
            Calls.Add("open");
            OpenCalls++;
            Open = true;
        }

        public void ClosePanel()
        {
            Calls.Add("close");
            CloseCalls++;
            Open = false;
        }

        /// <summary>Retail lays the panel out again from its saved position, as a resize or a portal transition does.</summary>
        public void RetailLaysOut() => _location = SavedLocation;
    }

    private sealed class FakeNativeLayout
    {
        public Point Position { get; set; }
    }

    private sealed class FakePort : IRetailTakeoverPort
    {
        public bool Exists { get; set; } = true;
        public IntPtr Instance { get; set; } = new IntPtr(1);
        public IntPtr ElementIdentity => Exists ? Instance : IntPtr.Zero;
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

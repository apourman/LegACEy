using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;

namespace LegACEy.Client.Tests;

public sealed class IndicatorBarTests
{
    private const uint RedIcon = 0x06000001;
    private const uint BlueIcon = 0x06000002;

    [Fact]
    public void Slots_draw_their_game_art_in_a_row() => RenderThread.Run(() =>
    {
        using var panel = CreateBar(out _, out _);

        panel.Tick();

        Assert.Equal(new byte[] { 0x00, 0x00, 0xff, 0xff }, Pixel(panel.Frame, SlotCentre(0), 12));
        Assert.Equal(new byte[] { 0xff, 0x00, 0x00, 0xff }, Pixel(panel.Frame, SlotCentre(1), 12));
    });

    [Fact]
    public void Clicking_a_slot_runs_its_action_and_clicking_the_border_does_nothing() => RenderThread.Run(() =>
    {
        using var panel = CreateBar(out var clicks, out _);
        panel.Tick();

        panel.PointerDown(SlotCentre(1), 12);
        panel.PointerUp(SlotCentre(1), 12);
        panel.PointerDown(0, 0);
        panel.PointerUp(0, 0);

        Assert.Equal(new[] { "Blue" }, clicks);
    });

    [Fact]
    public void Hovering_a_slot_highlights_it_until_the_pointer_leaves() => RenderThread.Run(() =>
    {
        using var panel = CreateBar(out _, out _);
        panel.Tick();
        var resting = Pixel(panel.Frame, SlotCentre(0), 12);

        panel.PointerMove(SlotCentre(0), 12);
        Assert.True(panel.Tick());
        Assert.NotEqual(resting, Pixel(panel.Frame, SlotCentre(0), 12));

        panel.PointerLeave();
        Assert.True(panel.Tick());
        Assert.Equal(resting, Pixel(panel.Frame, SlotCentre(0), 12));
    });

    [Fact]
    public void An_open_slot_shows_the_open_overlay() => RenderThread.Run(() =>
    {
        using var panel = CreateBar(out _, out var bar);
        panel.Tick();

        bar.SetOpen("Blue", true);
        Assert.True(panel.Tick());
        Assert.Equal(new byte[] { 0x00, 0xff, 0x00, 0xff }, Pixel(panel.Frame, SlotCentre(1), 12));

        bar.SetOpen("Blue", false);
        Assert.True(panel.Tick());
        Assert.Equal(new byte[] { 0xff, 0x00, 0x00, 0xff }, Pixel(panel.Frame, SlotCentre(1), 12));
    });

    private static AvaloniaPanel CreateBar(out List<string> clicks, out IndicatorBar bar)
    {
        var clicked = clicks = new List<string>();
        var slots = new[]
        {
            new IndicatorSlot("Red", RedIcon, () => clicked.Add("Red")),
            new IndicatorSlot("Blue", BlueIcon, () => clicked.Add("Blue"))
        };
        var size = IndicatorBar.MeasureFor(slots.Length);
        IndicatorBar? created = null;
        var panel = AvaloniaPanel.Create(() => created = new IndicatorBar(slots, FakeArt), size.Width, size.Height);
        bar = created!;
        return panel;
    }

    /// <summary>Solid 20 x 20 images: red and blue slots, and a solid green "open" overlay.</summary>
    private static GameImage? FakeArt(uint id) => id switch
    {
        RedIcon => Solid(0x00, 0x00, 0xff),
        BlueIcon => Solid(0xff, 0x00, 0x00),
        IndicatorBar.OpenOverlayIcon => Solid(0x00, 0xff, 0x00),
        _ => null
    };

    private static GameImage Solid(byte b, byte g, byte r)
    {
        var pixels = new byte[20 * 20 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 0xff;
        }
        return new GameImage(20, 20, pixels);
    }

    private static int SlotCentre(int slot) => 2 + (slot * IndicatorBar.SlotSize) + (IndicatorBar.SlotSize / 2);

    private static byte[] Pixel(PanelFrame frame, int x, int y) => frame.Pixels.Skip((y * frame.Stride) + (x * 4)).Take(4).ToArray();
}

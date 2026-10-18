using Avalonia;
using Avalonia.Input;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class ModelViewTests
{
    [Fact]
    public void Dragging_turns_and_tilts_the_model_and_Shift_dragging_moves_the_view_up_it() => RenderThread.Run(() =>
    {
        ModelView? view = null;
        using var panel = AvaloniaPanel.Create(() => view = new ModelView(), 100, 200);

        panel.PointerDown(50, 100);
        panel.PointerMove(80, 150);
        panel.PointerUp(80, 150);
        Assert.Equal((float)Math.PI + 0.3f, view!.Yaw, 3);
        Assert.Equal(0.5f, view.Pitch, 3);
        Assert.Equal(0.5f, view.Focus, 3);
        view.ResetView();

        // Down the whole view at zoom 1: the view moves up by the framing's share of the model, as far as the top allows.
        panel.PointerDown(50, 100, KeyModifiers.Shift);
        panel.PointerMove(50, 150);
        panel.PointerUp(50, 150);
        Assert.Equal(0.5f + 0.25f * ModelView.Framing, view!.Focus, 3);

        // Zooming in with the pointer near the top keeps that height under it, so the view climbs toward the head.
        view.Focus = 0.5f;
        var before = view.Focus + (0.5f - 20 / 200f) * ModelView.Framing / view.Zoom;
        panel.MouseWheel(50, 20, 0, 1);
        Assert.True(view.Zoom > 1);
        Assert.True(view.Focus > 0.5f);
        Assert.Equal(before, view.Focus + (0.5f - 20 / 200f) * ModelView.Framing / view.Zoom, 3);
    });
}

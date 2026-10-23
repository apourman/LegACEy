using LegACEy.Client.GameArt;
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

        // Upright and facing the viewer, a model's front (+Y) faces the camera (-Y).
        Assert.Equal(-1, System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, view!.Orientation).Y, 3);

        // A drag down pulls the front down with the pointer, so it faces the floor; a sideways drag then turns it about the view's
        // upright axis, so the front keeps facing the floor.
        panel.PointerDown(50, 50);
        panel.PointerMove(50, 50 + Math.PI / 2 / 0.01);
        panel.PointerUp(50, 50 + Math.PI / 2 / 0.01);
        Assert.Equal(-1, System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, view.Orientation).Z, 2);
        panel.PointerDown(20, 100);
        panel.PointerMove(80, 100);
        panel.PointerUp(80, 100);
        Assert.Equal(-1, System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, view.Orientation).Z, 2);
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

    [Fact]
    public void A_point_on_the_view_finds_the_part_drawn_there_nearest_the_viewer() => RenderThread.Run(() =>
    {
        ModelView? view = null;
        using var panel = AvaloniaPanel.Create(() => view = new ModelView(), 100, 200);
        var mesh = new ModelMesh(new GameImage(1, 1, new byte[4]), clipped: false);
        void Triangle(int part, float y, float bottom, float top)
        {
            foreach (var (x, z) in new[] { (-0.1f, bottom), (0.1f, bottom), (0f, top) })
            {
                mesh.Vertices.Add(new ModelVertex { X = x, Y = y, Z = z });
                mesh.Parts.Add(part);
            }
        }
        Triangle(16, 0, 1.6f, 1.8f);
        Triangle(3, 0, 0, 0.2f);
        Triangle(9, 0, 0.8f, 1.1f);
        // In front of the chest: the model faces the viewer, so its +Y side is the near one.
        Triangle(12, 0.2f, 0.8f, 1.0f);
        view!.Model = new CharacterModel(new[] { mesh });

        Assert.Equal(16, view.PartAt(new Point(50, 20)));
        Assert.Equal(3, view.PartAt(new Point(50, 180)));
        Assert.Equal(12, view.PartAt(new Point(50, 100)));
        Assert.Equal(-1, view.PartAt(new Point(5, 100)));
    });
}

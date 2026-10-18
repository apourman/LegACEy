using System.Drawing;
using System.Numerics;
using LegACEy.Client.DecalPlugin;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class CombatTextTests
{
    [Fact]
    public void A_point_ahead_lands_on_the_screen_and_one_behind_the_camera_does_not()
    {
        var viewProjection = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1, 1, 1000);
        var screen = new Rectangle(0, 0, 800, 600);
        // Right-handed: the camera looks down -z, so a point straight ahead is the screen's middle.
        Assert.Equal(new PointF(400, 300), CombatText.ToScreen(new Vector3(0, 0, -10), viewProjection, screen));
        var above = CombatText.ToScreen(new Vector3(0, 2, -10), viewProjection, screen)!.Value;
        Assert.True(above.Y < 300);
        Assert.Null(CombatText.ToScreen(new Vector3(0, 0, 10), viewProjection, screen));
    }
}

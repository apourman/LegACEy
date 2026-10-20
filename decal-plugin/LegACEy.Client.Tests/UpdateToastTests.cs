using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Tests;

public sealed class UpdateToastTests
{
    private static readonly Version Next = new(0, 11, 0);

    [Fact]
    public void Each_update_notice_shows_its_title_and_buttons_and_the_buttons_open_the_release_or_check_again() => RenderThread.Run(() =>
    {
        var opened = new List<string>();
        var checks = 0;
        ToastNotice Toast(UpdateNotice notice) => ClientUiRuntime.UpdateToast(notice, opened.Add, () => checks++);
        var toast = new DerethNoticeToast();
        using var panel = AvaloniaPanel.Create(() => toast, (int)DerethNoticeToast.CardWidth, 200);

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Checking, null)));
        Assert.Equal(new[] { "Checking for updates…" }, Texts(panel));
        Assert.Empty(Buttons(panel));

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Ready, "chat", Next)));
        Assert.Equal(new[] { "LegACEy 0.11.0 is ready", "Downloaded. It installs when you close the game.", "What's new" }, Texts(panel));
        Press(panel, Buttons(panel).Single(button => button.Content as string == "What's new"));
        Assert.Equal(new[] { "https://github.com/apourman/LegACEy/releases/tag/decal-plugin-v0.11.0" }, opened);

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Ready, "chat", Next, SeveralClients: true)));
        Assert.Contains("Downloaded. It installs once every game window is closed.", Texts(panel));

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.UpToDate, "chat", new Version(0, 10, 1))));
        Assert.Equal(new[] { "LegACEy 0.10.1 is up to date" }, Texts(panel));

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Wait, null)));
        Assert.Equal("Checked moments ago", Texts(panel)[0]);

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Failed, "chat")));
        Assert.Equal("Couldn't check for updates", Texts(panel)[0]);
        Press(panel, Buttons(panel).Single(button => button.Content as string == "Try again"));
        Assert.Equal(1, checks);
    });

    [Fact]
    public void A_toast_hides_after_its_lifetime_waits_while_hovered_and_closes_with_the_cross() => RenderThread.Run(() =>
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var toast = new DerethNoticeToast(() => now);
        using var panel = AvaloniaPanel.Create(() => toast, (int)DerethNoticeToast.CardWidth, 200);
        void Wait(double seconds)
        {
            now += TimeSpan.FromSeconds(seconds);
            toast.Tick();
            panel.Tick();
        }

        // Wait lasts 5 s; the pointer over it holds it.
        Show(panel, toast, ClientUiRuntime.UpdateToast(new UpdateNotice(UpdateNoticeKind.Wait, null), _ => { }, () => { }));
        Wait(4);
        Assert.True(toast.IsVisible);
        panel.PointerMove(20, 20);
        Wait(30);
        Assert.True(toast.IsVisible);
        panel.PointerLeave();
        Wait(1.1);
        Assert.False(toast.IsVisible);

        // Checking waits for the outcome, which replaces it with a fresh lifetime: Ready's 12 s.
        Show(panel, toast, ClientUiRuntime.UpdateToast(new UpdateNotice(UpdateNoticeKind.Checking, null), _ => { }, () => { }));
        Wait(3600);
        Assert.True(toast.IsVisible);
        Show(panel, toast, ClientUiRuntime.UpdateToast(new UpdateNotice(UpdateNoticeKind.Ready, "chat", Next), _ => { }, () => { }));
        Assert.DoesNotContain("Checking for updates…", Texts(panel));
        Wait(11);
        Assert.True(toast.IsVisible);
        Wait(1.1);
        Assert.False(toast.IsVisible);

        Show(panel, toast, ClientUiRuntime.UpdateToast(new UpdateNotice(UpdateNoticeKind.Checking, null), _ => { }, () => { }));
        Press(panel, panel.Content.GetVisualDescendants().OfType<DerethPagerButton>().Single());
        Assert.False(toast.IsVisible);
    });

    private static void Show(AvaloniaPanel panel, DerethNoticeToast toast, ToastNotice notice)
    {
        toast.Show(notice);
        // Two frames, as the game gives it before a click: hit testing finds the new buttons only on the frame after their layout.
        panel.Tick();
        panel.Tick();
    }

    private static List<string> Texts(AvaloniaPanel panel) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? "").ToList();

    // The buttons with a label; the × is a picture.
    private static List<Button> Buttons(AvaloniaPanel panel) => panel.Content.GetVisualDescendants().OfType<Button>().Where(button => button.Content is string).ToList();

    private static void Press(AvaloniaPanel panel, Control control)
    {
        var centre = Avalonia.VisualExtensions.TranslatePoint(control, new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), panel.Content)!.Value;
        panel.PointerDown(centre.X, centre.Y);
        panel.PointerUp(centre.X, centre.Y);
    }
}

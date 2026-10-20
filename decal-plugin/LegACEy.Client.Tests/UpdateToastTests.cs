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
        var toast = new DerethNoticeToast();
        var installs = new List<Version>();
        ToastNotice Toast(UpdateNotice notice) => ClientUiRuntime.UpdateToast(notice, opened.Add, () => checks++, toast.Show, () => true, installs.Add, () => { });
        using var panel = AvaloniaPanel.Create(() => toast, (int)DerethNoticeToast.CardWidth, 200);

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Checking, null)));
        Assert.Equal(new[] { "Checking for updates…" }, Texts(panel));
        Assert.Empty(Buttons(panel));

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Ready, "chat", Next)));
        Assert.Equal(new[] { "LegACEy 0.11.0 is available", "Nothing changes until you update.", "Log out and update", "What's new" }, Texts(panel));
        Press(panel, Buttons(panel).Single(button => Label(button) == "What's new"));
        Assert.Equal(new[] { "https://github.com/apourman/LegACEy/releases/tag/decal-plugin-v0.11.0" }, opened);

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Ready, "chat", Next, SeveralClients: true)));
        Assert.Equal(new[] { "Update when all are closed", "What's new" }, Buttons(panel).Select(Label));
        // A long label wraps the next button rather than pushing it off the card.
        Assert.All(Buttons(panel), button =>
            Assert.True(Avalonia.VisualExtensions.TranslatePoint(button, new Point(button.Bounds.Width, 0), toast)!.Value.X <= toast.Bounds.Width));

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.UpToDate, "chat", new Version(0, 10, 1))));
        Assert.Equal(new[] { "LegACEy 0.10.1 is up to date" }, Texts(panel));

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Wait, null)));
        Assert.Equal("Checked moments ago", Texts(panel)[0]);

        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Failed, "chat")));
        Assert.Equal("Couldn't check for updates", Texts(panel)[0]);
        Press(panel, Buttons(panel).Single(button => Label(button) == "Try again"));
        Assert.Equal(1, checks);

        // A failed download tries the download again.
        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Failed, null, Next)));
        Assert.Equal("Couldn't download LegACEy 0.11.0", Texts(panel)[0]);
        Press(panel, Buttons(panel).Single(button => Label(button) == "Try again"));
        Assert.Equal(new[] { Next }, installs);
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
        Show(panel, toast, Idle(new UpdateNotice(UpdateNoticeKind.Wait, null)));
        Wait(4);
        Assert.True(toast.IsVisible);
        panel.PointerMove(20, 20);
        Wait(30);
        Assert.True(toast.IsVisible);
        panel.PointerLeave();
        Wait(1.1);
        Assert.False(toast.IsVisible);

        // Checking waits for the outcome, which replaces it with a fresh lifetime: Ready's 12 s.
        Show(panel, toast, Idle(new UpdateNotice(UpdateNoticeKind.Checking, null)));
        Wait(3600);
        Assert.True(toast.IsVisible);
        Show(panel, toast, Idle(new UpdateNotice(UpdateNoticeKind.Ready, "chat", Next)));
        Assert.DoesNotContain("Checking for updates…", Texts(panel));
        Wait(11);
        Assert.True(toast.IsVisible);
        Wait(1.1);
        Assert.False(toast.IsVisible);

        Show(panel, toast, Idle(new UpdateNotice(UpdateNoticeKind.Checking, null)));
        Press(panel, panel.Content.GetVisualDescendants().OfType<DerethPagerButton>().Single());
        Assert.False(toast.IsVisible);
    });

    [Fact]
    public void Updating_asks_first_waits_for_peace_downloads_and_then_logs_out() => RenderThread.Run(() =>
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var peace = true;
        var installs = 0;
        var logOuts = 0;
        var toast = new DerethNoticeToast(() => now);
        using var panel = AvaloniaPanel.Create(() => toast, (int)DerethNoticeToast.CardWidth, 200);
        ToastNotice Toast(UpdateNotice notice) => ClientUiRuntime.UpdateToast(notice, _ => { }, () => { }, toast.Show, () => peace, _ => installs++, () => logOuts++);
        Button Labelled(string label) => Buttons(panel).Single(button => Label(button) == label);
        void Wait(double seconds)
        {
            now += TimeSpan.FromSeconds(seconds);
            toast.Tick();
            panel.Tick();
        }
        var confirm = new[]
        {
            "Log out and update?",
            "LegACEy 0.11.0 downloads, then your character logs out and the game closes while it installs. Then start it again from your launcher.",
            "Log out", "Cancel"
        };
        var ready = new[] { "LegACEy 0.11.0 is available", "Nothing changes until you update.", "Log out and update", "What's new" };

        // The update button asks first, and the question waits for an answer; Cancel changes nothing.
        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Ready, "chat", Next)));
        Press(panel, Labelled("Log out and update"));
        Assert.Equal(confirm, Texts(panel));
        Wait(3600);
        Assert.True(toast.IsVisible);
        Press(panel, Labelled("Cancel"));
        Assert.Equal(ready, Texts(panel));
        Assert.Equal((0, 0), (installs, logOuts));

        // The stance is read again on the click: drawn in peace, pressed in combat, it does nothing.
        Press(panel, Labelled("Log out and update"));
        Assert.True(Labelled("Log out").IsEnabled);
        peace = false;
        Press(panel, Labelled("Log out"));
        Assert.Equal(0, installs);
        Assert.False(Labelled("Log out").IsEnabled);
        Assert.Contains("Not while in combat", Texts(panel));

        // In peace, Log out downloads first and logs out only once the download is in.
        peace = true;
        Wait(1);
        Press(panel, Labelled("Log out"));
        Assert.Equal(1, installs);
        Assert.Equal("Downloading LegACEy 0.11.0…", Texts(panel)[0]);
        Assert.Equal(0, logOuts);
        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Downloaded, "chat", Next)));
        Assert.Equal(1, logOuts);

        // Out of peace when the download lands, it asks again rather than logging out.
        peace = false;
        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Downloaded, "chat", Next)));
        Assert.Equal(1, logOuts);
        Assert.Equal(confirm, Texts(panel).Take(4));

        // Several clients: the same question, named for waiting on the other windows.
        peace = true;
        Show(panel, toast, Toast(new UpdateNotice(UpdateNoticeKind.Ready, "chat", Next, SeveralClients: true)));
        Press(panel, Labelled("Update when all are closed"));
        Assert.Equal("Update when all are closed?", Texts(panel)[0]);
        Press(panel, Labelled("Log out"));
        Assert.Equal(2, installs);
    });

    // An update toast whose buttons do nothing.
    private static ToastNotice Idle(UpdateNotice notice) => ClientUiRuntime.UpdateToast(notice, _ => { }, () => { }, _ => { }, () => true, _ => { }, () => { });

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
    private static List<Button> Buttons(AvaloniaPanel panel) => panel.Content.GetVisualDescendants().OfType<Button>().Where(button => Label(button) != null).ToList();

    private static string? Label(Button button) => (button.Content as TextBlock)?.Text;

    private static void Press(AvaloniaPanel panel, Control control)
    {
        var centre = Avalonia.VisualExtensions.TranslatePoint(control, new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), panel.Content)!.Value;
        panel.PointerDown(centre.X, centre.Y);
        panel.PointerUp(centre.X, centre.Y);
        // A button that put up another notice: its buttons can be hit from the next frame on.
        panel.Tick();
        panel.Tick();
    }
}

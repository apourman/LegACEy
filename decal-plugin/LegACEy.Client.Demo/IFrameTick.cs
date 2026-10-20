namespace LegACEy.Client.Demo;

/// <summary>
/// A window's content that keeps time, such as a toast that hides itself after a while. The client calls <see cref="Tick"/> once a frame
/// while the window shows: its panels run Avalonia's jobs but not its timers, so a DispatcherTimer never fires there.
/// </summary>
public interface IFrameTick
{
    void Tick();
}

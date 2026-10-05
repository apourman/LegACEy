using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Simple;

namespace LegACEy.Client.Preview;

internal sealed class PreviewApplication : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new PreviewWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

using System;
using System.Linq;
using Avalonia;

namespace LegACEy.Client.Preview;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        PreviewOptions.PortalPath = args.FirstOrDefault();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<PreviewApplication>().UsePlatformDetect().LogToTrace();
}

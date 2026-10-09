using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Demo;

namespace LegACEy.Plugin.Paperdoll;

/// <summary>The 3D paperdoll window's content: the character's look, with a hint under it.</summary>
public sealed class PaperdollPanel : UserControl
{
    public const int WindowWidth = 300;
    public const int WindowHeight = 440;

    public PaperdollPanel(IServerChannel channel, string portalPath)
    {
        var hint = new TextBlock { Text = "Drag to turn · wheel to zoom", Foreground = Brushes.Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var layout = new DockPanel();
        DockPanel.SetDock(hint, Dock.Bottom);
        layout.Children.Add(hint);
        layout.Children.Add(new PaperdollView(channel, portalPath));
        Content = layout;
    }
}

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LegACEy.Client.Themes;

/// <summary>
/// A throwaway Dereth window that uses every font and part a real one does. The client lays it out once at login, so the first
/// real window doesn't pay for loading the fonts and compiling the text, layout and control code (a 761 ms frame for the Vault).
/// </summary>
public static class DerethWarmUp
{
    public const int Width = 344;
    public const int Height = 606;

    public static Control Sample()
    {
        var grid = new DerethSlotGrid { Height = 200 };
        for (var i = 1; i <= 12; i++)
            grid.Cells.Add(new DerethSlot(Text(i.ToString(), 11, FontWeight.SemiBold)) { Selected = i == 1 });
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(Text("Items: 317 / 1,000", 12, FontWeight.Normal));
        content.Children.Add(new DerethSearchField("Search vault..."));
        content.Children.Add(grid);
        content.Children.Add(new DerethButton { Content = Text("Withdraw 2", 12, FontWeight.Normal) });
        content.Children.Add(new DerethPagerButton(DerethSpriteArt.PagerNext) { Width = 34, Height = 32 });
        content.Children.Add(Text("1 – 100 of 317 ×2", 12, FontWeight.Normal));
        return new DerethWindow("Vault", null, content);
    }

    private static TextBlock Text(string text, double size, FontWeight weight) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, FontFamily = DerethPalette.Body, Foreground = DerethPalette.TextBrush,
        VerticalAlignment = VerticalAlignment.Center
    };
}

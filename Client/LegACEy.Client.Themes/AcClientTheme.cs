using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Themes;

/// <summary>AC-inspired controls with portal.dat artwork and a pixel-snapped chrome sample.</summary>
public sealed class AcClientTheme : IClientTheme
{
    public const uint WindowChromeCenterId = 0x06004CC2;
    private readonly IGameArtSource _art;

    public AcClientTheme(IGameArtSource art) => _art = art ?? throw new ArgumentNullException(nameof(art));

    public string Name => "Asheron's Call";

    public IStyle CreateStyles()
    {
        var styles = new Styles();
        SimpleClientTheme.AddBase(styles, Color.FromRgb(0x23, 0x20, 0x19), Color.FromRgb(0xe2, 0xd0, 0xa4), Color.FromRgb(0x9a, 0x79, 0x43));
        // The center tile is real retail chrome art. Other border pieces can be authored from
        // the same NineSliceImage control as their portal.dat ids are selected by the gallery.
        var center = GameArtImageExtension.CreateBitmap(_art, WindowChromeCenterId);
        if (center != null)
            styles.Add(new Style(x => x.OfType<Border>().Class("ac-window-chrome"))
                { Setters = { new Setter(Border.BackgroundProperty, new ImageBrush(center)
                    { Stretch = Stretch.Fill, TileMode = TileMode.Tile }) } });
        return styles;
    }
}

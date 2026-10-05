using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>A static vault concept with its own styling and sample icons read from the player's DAT.</summary>
public sealed class VaultShellPanel : UserControl, IDisposable
{
    public const int WindowWidth = 620;
    public const int WindowHeight = 460;
    internal static readonly IBrush Text = Brush("#E8DDC7");
    internal static readonly IBrush Muted = Brush("#ACA89D");
    internal static readonly IBrush Gold = Brush("#CDA569");
    private readonly Dictionary<uint, WriteableBitmap?> _images = new();
    private readonly IGameArtSource _art;

    private static readonly (string Name, uint Icon, uint Plate)[] Samples =
    {
        ("Chainmail shirt", 0x06000FC7, 0x060011CF),
        ("Leather boots", 0x06000FAD, 0x060011F3),
        ("Gold ring", 0x06000FB5, 0x060011D5),
        ("Pendant", 0x06000FBE, 0x060011D5),
        ("Steel shield", 0x06000FCB, 0x060011CF),
        ("Leather cap", 0x06000FAA, 0x060011F3),
        ("Blue potion", 0x06001012, 0x060011D4),
        ("Yellow potion", 0x06001013, 0x060011D4),
        ("Treasure chest", 0x06001020, 0x060011D4),
        ("Small pouch", 0x06001031, 0x060011D4),
        ("Green bottle", 0x06001030, 0x060011D4),
        ("Silver goblet", 0x0600101F, 0x060011D4)
    };

    public VaultShellPanel(IGameArtSource art)
    {
        _art = art ?? throw new ArgumentNullException(nameof(art));
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10, Margin = new Thickness(20, 8, 20, 18)
        };
        var summary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        summary.Children.Add(Label("12 items  /  1,000 capacity", Text));
        var balance = Label("245 MMD", Gold);
        Grid.SetColumn(balance, 1);
        summary.Children.Add(balance);
        root.Children.Add(new Border
        {
            BorderBrush = Brush("#68563D"), BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8), Child = summary
        });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*"), ColumnSpacing = 22 };
        var inventory = new StackPanel { Spacing = 12 };
        // Static presentation surfaces: no search, filter or inventory behavior is connected.
        var search = new VaultSurface
        {
            Padding = new Thickness(12, 9), Child = Label("Search your vault…", Muted)
        };
        inventory.Children.Add(search);
        var categories = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        categories.Children.Add(Label("All items", Gold));
        var sort = Label("Name  ↓", Muted);
        Grid.SetColumn(sort, 1);
        categories.Children.Add(sort);
        inventory.Children.Add(categories);
        var slots = new UniformGrid { Columns = 6, Rows = 4 };
        for (var index = 0; index < 24; index++)
        {
            var slot = new VaultSurface(index == 0 ? VaultMaterial.SelectedSocket : VaultMaterial.Socket)
            {
                Height = 48, Margin = new Thickness(0, 0, 6, 6)
            };
            if (index < Samples.Length)
            {
                var sample = Samples[index];
                slot.Child = Icon(sample.Icon, sample.Plate);
                ToolTip.SetTip(slot, sample.Name + " — sample item");
            }
            slots.Children.Add(slot);
        }
        inventory.Children.Add(slots);
        body.Children.Add(inventory);

        var selected = Samples[0];
        var details = new StackPanel { Spacing = 10 };
        details.Children.Add(Label("SELECTED ITEM", Gold, 11));
        var identity = new StackPanel { Spacing = 12, Orientation = Orientation.Horizontal };
        identity.Children.Add(new VaultSurface(VaultMaterial.Socket)
        {
            Padding = new Thickness(9),
            Child = Icon(selected.Icon, selected.Plate)
        });
        identity.Children.Add(new StackPanel
        {
            Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children = { Label("Chainmail\nshirt", Text, 15), Label("Armor · Stored", Muted, 11) }
        });
        details.Children.Add(identity);
        details.Children.Add(Rule());
        details.Children.Add(Label("Deposited by", Muted, 11));
        details.Children.Add(Label("Arwic Wanderer"));
        details.Children.Add(Label("Withdraw to", Muted, 11));
        details.Children.Add(Label("Current character"));
        details.Children.Add(Label("60-second transfer", Muted, 11));
        details.Children.Add(ActionFace("Withdraw item", true));
        details.Children.Add(Label("Appraise item", Gold));
        var detailPane = new Border
        {
            BorderBrush = Brush("#68563D"), BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(14, 0, 0, 0), Child = details
        };
        Grid.SetColumn(detailPane, 1);
        body.Children.Add(detailPane);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 12 };
        footer.Children.Add(Rule());
        var footerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        footerRow.Children.Add(ActionFace("Deposit item", false));
        var status = Label("Sample vault · Preview only", Muted, 11);
        status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(status, 2);
        footerRow.Children.Add(status);
        Grid.SetRow(footerRow, 1);
        footer.Children.Add(footerRow);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }

    internal static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    internal static TextBlock Label(string text, IBrush? color = null, double size = 12) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = color ?? Text,
        FontSize = size, FontFamily = new FontFamily("Tahoma, avares://LegACEy.Client.Themes/Assets#Liberation Sans")
    };

    private static Border Rule() => new() { Height = 1, Background = Brush("#68563D") };

    // Deliberately a display-only face rather than an actionable transfer control.
    private static VaultSurface ActionFace(string text, bool primary) => new(VaultMaterial.BlueSteel)
    {
        Padding = new Thickness(14, 9),
        Child = Label(text, Text, primary ? 13 : 12)
    };

    private Grid Icon(uint iconId, uint plateId)
    {
        var layers = new Grid
        {
            Width = 32, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var id in new[] { plateId, iconId })
        {
            if (!_images.TryGetValue(id, out var bitmap))
                _images.Add(id, bitmap = GameArtImageExtension.CreateBitmap(_art, id));
            if (bitmap != null)
                layers.Children.Add(new Image { Source = bitmap, Width = 32, Height = 32, Stretch = Stretch.None });
        }
        if (layers.Children.Count == 0)
        {
            var fallback = Label("?", Muted);
            fallback.HorizontalAlignment = HorizontalAlignment.Center;
            fallback.VerticalAlignment = VerticalAlignment.Center;
            layers.Children.Add(fallback);
        }
        return layers;
    }

    public void Dispose()
    {
        foreach (var image in _images.Values) image?.Dispose();
        _images.Clear();
    }
}

/// <summary>Vault-specific chrome; independent of the theme gallery's retail control styling.</summary>
public sealed class VaultShellWindow : UserControl
{
    public event EventHandler? CloseRequested;

    public VaultShellWindow(VaultShellPanel panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        var layout = new Grid { RowDefinitions = new RowDefinitions("64,*") };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,32"), Margin = new Thickness(20, 6, 16, 0) };
        title.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, Spacing = 3,
            Children = { new TextBlock { Text = "Account Vault", Foreground = VaultShellPanel.Text, FontSize = 23, FontFamily = new FontFamily("Georgia, Liberation Serif") }, VaultShellPanel.Label("Shared across your account", VaultShellPanel.Muted, 11) }
        });
        var close = new Button
        {
            Content = "×", Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center,
            Template = new FuncControlTemplate<Button>((_, _) => new VaultSurface(VaultMaterial.BlueSteel)
            {
                Child = new TextBlock
                {
                    Text = "×", FontSize = 18, Foreground = VaultShellPanel.Muted,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            })
        };
        ToolTip.SetTip(close, "Close vault preview");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(close, 1);
        title.Children.Add(close);
        layout.Children.Add(title);
        Grid.SetRow(panel, 1);
        layout.Children.Add(panel);
        Content = new VaultSurface(ornate: true)
        {
            Padding = new Thickness(3), Child = layout
        };
    }
}

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Preview;

internal static class PreviewOptions
{
    public static string? PortalPath { get; set; }
}

internal sealed class PreviewWindow : Window
{
    private readonly TextBox _pathBox;
    private readonly StackPanel _workspace;
    private readonly ComboBox _themePicker;
    private PortalDat? _portal;
    private IGameArtSource _art = new MissingArtSource();
    private IStyle? _appliedTheme;

    public PreviewWindow()
    {
        Title = "LegACEy Avalonia preview";
        Width = 1180;
        Height = 850;
        MinWidth = 760;
        MinHeight = 560;

        _pathBox = new TextBox { Watermark = "Path to client_portal.dat", Text = PreviewOptions.PortalPath, Width = 600 };
        var loadButton = new Button { Content = "Load game art" };
        loadButton.Click += (_, _) => LoadArt();
        _themePicker = new ComboBox
        {
            ItemsSource = new[] { "Asheron's Call", "Simple" },
            SelectedIndex = 0,
            Width = 180
        };
        _themePicker.SelectionChanged += (_, _) => ApplySelectedTheme();
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10),
            Children = { new TextBlock { Text = "Portal data", VerticalAlignment = VerticalAlignment.Center }, _pathBox, loadButton,
                new TextBlock { Text = "Theme", VerticalAlignment = VerticalAlignment.Center }, _themePicker }
        };
        _workspace = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(10) };
        Content = new DockPanel { Children = { header, new ScrollViewer { Content = _workspace } } };
        DockPanel.SetDock(header, Dock.Top);
        if (!string.IsNullOrWhiteSpace(PreviewOptions.PortalPath) && File.Exists(PreviewOptions.PortalPath))
            LoadArt();
        else
        {
            ShowGalleryAndTestPanel();
            GameArtImageExtension.CurrentSource = _art;
            ApplySelectedTheme();
        }
    }

    private void LoadArt()
    {
        try
        {
            var path = _pathBox.Text?.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException("Select an existing client_portal.dat file.");
            _portal?.Dispose();
            _portal = new PortalDat(path);
            _art = _portal;
            GameArtImageExtension.CurrentSource = _art;
            ShowGalleryAndTestPanel();
            ApplySelectedTheme();
        }
        catch (Exception exception)
        {
            Title = $"LegACEy Avalonia preview — {exception.Message}";
        }
    }

    private void ShowGalleryAndTestPanel()
    {
        _workspace.Children.Clear();
        var gallery = new ThemeGalleryControl();
        gallery.ThemeSwitchRequested += (_, _) =>
        {
            _themePicker.SelectedIndex = _themePicker.SelectedIndex == 0 ? 1 : 0;
        };
        var galleryChrome = new ThemeWindowChrome(_art, "Theme gallery", gallery) { Width = 580, Height = 560 };
        galleryChrome.CloseAndRemoveFrom(_workspace);
        _workspace.Children.Add(galleryChrome);
        var inputChrome = new ThemeWindowChrome(_art, "Input test", new InputTestPanel(360, 520)) { Width = 580, Height = 590, Margin = new Thickness(0, 0, 12, 0) };
        inputChrome.CloseAndRemoveFrom(_workspace);
        _workspace.Children.Add(inputChrome);
    }

    private void ApplySelectedTheme()
    {
        if (_themePicker.SelectedIndex < 0) return;
        if (_appliedTheme != null) Styles.Remove(_appliedTheme);
        IClientTheme theme = _themePicker.SelectedIndex == 0
            ? new AcClientTheme(_art)
            : new SimpleClientTheme();
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        _appliedTheme = theme.CreateStyles();
        Styles.Add(_appliedTheme);
        foreach (var chrome in _workspace.GetVisualDescendants().OfType<ThemeWindowChrome>())
            chrome.ApplyTheme(theme);
    }

    protected override void OnClosed(EventArgs e)
    {
        _portal?.Dispose();
        _portal = null;
        GameArtImageExtension.CurrentSource = null;
        base.OnClosed(e);
    }

    private sealed class MissingArtSource : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}

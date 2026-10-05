using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>A 500-entry fake pack for exercising list scrolling and upload cost in game.</summary>
public sealed class PerformanceListPanel : UserControl
{
    public const int PackItemCount = 500;
    public const uint PackIconId = 0x06007498;

    public PerformanceListPanel(IGameArtSource art)
    {
        if (art == null) throw new ArgumentNullException(nameof(art));
        var icon = GameArtImageExtension.CreateBitmap(art, PackIconId);
        var items = new List<PackItem>(PackItemCount);
        for (var index = 1; index <= PackItemCount; index++)
            items.Add(new PackItem($"Pack item {index:000}", icon));

        ItemsList = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<PackItem>((item, _) => item == null ? null : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Height = 28,
                Children =
                {
                    new Image { Source = item.Icon, Width = 20, Height = 20, Stretch = Avalonia.Media.Stretch.Uniform },
                    new TextBlock { Text = item.Name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
                }
            })
        };
        Content = ItemsList;
    }

    public ListBox ItemsList { get; }

    private sealed class PackItem
    {
        public PackItem(string name, Bitmap? icon) { Name = name; Icon = icon; }
        public string Name { get; }
        public Bitmap? Icon { get; }
    }
}

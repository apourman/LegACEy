using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Market;

/// <summary>Item icons drawn from the game's art: one bitmap per look, shared by the Browse and Sell screens and freed with the window.</summary>
public sealed class MarketIcons : IDisposable
{
    private readonly IGameArtSource _art;
    private readonly Dictionary<(ItemVisual Visual, uint OverlaySecondary), WriteableBitmap?> _bitmaps = new();

    public MarketIcons(IGameArtSource art) => _art = art ?? throw new ArgumentNullException(nameof(art));

    public Control For(ItemVisual visual, uint overlaySecondary)
    {
        var key = (visual, overlaySecondary);
        if (!_bitmaps.TryGetValue(key, out var bitmap))
            _bitmaps[key] = bitmap = GameArtImageExtension.CreateBitmap(
                ItemIcon.Draw(_art, visual.Underlay, visual.Icon, visual.Overlay, overlaySecondary, visual.UiEffects, visual.Plate));
        return DerethItemCells.Icon(bitmap);
    }

    public Control For(ListingCard card) => For(new ItemVisual(card.Icon, card.Underlay, card.Overlay, card.UiEffects, card.Plate), card.OverlaySecondary);

    public void Dispose()
    {
        foreach (var bitmap in _bitmaps.Values) bitmap?.Dispose();
        _bitmaps.Clear();
    }
}

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The appraisal window's Inspect view: the object's 3D model from the server's look. A creature's model stands in the inventory's
/// paperdoll, with what it wears in the slots round it; the Slots toggle swaps the model for the armour slots. Clicking a worn item
/// appraises it. Anything else is its model alone, on a stand the height of the doll.
/// </summary>
public sealed class AppraisalInspect : StackPanel
{
    private const double StandHeight = 234;

    private readonly IGameArtSource? _art;
    private readonly Action<WornGear> _appraise;
    private readonly PaperdollView _model;
    private readonly PaperdollCanvas? _doll;
    private IReadOnlyList<WornGear> _gear = Array.Empty<WornGear>();
    private bool _showSlots;

    public AppraisalInspect(IServerChannel channel, string portalPath, IGameArtSource? art, uint objectId, bool creature, Action<WornGear> appraise)
    {
        _art = art;
        _appraise = appraise ?? throw new ArgumentNullException(nameof(appraise));
        Margin = new Thickness(10, 8, 10, 6);
        Spacing = 6;
        _model = new PaperdollView(channel, portalPath, objectId);
        if (creature)
        {
            _doll = new PaperdollCanvas { HorizontalAlignment = HorizontalAlignment.Center };
            _doll.SlotsPressed += () =>
            {
                _showSlots = !_showSlots;
                Render();
            };
            _model.GearRead += gear =>
            {
                _gear = gear;
                Render();
            };
            Render();
            Children.Add(_doll);
        }
        else
        {
            Children.Add(new Border
            {
                Height = StandHeight, CornerRadius = new CornerRadius(3), BorderBrush = DerethPalette.GrooveEdgeBrush, BorderThickness = new Thickness(1),
                Child = _model,
            });
        }
        var hint = new TextBlock
        {
            Text = creature ? "Drag to turn · click a slot to appraise it" : "Drag to turn", FontSize = 11.5, FontFamily = DerethPalette.Body,
            Foreground = DerethPalette.MutedBrush, HorizontalAlignment = HorizontalAlignment.Center,
        };
        Children.Add(hint);
    }

    private void Render()
    {
        if (_doll == null) return;
        var worn = new Dictionary<PaperdollSlot, WornGear>();
        foreach (var item in _gear)
            foreach (var slot in InventorySnapshotBuilder.SlotsFor(item.Location))
                worn[slot] = item;
        _doll.Show(slot => worn.TryGetValue(slot, out var item) ? Worn(item) : new DerethSlot(PaperdollGlyphs.For(slot)), _showSlots);
        _doll.SetSlotsOn(_showSlots);
        _doll.DollArea.Child = _showSlots ? null : _model;
    }

    private Control Worn(WornGear item)
    {
        var image = _art == null ? null : ItemIcon.Draw(_art, item.Visual.Underlay, item.Visual.Icon, item.Visual.Overlay, 0, item.Visual.UiEffects, item.Visual.Plate);
        var slot = new DerethSlot(DerethItemCells.Icon(image == null ? null : GameArtImageExtension.CreateBitmap(image)));
        ToolTip.SetTip(slot, item.Name);
        slot.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) _appraise(item);
        };
        return slot;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// A 3D look in a <see cref="ModelView"/>: the player's own, or any object's. It asks its <see cref="ILookSource"/> how the object
/// looks, builds the model from portal.dat off the game thread, and asks again whenever the look changes. It asks only while it is in a visual tree; the owner must take
/// it out of the tree to stop it asking. The status line shows while the model loads or cannot be had.
/// </summary>
public sealed class PaperdollView : Border
{
    private readonly ILookSource _looks;
    private readonly string _portalPath;
    private readonly TextBlock _status;
    private readonly ModelView _view;
    private IDisposable? _changed;
    // Counts requests and leaving the tree: a reply or build from an older request, or from before the view left, is dropped.
    private int _request;
    private readonly uint _target;
    // A still doll's look and its body's clothing tables, once built: what works out which worn item draws which part.
    private (CharacterAppearance Look, ClothingTables Tables)? _clothing;
    // The last answer of PartsDrawn, which a drag asks for on every move: it holds until the claims or the look change.
    private (List<IReadOnlyCollection<int>> Claims, IReadOnlyList<IReadOnlyCollection<int>> Drawn)? _drawn;
    // The clothing tables of each portal.dat and body setup, read once: a few thousand small reads.
    private static readonly Dictionary<(string Path, uint Setup), ClothingTables> Tables = new();

    /// <param name="target">The object to show, zero for the player.</param>
    public PaperdollView(ILookSource looks, string portalPath, uint target = 0)
    {
        _looks = looks ?? throw new ArgumentNullException(nameof(looks));
        _portalPath = portalPath;
        _target = target;
        _status = new TextBlock { Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _view = new ModelView { Child = _status };
        Child = _view;
        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => Stop();
    }

    /// <summary>Turns, zooms and moves the view back to its first one: facing the viewer, the whole character, centred.</summary>
    public void ResetView() => _view.ResetView();

    /// <summary>
    /// The inventory's doll, as retail's: the character holds still in retail's stance facing the viewer, without what it holds, and takes no pointer input,
    /// so the window under it gets the presses and picks the body part with <see cref="PartAt"/>.
    /// </summary>
    public bool Still
    {
        get => !_view.IsHitTestVisible;
        set => _view.IsHitTestVisible = !value;
    }

    /// <summary>Whether the status line shows while the model loads or cannot be had; off for a figure drawn behind other text.</summary>
    public bool StatusShown
    {
        get => _status.IsVisible;
        set => _status.IsVisible = value;
    }

    /// <summary>The body part (a setup part number) drawn at a point of this view, or -1.</summary>
    public int PartAt(Avalonia.Point point) => _view.PartAt(point);

    /// <summary>
    /// The body parts each worn item draws on a still doll, found from the clothing tables in portal.dat (see
    /// <see cref="ClothingTables.PartsDrawn"/>). <paramref name="claims"/> holds each worn item's parts by its coverage. Empty for every
    /// item until the doll is built.
    /// </summary>
    public IReadOnlyList<IReadOnlyCollection<int>> PartsDrawn(IReadOnlyList<IReadOnlyCollection<int>> claims)
    {
        if (_clothing is not { } clothing)
        {
            var none = new IReadOnlyCollection<int>[claims.Count];
            for (var i = 0; i < none.Length; i++) none[i] = Array.Empty<int>();
            return none;
        }
        if (_drawn is { } last && last.Claims.Count == claims.Count && last.Claims.Zip(claims, (a, b) => a.SequenceEqual(b)).All(same => same))
            return last.Drawn;
        var drawn = clothing.Tables.PartsDrawn(clothing.Look, claims);
        _drawn = (claims.ToList(), drawn);
        return drawn;
    }

    /// <summary>Outlines the body parts in the colour (RGB), or none for an empty set.</summary>
    public void Outline(IReadOnlyCollection<int> parts, uint colour)
    {
        _view.Outlined = parts;
        _view.OutlineColour = colour;
    }

    private ClothingTables TablesFor(PortalDat dat, uint setup)
    {
        lock (Tables)
        {
            if (!Tables.TryGetValue((_portalPath, setup), out var tables))
                Tables[(_portalPath, setup)] = tables = ClothingTables.Read(dat, setup);
            return tables;
        }
    }

    private void Start()
    {
        _changed = _looks.WhenChanged(_target, Refresh);
        Refresh();
    }

    private void Stop()
    {
        _changed?.Dispose();
        _changed = null;
        _request++;
    }

    private void Refresh()
    {
        if (_view.Model == null) _status.Text = "Loading…";
        var request = ++_request;
        var still = Still;
        _looks.Request(_target, (appearance, why) =>
        {
            if (request != _request) return;
            if (appearance == null)
            {
                _status.Text = why;
                return;
            }
            // ~0.25 s of dat reads and decoding: off the game thread, with its own file handle
            Task.Run(() =>
            {
                using var dat = new PortalDat(_portalPath);
                var model = still
                    ? CharacterModel.Build(dat, new CharacterAppearance(appearance.SetupId, appearance.PaletteId, appearance.SubPalettes,
                        appearance.TextureChanges, appearance.PartChanges), CharacterModel.PaperDollPose)
                    : CharacterModel.Build(dat, appearance);
                return (Model: model, Tables: still ? TablesFor(dat, appearance.SetupId) : null);
            }).ContinueWith(task => Dispatcher.UIThread.Post(() =>
            {
                if (request != _request) return;
                if (task.Exception != null)
                {
                    _status.Text = $"Could not build the model: {task.Exception.GetBaseException().Message}";
                    return;
                }
                _view.Model = task.Result.Model;
                _clothing = task.Result.Tables is { } tables ? (appearance, tables) : null;
                _drawn = null;
                _status.Text = string.Empty;
            }), TaskScheduler.Default);
        });
    }
}

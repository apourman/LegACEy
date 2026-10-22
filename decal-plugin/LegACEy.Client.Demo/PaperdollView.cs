using System;
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

    /// <summary>The wheel zooms toward the head rather than the pointer: see <see cref="ModelView.ZoomsToTop"/>.</summary>
    public bool ZoomsToTop
    {
        get => _view.ZoomsToTop;
        set => _view.ZoomsToTop = value;
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
                return CharacterModel.Build(dat, appearance);
            }).ContinueWith(task => Dispatcher.UIThread.Post(() =>
            {
                if (request != _request) return;
                if (task.Exception != null)
                {
                    _status.Text = $"Could not build the model: {task.Exception.GetBaseException().Message}";
                    return;
                }
                _view.Model = task.Result;
                _status.Text = string.Empty;
            }), TaskScheduler.Default);
        });
    }
}

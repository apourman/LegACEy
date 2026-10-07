using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>Bodies of the paperdoll's channel actions; they must match ACE.Server.ClientChannel.PaperdollChannelActions.</summary>
public static class PaperdollProtocol
{
    public const string Look = "paperdoll.look";
    public const string Changed = "paperdoll.changed";

    public static CharacterAppearance ReadLook(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var setup = reader.ReadUInt32();
        var palette = Known(reader.ReadUInt32(), 0x04000000);
        var palettes = new List<SubPalette>();
        for (int i = 0, count = reader.ReadUInt16(); i < count; i++)
            palettes.Add(new SubPalette(Known(reader.ReadUInt32(), 0x04000000), reader.ReadUInt16(), reader.ReadUInt16()));
        var textures = new List<TextureChange>();
        for (int i = 0, count = reader.ReadUInt16(); i < count; i++)
            textures.Add(new TextureChange(reader.ReadByte(), Known(reader.ReadUInt32(), 0x05000000), Known(reader.ReadUInt32(), 0x05000000)));
        var parts = new List<PartChange>();
        for (int i = 0, count = reader.ReadUInt16(); i < count; i++)
            parts.Add(new PartChange(reader.ReadByte(), Known(reader.ReadUInt32(), 0x01000000)));
        return new CharacterAppearance(setup, palette, palettes, textures, parts);
    }

    /// <summary>
    /// ACE keeps some ids without their file type (clothing palettes are cut to 16 bits); the retail wire's
    /// "packed dword of known type" puts it back, and so does this.
    /// </summary>
    private static uint Known(uint id, uint type) => id != 0 && (id & 0xFF000000) == 0 ? id | type : id;
}

/// <summary>
/// The 3D paperdoll prototype: asks the server how the player looks, builds the model from portal.dat off the game
/// thread, and turns and zooms it with the mouse. The host draws the model on the game device over <see cref="Viewport"/>.
/// </summary>
public sealed class PaperdollPanel : UserControl, IDisposable
{
    public const int WindowWidth = 300;
    public const int WindowHeight = 440;

    private readonly IServerChannel _channel;
    private readonly string _portalPath;
    private readonly IDisposable _changed;
    private readonly TextBlock _status;
    private Point? _dragFrom;
    private int _build;
    private bool _disposed;

    public PaperdollPanel(IServerChannel channel, string portalPath)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _portalPath = portalPath;
        _status = new TextBlock { Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Viewport = new Border { Background = Brushes.Transparent, ClipToBounds = true, Child = _status };
        Viewport.PointerPressed += (_, e) => { _dragFrom = e.GetPosition(Viewport); e.Pointer.Capture(Viewport); e.Handled = true; };
        Viewport.PointerMoved += (_, e) =>
        {
            if (_dragFrom is not { } from) return;
            var point = e.GetPosition(Viewport);
            Yaw += (float)(point.X - from.X) * 0.01f;
            _dragFrom = point;
        };
        Viewport.PointerReleased += (_, e) => { _dragFrom = null; e.Pointer.Capture(null); };
        Viewport.PointerWheelChanged += (_, e) => { Zoom = Math.Max(0.6f, Math.Min(3f, Zoom * (e.Delta.Y > 0 ? 1.1f : 1 / 1.1f))); e.Handled = true; };

        var hint = new TextBlock { Text = "Drag to turn · wheel to zoom", Foreground = Brushes.Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var layout = new DockPanel();
        DockPanel.SetDock(hint, Dock.Bottom);
        layout.Children.Add(hint);
        layout.Children.Add(Viewport);
        Content = layout;

        _changed = _channel.Subscribe(PaperdollProtocol.Changed, _ => Refresh());
        Refresh();
    }

    /// <summary>Where the model is drawn.</summary>
    public Control Viewport { get; }

    /// <summary>The model to draw, or null while it loads. Replaced as a whole, from the UI thread.</summary>
    public CharacterModel? Model { get; private set; }

    /// <summary>Turn around Z, in radians. Starts facing the viewer (models face +Y; the camera looks along +Y).</summary>
    public float Yaw { get; private set; } = (float)Math.PI;

    public float Zoom { get; private set; } = 1f;

    public void Refresh()
    {
        if (_disposed) return;
        if (!_channel.IsAvailable)
        {
            _status.Text = "The LegACEy server channel is not available in this client.";
            return;
        }
        if (Model == null) _status.Text = "Loading…";
        _channel.Request(PaperdollProtocol.Look, Array.Empty<byte>(), reply =>
        {
            if (_disposed) return;
            if (!reply.Ok)
            {
                _status.Text = $"The server didn't answer: {reply.Message}";
                return;
            }
            var appearance = PaperdollProtocol.ReadLook(reply.Body);
            var build = ++_build;
            // ~0.25 s of dat reads and decoding: off the game thread, with its own file handle
            Task.Run(() =>
            {
                using var dat = new PortalDat(_portalPath);
                return CharacterModel.Build(dat, appearance);
            }).ContinueWith(task => Dispatcher.UIThread.Post(() =>
            {
                if (_disposed || build != _build) return;
                if (task.Exception != null)
                {
                    _status.Text = $"Could not build the model: {task.Exception.GetBaseException().Message}";
                    return;
                }
                Model = task.Result;
                _status.Text = string.Empty;
            }), TaskScheduler.Default);
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _changed.Dispose();
    }
}

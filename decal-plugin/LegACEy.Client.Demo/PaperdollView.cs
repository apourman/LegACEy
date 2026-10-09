using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
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
/// The character's 3D look in a <see cref="ModelView"/>: asks the server how the player looks, builds the model from portal.dat
/// off the game thread, and asks again on each paperdoll.changed. It asks only while it is in a visual tree, so a window that
/// hides it (or takes it out) stops asking and stops listening. The status line shows while the model loads or cannot be had.
/// </summary>
public sealed class PaperdollView : Border
{
    private readonly IServerChannel _channel;
    private readonly string _portalPath;
    private readonly TextBlock _status;
    private readonly ModelView _view;
    private IDisposable? _changed;
    // Bumped when the view leaves the tree, so a reply or build still in flight from before is dropped.
    private int _generation;
    private int _build;

    public PaperdollView(IServerChannel channel, string portalPath)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _portalPath = portalPath;
        _status = new TextBlock { Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _view = new ModelView { Child = _status };
        Child = _view;
        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => Stop();
    }

    private void Start()
    {
        _changed = _channel.Subscribe(PaperdollProtocol.Changed, _ => Refresh());
        Refresh();
    }

    private void Stop()
    {
        _changed?.Dispose();
        _changed = null;
        _generation++;
    }

    private void Refresh()
    {
        if (!_channel.IsAvailable)
        {
            _status.Text = "The LegACEy server channel is not available in this client.";
            return;
        }
        if (_view.Model == null) _status.Text = "Loading…";
        var generation = _generation;
        _channel.Request(PaperdollProtocol.Look, Array.Empty<byte>(), reply =>
        {
            if (generation != _generation) return;
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
                if (generation != _generation || build != _build) return;
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

using System;
using System.Collections.Generic;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>Where the paperdoll gets an object's look. Zero is the player.</summary>
public interface ILookSource
{
    /// <summary>Whether <see cref="Request"/> can have the object's look, for showing an Inspect button.</summary>
    bool Knows(uint objectId);
    /// <summary>Asks for the object's look; the reply is the look, or null and why there is none.</summary>
    void Request(uint objectId, Action<CharacterAppearance?, string> reply);
    /// <summary>Calls <paramref name="changed"/> when the object's look may have changed, until disposed.</summary>
    IDisposable WhenChanged(uint objectId, Action changed);
    /// <summary>Asks which worn item draws each body part of the player's look; the reply is null where that is not known.</summary>
    void RequestParts(Action<IReadOnlyDictionary<int, uint>?> reply);
}

/// <summary>
/// The look the game sent (<see cref="LookCache"/>), which every server sends; otherwise the LegACEy server's paperdoll actions, which
/// also know objects outside the world (the Vault's).
/// </summary>
public sealed class GameLooks : ILookSource
{
    private readonly LookCache _cache;
    private readonly Func<IServerChannel> _channel;
    private readonly Func<string, bool> _serverHas;
    private readonly Func<uint> _player;

    /// <param name="serverHas">Whether the server registered a channel action.</param>
    /// <param name="player">The player's object id.</param>
    public GameLooks(LookCache cache, Func<IServerChannel> channel, Func<string, bool> serverHas, Func<uint> player)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _serverHas = serverHas ?? throw new ArgumentNullException(nameof(serverHas));
        _player = player ?? throw new ArgumentNullException(nameof(player));
    }

    private uint Resolve(uint objectId) => objectId == 0 ? _player() : objectId;
    private static string Action(uint objectId) => objectId == 0 ? PaperdollProtocol.Look : PaperdollProtocol.Inspect;

    public bool Knows(uint objectId) => _cache.Look(Resolve(objectId)) != null || _serverHas(Action(objectId));

    public void Request(uint objectId, Action<CharacterAppearance?, string> reply)
    {
        if (_cache.Look(Resolve(objectId)) is { } look)
        {
            reply(look, string.Empty);
            return;
        }
        var channel = _channel();
        if (!_serverHas(Action(objectId)) || !channel.IsAvailable)
        {
            reply(null, "The game hasn't sent how this looks.");
            return;
        }
        var body = objectId == 0 ? Array.Empty<byte>() : PaperdollProtocol.InspectBody(objectId);
        channel.Request(Action(objectId), body, answer =>
        {
            if (!answer.Ok)
            {
                reply(null, $"The server didn't answer: {answer.Message}");
                return;
            }
            CharacterAppearance read;
            try { read = PaperdollProtocol.ReadLook(answer.Body); }
            catch (Exception exception)
            {
                // A garbled reply is shown, not thrown: a throw would turn off the plugin that asked.
                reply(null, $"The server sent a look this client can't read: {exception.Message}");
                return;
            }
            reply(read, string.Empty);
        });
    }

    /// <summary>Only the LegACEy server knows: the look the game sends merges every worn item into one.</summary>
    public void RequestParts(Action<IReadOnlyDictionary<int, uint>?> reply)
    {
        var channel = _channel();
        if (!_serverHas(PaperdollProtocol.Parts) || !channel.IsAvailable)
        {
            reply(null);
            return;
        }
        channel.Request(PaperdollProtocol.Parts, Array.Empty<byte>(), answer =>
        {
            IReadOnlyDictionary<int, uint>? parts = null;
            // A refused or garbled reply is not thrown, which would turn off the plugin that asked: the parts are just not known.
            try { if (answer.Ok) parts = PaperdollProtocol.ReadParts(answer.Body); }
            catch (Exception) { }
            reply(parts);
        });
    }

    public IDisposable WhenChanged(uint objectId, Action changed)
    {
        Action<uint> handler = id => { if (id == Resolve(objectId)) changed(); };
        _cache.Changed += handler;
        // The server's push covers a player whose look the game sent before the cache was listening.
        var pushed = objectId == 0 ? _channel().Subscribe(PaperdollProtocol.Changed, _ => changed()) : null;
        return new Unsubscribe(() =>
        {
            _cache.Changed -= handler;
            pushed?.Dispose();
        });
    }

    private sealed class Unsubscribe : IDisposable
    {
        private Action? _undo;
        public Unsubscribe(Action undo) => _undo = undo;
        public void Dispose()
        {
            _undo?.Invoke();
            _undo = null;
        }
    }
}

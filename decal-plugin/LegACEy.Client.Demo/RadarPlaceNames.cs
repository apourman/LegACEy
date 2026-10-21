using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LegACEy.Client.Demo;

/// <summary>
/// The server's names for places, for the radar's plaque. A landblock is asked once when the radar first needs it, and the answer is kept
/// until the next hello: the window asks on the UI thread every redraw, so this never waits on the server. Asked only when hello listed
/// <see cref="PlaceProtocol.Name"/>; otherwise every name is the list's. A failed answer keeps no name for the login.
/// </summary>
public sealed class RadarPlaceNames
{
    private readonly Dictionary<ushort, string?> _names = new();
    private IServerChannel? _channel;
    // Bumped by each hello, so an answer to an earlier login lands nowhere.
    private int _login;

    /// <summary>Starts a login: forgets the answers, and asks the channel only when the server listed the action.</summary>
    public void Reset(IServerChannel? channel, IEnumerable<string> actions)
    {
        _login++;
        _names.Clear();
        _channel = channel != null && actions.Contains(PlaceProtocol.Name) ? channel : null;
    }

    /// <summary>The server's name for the cell's place, or null while it is not known or the server names none.</summary>
    public string? NameOf(uint cell)
    {
        var landblock = (ushort)(cell >> 16);
        if (_names.TryGetValue(landblock, out var known)) return known;
        if (_channel is not { } channel) return null;

        // Marked as asked before the request goes, so a reply sent at once is kept and the next redraw does not ask again.
        _names[landblock] = null;
        var login = _login;
        channel.Request(PlaceProtocol.Name, PlaceProtocol.Request(cell), reply =>
        {
            if (login == _login) _names[landblock] = reply.Ok ? Read(reply.Body) : null;
        });
        return null;
    }

    private static string? Read(byte[] body)
    {
        try
        {
            var name = PlaceProtocol.ReadName(body);
            return name.Length == 0 ? null : name;
        }
        catch (Exception error) when (error is EndOfStreamException || error is InvalidDataException) { return null; }
    }
}

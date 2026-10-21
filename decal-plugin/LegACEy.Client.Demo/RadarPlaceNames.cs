using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LegACEy.Client.Demo;

/// <summary>
/// The server's name for the place the player is in, for the radar's plaque. Asked when the landblock changes, and only when hello listed
/// <see cref="PlaceProtocol.Name"/>; otherwise every name is the list's. A landblock is one visit: the answer is kept while the player stays
/// in it and dropped when they leave, so a dungeon copy never keeps the base world's name (the two share landblock ids). A failed answer
/// keeps no name for that visit only, and the next landblock change asks again. The window asks on the UI thread every redraw, so this
/// never waits on the server.
/// </summary>
public sealed class RadarPlaceNames
{
    private IServerChannel? _channel;
    // Bumped by each hello, so an answer to an earlier login lands nowhere.
    private int _login;
    private ushort? _landblock;
    // Bumped by each change of landblock, so an answer to an earlier visit lands nowhere.
    private int _visit;
    private string? _name;

    /// <summary>Starts a login: forgets the answer, and asks the channel only when the server listed the action.</summary>
    public void Reset(IServerChannel? channel, IEnumerable<string> actions)
    {
        _login++;
        _landblock = null;
        _name = null;
        _channel = channel != null && actions.Contains(PlaceProtocol.Name) ? channel : null;
    }

    /// <summary>The server's name for the cell's place, or null while it is not known or the server names none.</summary>
    public string? NameOf(uint cell)
    {
        var landblock = (ushort)(cell >> 16);
        if (landblock == _landblock) return _name;

        _landblock = landblock;
        _visit++;
        _name = null;
        if (_channel is not { } channel) return null;

        var login = _login;
        var visit = _visit;
        channel.Request(PlaceProtocol.Name, PlaceProtocol.Request(cell), reply =>
        {
            if (login == _login && visit == _visit) _name = reply.Ok ? Read(reply.Body) : null;
        });
        return _name;
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

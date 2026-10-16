using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LegACEy.Client.GameArt;

/// <summary>
/// The ground tiles around the player. <see cref="Follow"/> names the player's landblock; the 3 × 3 landblocks around it are
/// built on the thread pool and every other tile is dropped. <see cref="Tile"/> only reads what has been built, so the UI thread
/// never waits on a DAT.
/// </summary>
public sealed class GroundTiles
{
    private readonly IGroundSource _source;
    private readonly object _gate = new();
    private readonly HashSet<ushort> _wanted = new();
    private readonly HashSet<ushort> _building = new();
    private readonly Dictionary<ushort, GameImage?> _tiles = new(); // a null entry is a landblock that could not be drawn
    private ushort? _centre;

    public GroundTiles(IGroundSource source) => _source = source;

    /// <summary>
    /// The player is in landblock <paramref name="landblock"/> (0xXXYY: the high 16 bits of its id). Cheap when the landblock
    /// hasn't changed; otherwise it drops the tiles outside the 3 × 3 and starts the missing ones.
    /// </summary>
    public void Follow(ushort landblock)
    {
        lock (_gate)
        {
            if (_centre == landblock) return;
            _centre = landblock;
            _wanted.Clear();
            _wanted.Add(landblock);
            foreach (var (nx, ny) in Neighbours(landblock >> 8, landblock & 0xFF)) _wanted.Add((ushort)((nx << 8) | ny));
            foreach (var id in new List<ushort>(_tiles.Keys))
                if (!_wanted.Contains(id)) _tiles.Remove(id);
            foreach (var id in _wanted)
                if (!_tiles.ContainsKey(id) && _building.Add(id)) Task.Run(() => Build(id));
        }
    }

    /// <summary>The tile for a landblock once it has been built, else null. Never blocks.</summary>
    public GameImage? Tile(ushort landblock)
    {
        lock (_gate) return _tiles.TryGetValue(landblock, out var tile) ? tile : null;
    }

    private void Build(ushort id)
    {
        lock (_gate)
        {
            if (!_wanted.Contains(id))
            {
                _building.Remove(id);
                return;
            }
        }

        GameImage? tile = null;
        try
        {
            var x = id >> 8;
            var y = id & 0xFF;
            var centre = _source.ReadLandblock(x, y);
            if (centre != null)
            {
                var around = new List<GroundLandblock> { centre };
                foreach (var (nx, ny) in Neighbours(x, y))
                {
                    var neighbour = _source.ReadLandblock(nx, ny);
                    if (neighbour != null) around.Add(neighbour);
                }
                tile = GroundTileRenderer.Render(centre, around, _source.TerrainTexture);
            }
        }
        catch (Exception)
        {
            // A landblock whose DATs can't be read has no ground, and the radar falls back to dots on dark.
            tile = null;
        }

        lock (_gate)
        {
            _building.Remove(id);
            if (_wanted.Contains(id)) _tiles[id] = tile;
        }
    }

    private static IEnumerable<(int X, int Y)> Neighbours(int x, int y)
    {
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if ((dx != 0 || dy != 0) && nx is >= 0 and <= 254 && ny is >= 0 and <= 254) yield return (nx, ny);
            }
    }
}

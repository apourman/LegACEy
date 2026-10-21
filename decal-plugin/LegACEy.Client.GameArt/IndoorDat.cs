using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LegACEy.Client.GameArt;

// The polygons' vertices by cell structure, by environment: what IndoorDat keeps for the environments it has read in one plan.
using Environments = Dictionary<uint, Dictionary<uint, Dictionary<ushort, Vec3[]>>>;

/// <summary>
/// The indoor plans' DATs, read as ACE.DatLoader reads them: client_cell_1.dat for a landblock's cell count (LandblockInfo) and each
/// cell's environment, frame and portals (EnvCell), and client_portal.dat for an environment's cell structures (Environment, CellStruct).
/// Vertices and polygons are read; BSP trees are stepped over, not parsed. It opens its own copies of both DATs, so it never waits on
/// another reader's stream.
/// </summary>
public sealed class IndoorDat : IDisposable
{
    private const uint FirstCell = 0x0100;
    private const uint LastCell = 0xFFFE;
    private const uint EnvironmentBase = 0x0D000000;
    private const byte NoPositiveUvs = 0x04; // StipplingType: the positive side has no UVs
    private const byte NoNegativeUvs = 0x08; // StipplingType: the negative side has no UVs
    private const int ClockwiseCull = 0x02; // CullMode: a polygon with two sides
    private const long PlaneSize = 16; // a normal and a distance
    private const long SphereSize = 16; // a centre and a radius

    private enum Tree { Cell, Physics, Drawing }

    private readonly PortalDat _portal;
    private readonly PortalDat _cell;
    private readonly Action<string>? _log;

    /// <param name="log">Told when some of a landblock's cells could not be read; the plan is built without them.</param>
    public IndoorDat(string portalPath, string cellPath, Action<string>? log = null)
    {
        _portal = new PortalDat(portalPath);
        try
        {
            _cell = new PortalDat(cellPath);
        }
        catch
        {
            // The constructor failed, so there is no IndoorDat to dispose: close the portal it opened.
            _portal.Dispose();
            throw;
        }
        _log = log;
    }

    /// <summary>
    /// The landblock's indoor plan, empty where it has no cells. This reads the DATs, so call it off the UI thread
    /// (<see cref="IndoorPlanCache"/> does). Safe to call from several threads at once: each call keeps its own environments, and
    /// the DATs serialise their own reads.
    /// </summary>
    public IndoorPlan ReadPlan(int x, int y) => IndoorPlan.Build(x, y, ReadShapes(x, y, new Environments()));

    public void Dispose()
    {
        _portal.Dispose();
        _cell.Dispose();
    }

    /// <summary>
    /// The landblock's cells, from 0x0100 for the count LandblockInfo gives. A cell that fails to read, or whose structure is missing,
    /// is left out; the landblock's failures are logged once.
    /// </summary>
    private List<IndoorCellShape> ReadShapes(int x, int y, Environments environments)
    {
        var shapes = new List<IndoorCellShape>();
        if (x is < 0 or > 254 || y is < 0 or > 254) return shapes;
        var landblock = ((uint)x << 24) | ((uint)y << 16);
        var info = _cell.ReadFile(landblock | 0xFFFE);
        if (info == null) return shapes;

        // LandblockInfo: its id, then NumCells. Cell ids end at 0xFFFE, so the count is clamped there and no id spills into the next landblock.
        var count = Math.Min(BitConverter.ToUInt32(info, 4), LastCell - FirstCell + 1);
        var failures = 0;
        string? firstFailure = null;
        for (var id = FirstCell; id < FirstCell + count; id++)
        {
            try
            {
                if (ReadCell(environments, landblock | id) is { } shape) shapes.Add(shape);
            }
            catch (Exception exception)
            {
                failures++;
                firstFailure ??= exception.Message;
            }
        }
        if (failures > 0) _log?.Invoke($"{failures} cells of landblock {x:X2}{y:X2} could not be read: {firstFailure}");
        return shapes;
    }

    /// <summary>EnvCell: its environment and cell structure, its frame and its portals. Null where the cell is missing.</summary>
    private IndoorCellShape? ReadCell(Environments environments, uint id)
    {
        var data = _cell.ReadFile(id);
        if (data == null) return null;

        var reader = new BinaryReader(new MemoryStream(data));
        reader.ReadUInt32(); // the id
        reader.ReadUInt32(); // the flags
        reader.ReadUInt32(); // the cell id again
        var surfaces = reader.ReadByte();
        var portalCount = reader.ReadByte();
        reader.ReadUInt16(); // the number of visible cells, which are not needed
        reader.BaseStream.Seek(2 * surfaces, SeekOrigin.Current); // the surfaces
        var environment = EnvironmentBase | reader.ReadUInt16();
        var structure = reader.ReadUInt16();
        var frame = DatModels.ReadFrame(reader);

        var portals = new List<(ushort Polygon, ushort OtherCell)>();
        for (var i = 0; i < portalCount; i++)
        {
            reader.ReadUInt16(); // the flags
            var polygon = reader.ReadUInt16();
            var otherCell = reader.ReadUInt16();
            reader.ReadUInt16(); // its portal in the other cell
            portals.Add((polygon, otherCell));
        }

        if (!ReadEnvironment(environments, environment).TryGetValue(structure, out var polygons)) return null;
        return new IndoorCellShape((ushort)id, frame, polygons, portals);
    }

    /// <summary>An environment's cell structures by id, each as its polygons' vertices, read once per plan. Empty where the portal has no such environment.</summary>
    private Dictionary<uint, Dictionary<ushort, Vec3[]>> ReadEnvironment(Environments environments, uint id)
    {
        if (environments.TryGetValue(id, out var known)) return known;

        var structures = new Dictionary<uint, Dictionary<ushort, Vec3[]>>();
        var data = _portal.ReadFile(id);
        if (data != null)
        {
            var reader = new BinaryReader(new MemoryStream(data));
            reader.ReadUInt32(); // the id
            for (var count = reader.ReadUInt32(); count > 0; count--)
            {
                var key = reader.ReadUInt32();
                structures[key] = ReadCellStructure(reader);
            }
        }
        environments[id] = structures;
        return structures;
    }

    /// <summary>CellStruct: its vertices, then its polygons, read in place. Its portals and BSP trees are stepped over.</summary>
    private static Dictionary<ushort, Vec3[]> ReadCellStructure(BinaryReader reader)
    {
        var polygonCount = reader.ReadUInt32();
        var physicsPolygonCount = reader.ReadUInt32();
        var portalCount = reader.ReadUInt32();

        reader.ReadInt32(); // the vertex type, always 1
        var vertices = new Dictionary<ushort, Vec3>();
        for (var count = reader.ReadUInt32(); count > 0; count--)
        {
            var key = reader.ReadUInt16();
            var uvs = reader.ReadUInt16();
            vertices[key] = DatModels.ReadVector(reader);
            reader.BaseStream.Seek(12 + (8 * uvs), SeekOrigin.Current); // the normal, then the UVs
        }

        var polygons = new Dictionary<ushort, Vec3[]>();
        for (var i = 0u; i < polygonCount; i++)
        {
            var key = reader.ReadUInt16();
            polygons[key] = ReadPolygonVertexIds(reader).Select(id => vertices[(ushort)id]).ToArray();
        }

        reader.BaseStream.Seek(2 * portalCount, SeekOrigin.Current); // the portal polygons, which the cells' portals name
        Align(reader);
        SkipTree(reader, Tree.Cell);
        for (var i = 0u; i < physicsPolygonCount; i++)
        {
            reader.ReadUInt16(); // the key
            ReadPolygonVertexIds(reader);
        }
        SkipTree(reader, Tree.Physics);
        if (reader.ReadUInt32() != 0) SkipTree(reader, Tree.Drawing);
        Align(reader);
        return polygons;
    }

    /// <summary>
    /// A polygon's vertex ids. Its UVs are stepped over; the flags that say whether they are stored are read here, since they
    /// decide how many bytes follow.
    /// </summary>
    private static short[] ReadPolygonVertexIds(BinaryReader reader)
    {
        var count = reader.ReadByte();
        var stippling = reader.ReadByte();
        var cull = reader.ReadInt32();
        reader.ReadInt16(); // the positive surface
        reader.ReadInt16(); // the negative surface
        var ids = new short[count];
        for (var i = 0; i < count; i++) ids[i] = reader.ReadInt16();
        if ((stippling & NoPositiveUvs) == 0) reader.BaseStream.Seek(count, SeekOrigin.Current);
        if (cull == ClockwiseCull && (stippling & NoNegativeUvs) == 0) reader.BaseStream.Seek(count, SeekOrigin.Current);
        return ids;
    }

    /// <summary>
    /// Steps over one BSP tree, as ACE.DatLoader lays it out, without keeping anything from it: a node is its tag, a plane and its
    /// children; a leaf is its index (a physics leaf adds its solid flag, sphere and polygons); a node adds a sphere and polygons
    /// except in a cell tree; a portal node adds, in a drawing tree, its sphere, polygons and portals.
    /// </summary>
    private static void SkipTree(BinaryReader reader, Tree tree)
    {
        var tag = ReadTag(reader);
        if (tag == "LEAF")
        {
            reader.ReadInt32(); // the leaf's index
            if (tree == Tree.Physics)
            {
                reader.ReadInt32(); // solid
                Skip(reader, SphereSize);
                SkipPolygonIds(reader);
            }
            return;
        }

        Skip(reader, PlaneSize);
        if (tag == "PORT")
        {
            SkipTree(reader, tree);
            SkipTree(reader, tree);
            if (tree == Tree.Drawing)
            {
                Skip(reader, SphereSize);
                var polygons = reader.ReadUInt32();
                var portals = reader.ReadUInt32();
                Skip(reader, 2 * polygons); // the polygons in it
                Skip(reader, 4 * portals); // its portals: two shorts each
            }
            return;
        }

        if (tag is "BPnn" or "BPIn" or "BPIN" or "BPnN") SkipTree(reader, tree); // the positive child
        if (tag is "BpIN" or "BpnN" or "BPIN" or "BPnN") SkipTree(reader, tree); // the negative child
        if (tree == Tree.Cell) return;

        Skip(reader, SphereSize);
        if (tree == Tree.Physics) return;
        SkipPolygonIds(reader); // the polygons in it, as shorts
    }

    /// <summary>A count, then that many shorts.</summary>
    private static void SkipPolygonIds(BinaryReader reader) => Skip(reader, 2 * reader.ReadUInt32());

    private static string ReadTag(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        Array.Reverse(bytes); // the tags are stored backwards
        return Encoding.ASCII.GetString(bytes);
    }

    private static void Skip(BinaryReader reader, long bytes) => reader.BaseStream.Seek(bytes, SeekOrigin.Current);

    /// <summary>Aligns the stream to four bytes, as the DAT stores it.</summary>
    private static void Align(BinaryReader reader) => reader.BaseStream.Position = (reader.BaseStream.Position + 3) / 4 * 4;
}

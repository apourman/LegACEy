using System;
using System.Collections.Generic;
using System.Linq;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The radar's port over the installed client. The player and the objects are read from retail's physics objects each call, as retail's
/// radar reads them each frame (gmRadarUI::DrawObjects): the objects are CObjectMaint's, by id, with their weenie's radar behaviour and the
/// colour retail's own GetBlipColor gives them. Names come from Decal's WorldFilter. Selecting goes through Decal's SelectItem; the UI lock
/// is set as retail's radar lock button sets it.
/// </summary>
/// <remarks>Every address was checked against the installed acclient.exe by <see cref="RetailHudCatalogue"/>; a feature that did not check
/// reads as empty. Build it once <see cref="NativeUi.Ready"/>, and call it on the game thread only.</remarks>
internal sealed unsafe class NativeRadarPort : IRadarPort
{
    /// <summary>How far from the player objects are read: the Dereth radar's widest zoom, in metres.</summary>
    internal const float Range = 150f;
    // A bucket table over a million entries is not CObjectMaint's (retail's holds a few thousand objects): the layout moved, so read nothing.
    private const uint MaxHashMask = 0xFFFFF;
    private static readonly KeyValuePair<RadarColor, (float R, float G, float B)>[] Palette = RadarColors.Retail.ToArray();

    private readonly RetailHudNatives _natives;
    private readonly Func<uint, string?> _name;
    private readonly Action<uint> _select;
    // ponytail: names are kept for the session, one short string per object seen, empty ones too (no WorldFilter call per frame); a name
    // Decal learns after the object is first seen is not picked up, and nothing is pruned. Retry empty names now and then if 05 shows either.
    private readonly Dictionary<uint, string> _names = new();

    /// <param name="natives">Which HUD features the running client matched.</param>
    /// <param name="name">An object's name from Decal's WorldFilter, or null when Decal has none yet.</param>
    /// <param name="select">Decal's SelectItem.</param>
    public NativeRadarPort(RetailHudNatives natives, Func<uint, string?> name, Action<uint> select)
    {
        _natives = natives;
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _select = select ?? throw new ArgumentNullException(nameof(select));
    }

    public RadarPlayer? Player
    {
        get
        {
            var player = PlayerObject();
            if (player == null) return null;
            return new RadarPlayer(PositionOf(player), Heading(Float(player, RetailHudCatalogue.ObjectForwardX), Float(player, RetailHudCatalogue.ObjectForwardX + 4)));
        }
    }

    public IReadOnlyList<RadarObject> Objects
    {
        get
        {
            var objects = new List<RadarObject>();
            if (!_natives.Objects) return objects;
            var player = PlayerObject();
            var maint = Global(RetailHudCatalogue.ObjectMaint);
            if (player == null || maint == null) return objects;
            var buckets = (byte**)Pointer(maint, RetailHudCatalogue.MaintBuckets);
            var mask = *(uint*)(maint + RetailHudCatalogue.MaintMask);
            if (buckets == null || mask > MaxHashMask) return objects;
            var playerId = *(uint*)(Global(RetailHudCatalogue.SmartBox) + RetailHudCatalogue.SmartBoxPlayerId);
            var here = PositionOf(player);
            for (var bucket = 0u; bucket <= mask; bucket++)
                for (var node = buckets[bucket]; node != null; node = Pointer(node, RetailHudCatalogue.ObjectNext))
                {
                    var id = *(uint*)(node + RetailHudCatalogue.ObjectId);
                    // Retail's radar skips the player and objects in no cell (in a pack, or not placed yet).
                    if (id == playerId || Pointer(node, RetailHudCatalogue.ObjectInCell) == null || *(uint*)(node + RetailHudCatalogue.ObjectCell) == 0) continue;
                    // Out of range costs two reads: the weenie, name and colour are only looked up for what the radar can show.
                    var position = PositionOf(node);
                    var (east, north) = here.OffsetTo(position);
                    if (east * east + north * north > Range * Range) continue;
                    var weenie = ((delegate* unmanaged[Cdecl]<uint, byte*>)RetailHudCatalogue.GetWeenieObject)(id);
                    if (weenie == null || Pointer(weenie, RetailHudCatalogue.WeeniePhysics) == null) continue;
                    if ((*(uint*)(weenie + RetailHudCatalogue.WeenieFlags) & RetailHudCatalogue.UiHidden) != 0) continue;
                    objects.Add(new RadarObject(id, Name(id), Colour(weenie), (RadarBehavior)(*(int*)(weenie + RetailHudCatalogue.WeenieBehavior)), position));
                }
            return objects;
        }
    }

    public void Select(uint id) => _select(id);

    public bool UiLockAvailable => _natives.Lock;

    public bool UiLocked
    {
        get => _natives.Lock && NativeUi.IsUiLocked;
        set
        {
            if (!_natives.Lock || value == UiLocked) return;
            // As retail's radar lock button: PlayerModule::SetLockUI on its CPlayerSystem's PlayerModule, then the lock-changed message every
            // window updates on, through UIElementManager. The Lock list checks the button's calls, receivers and the manager global.
            var playerSystem = ((delegate* unmanaged[Cdecl]<byte*>)RetailHudCatalogue.GetPlayerSystem)();
            var manager = Global(NativeUi.Manager);
            if (playerSystem == null || manager == null) return;
            ((delegate* unmanaged[Thiscall]<byte*, byte, void>)RetailHudCatalogue.SetLockUi)(playerSystem + RetailToolbarCatalogue.PlayerModuleOffset, value ? (byte)1 : (byte)0);
            ((delegate* unmanaged[Thiscall]<byte*, uint, int, void>)RetailHudCatalogue.BroadcastGlobalMessage)(manager, RetailHudCatalogue.UiLockChangedMessage, 0);
        }
    }

    /// <summary>Retail's compass heading of a forward axis (Frame::get_heading): 0 north, 90 east.</summary>
    internal static float Heading(float forwardX, float forwardY) =>
        (float)((450 - Math.Atan2(forwardY, forwardX) * 57.29577951308232) % 360);

    private byte* PlayerObject()
    {
        if (!_natives.Player) return null;
        var smartBox = Global(RetailHudCatalogue.SmartBox);
        var player = smartBox == null ? null : Pointer(smartBox, RetailHudCatalogue.SmartBoxPlayer);
        return player == null || *(uint*)(player + RetailHudCatalogue.ObjectCell) == 0 ? null : player;
    }

    private string Name(uint id)
    {
        if (!_names.TryGetValue(id, out var name)) _names[id] = name = _name(id) ?? string.Empty;
        return name;
    }

    // The colour retail draws the object in: its own GetBlipColor (fellowship, player killer, portal, vendor and creature rules), matched
    // back to retail's table. Anything else is white, as retail's default.
    private static RadarColor Colour(byte* weenie)
    {
        var rgba = stackalloc float[4];
        ((delegate* unmanaged[Cdecl]<float*, byte*, float*>)RetailHudCatalogue.GetBlipColor)(rgba, weenie);
        foreach (var colour in Palette)
            if (colour.Value == (rgba[0], rgba[1], rgba[2])) return colour.Key;
        return RadarColor.White;
    }

    private static RadarPosition PositionOf(byte* physics) => new(*(uint*)(physics + RetailHudCatalogue.ObjectCell),
        Float(physics, RetailHudCatalogue.ObjectOrigin), Float(physics, RetailHudCatalogue.ObjectOrigin + 4), Float(physics, RetailHudCatalogue.ObjectOrigin + 8));

    private static float Float(byte* address, int offset) => *(float*)(address + offset);

    private static byte* Pointer(byte* address, int offset) => *(byte**)(address + offset);
    private static byte* Global(uint address) => *(byte**)(nuint)address;
}

using Decal.Adapter.Wrappers;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.DecalPlugin;

// ponytail: the net48 copy, kept so this plugin still runs until step 02 turns it into the stub. The client's own copy is in
// LegACEy.Client; step 02 deletes this file (the Decal reads move into the stub's bridge calls).

/// <summary>Decal's icon values as the portal ids and visual the client draws. Shared by the item icon and the inventory.</summary>
internal static class DecalIcons
{
    /// <summary>Decal reports portal texture ids without their 0x06 prefix; zero means none.</summary>
    public static uint Texture(int value) => value == 0 ? 0 : (value & 0xFF000000) == 0 ? unchecked((uint)value) | 0x06000000 : unchecked((uint)value);

    // Gate: Decal's Category is the item type (ACE's ItemType), which picks the plate.
    public static ItemVisual Visual(WorldObject item) => new(Texture(item.Icon), Texture(item.Values(LongValueKey.IconUnderlay, 0)),
        Texture(item.Values(LongValueKey.IconOverlay, 0)), unchecked((uint)item.Values(LongValueKey.IconOutline, 0)),
        ItemIcon.PlateFor(unchecked((uint)item.Values(LongValueKey.Category, 0))));
}

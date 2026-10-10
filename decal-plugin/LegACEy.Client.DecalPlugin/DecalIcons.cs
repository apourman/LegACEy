using Decal.Adapter.Wrappers;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Decal's icon values as the portal ids and visual the client draws. Shared by the item icon and the inventory.</summary>
internal static class DecalIcons
{
    /// <summary>Decal reports portal texture ids without their 0x06 prefix; zero means none.</summary>
    public static uint Texture(int value) => value == 0 ? 0 : (value & 0xFF000000) == 0 ? unchecked((uint)value) | 0x06000000 : unchecked((uint)value);

    public static ItemVisual Visual(WorldObject item) => new(Texture(item.Icon), Texture(item.Values(LongValueKey.IconUnderlay, 0)),
        Texture(item.Values(LongValueKey.IconOverlay, 0)), unchecked((uint)item.Values(LongValueKey.IconOutline, 0)));
}

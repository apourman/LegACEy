using System.Collections.Generic;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.Tests;

/// <summary>An inventory the window tests draw: the main pack, two side packs and an empty one, worn armour, a coat and a dagger.</summary>
internal static class InventorySample
{
    public const uint Character = 0x50000001;
    public const uint Sack = 0x50000010;
    public const uint Potions = 0x50000011;

    public const uint Helm = 0x80000001;
    public const uint Coat = 0x80000002;
    public const uint Dagger = 0x80000003;
    public const uint WieldedSword = 0x80000004;
    public const uint Apple = 0x80000005;
    public const uint BluePotion = 0x80000006;
    public const uint Scroll = 0x80000007;
    public const uint YellowPotion = 0x80000008;
    public const uint Sword = 0x80000009;

    private const uint BackpackIcon = 0x0600127E;
    private const uint SackIcon = 0x0600101E;

    /// <summary>The whole sample: main pack open or a side pack open, with one item selected.</summary>
    public static InventorySnapshot Snapshot(uint openContainer = Character, uint selected = 0, int burden = 4212)
    {
        var main = new InventoryPack(Character, "Main Pack", BackpackIcon, 96);
        var sides = new List<InventoryPack>
        {
            new(Sack, "Sack", SackIcon, 24),
            new(Potions, "Potions", SackIcon, 24),
        };
        for (var empty = 0; empty < 5; empty++) sides.Add(new InventoryPack(0, string.Empty, 0, 0));

        var items = new List<InventoryItem>
        {
            Item(Apple, "Apple", Character, 0, 0x06001049, stack: 20),
            Item(Scroll, "Scroll", Character, 4, 0x06001065),
            Item(Sword, "Sword", Character, 7, 0x060010DA),
        };
        // Seventeen potions in the potions pack, so its grid reads "17 / 24".
        for (var slot = 0; slot < 17; slot++)
        {
            if (slot == 0) items.Add(Item(BluePotion, "Blue potion", Potions, slot, 0x06001012, stack: 12));
            else if (slot == 5) items.Add(Item(YellowPotion, "Yellow potion", Potions, slot, 0x06001013, stack: 3));
            else items.Add(Item(0x80000100u + (uint)slot, "Vial", Potions, slot, 0x06001013));
        }

        var worn = new List<WieldedItem>
        {
            Wielded(Helm, "Helm", 0x06000FCA, 0x00000001, PaperdollSlot.Head),
            // A coat covers the chest and both arm slots.
            Wielded(Coat, "Chainmail coat", 0x06000FC7, 0x00000200 | 0x00000800 | 0x00001000,
                PaperdollSlot.Chest, PaperdollSlot.UpperArms, PaperdollSlot.LowerArms),
            // A dual-wielded off-hand weapon: its equip mask is the shield bit, so it shows in the shield slot.
            Wielded(Dagger, "Dagger", 0x060010B5, 0x00200000, PaperdollSlot.Shield),
            Wielded(WieldedSword, "Sword", 0x060010DA, 0x00100000, PaperdollSlot.Weapon),
        };

        return new InventorySnapshot(main, sides, items, worn, burden, 5400, 25000, openContainer, selected);
    }

    private static InventoryItem Item(uint id, string name, uint container, int slot, uint icon, int stack = 1) =>
        new(id, name, container, slot, new ItemVisual(icon, 0, 0, 0), stack, 0, 0, 0);

    private static WieldedItem Wielded(uint id, string name, uint icon, uint validLocations, params PaperdollSlot[] slots) =>
        new(id, name, new ItemVisual(icon, 0, 0, 0), 1, 1, 0, validLocations, slots);

    /// <summary>A picture-less client: every icon is missing, so each slot draws its placeholder.</summary>
    public sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}

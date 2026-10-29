using System.Reflection;
using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

/// <summary>
/// Everything the client gets from Decal crosses these bridge tables, so this list is the whole Decal surface. Decal is on its way out:
/// read state and send actions through the client's own functions (NativeAppraisalSender, NativeWieldSender) rather than adding to it.
/// When Decal really is the only way, add the name here and say why in its doc comment.
/// </summary>
public sealed class DecalSurfaceTests
{
    [Fact]
    public void The_decal_surface_does_not_grow_without_a_reason()
    {
        Assert.Equal(new[]
        {
            "BridgeCalls: Device Fact Text Inventory Item UseItem SelectItem RequestId DropItem MoveItem MergeItem AutoWield ValidLocations "
                + "Coverage PluralName DescriptionFlags Chat ChatClick Stat",
            "BridgeEvents: Frame WindowMessage ServerMessage LoginComplete Logoff ItemSelected ContainerOpened InventoryChanged Shutdown "
                + "CommandLine LogoffAuthorized",
            "BridgeItem: Id Container Slot EquippedMask ValidLocations StackCount StackMax ItemSlots IsContainer Wcid Icon Underlay Overlay "
                + "UiEffects Plate Name IsCreature HasMana Material Structure MaxStructure",
            "BridgeFact: CharacterId Health MaxHealth Stamina MaxStamina Mana MaxMana Burden Strength CarryingAugmentations MainPackCapacity "
                + "SidePackCapacity OpenedContainer CurrentSelection CombatMode CharacterMembership Vitae",
            "BridgeText: CharacterName ServerName PluginDirectory Race Gender"
        }, new[]
        {
            Fields(typeof(BridgeCalls)), Fields(typeof(BridgeEvents)), Fields(typeof(BridgeItem)),
            Names(typeof(BridgeFact)), Names(typeof(BridgeText))
        });
    }

    private static string Fields(Type type) =>
        $"{type.Name}: {string.Join(' ', type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(field => field.Name))}";

    private static string Names(Type type) => $"{type.Name}: {string.Join(' ', Enum.GetNames(type))}";
}

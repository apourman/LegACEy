using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

/// <summary>
/// Everything the client gets from Decal crosses these bridge tables, and the Decal plugin answers them from the Decal members listed
/// below, so these lists are the whole Decal surface. Decal is on its way out: read state and send actions through the client's own
/// functions (NativeAppraisalSender, NativeWieldSender, NativeVitals) rather than adding to it. When Decal really is the only way, add the
/// name here and say why in a comment where it is used.
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
                + "SidePackCapacity OpenedContainer CurrentSelection CombatMode CharacterMembership",
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

    [Fact]
    public void The_decal_plugin_reads_no_new_decal_members()
    {
        Assert.Equal(new[]
        {
            "CharFilterAttributeType.Strength",
            "CharFilterVitalType.Health", "CharFilterVitalType.Mana", "CharFilterVitalType.Stamina",
            "CharacterFilter", "CharacterFilter.Allegiance", "CharacterFilter.Attributes", "CharacterFilter.BurdenUnits",
            "CharacterFilter.EffectiveAttribute", "CharacterFilter.EffectiveVital", "CharacterFilter.Followers", "CharacterFilter.Gender",
            "CharacterFilter.GetCharProperty", "CharacterFilter.Health", "CharacterFilter.Id", "CharacterFilter.LoginComplete",
            "CharacterFilter.Logoff", "CharacterFilter.Mana", "CharacterFilter.Monarch", "CharacterFilter.Name", "CharacterFilter.Patron",
            "CharacterFilter.Race", "CharacterFilter.Server", "CharacterFilter.Skills", "CharacterFilter.Stamina",
            "CharacterFilter.Vitals",
            "Core.Actions.AddChatText", "Core.Actions.AutoWield", "Core.Actions.CombatMode", "Core.Actions.CurrentSelection",
            "Core.Actions.DropItem", "Core.Actions.MoveItem", "Core.Actions.OpenedContainer", "Core.Actions.SelectItem",
            "Core.Actions.UseItem", "Core.Actions.Vital", "Core.CommandLineText", "Core.ContainerOpened",
        "Core.Decal.Underlying.GetD3DDevice", "Core.EchoFilter.ServerDispatch", "Core.FilterInitComplete",
            "Core.IDQueue.ShortcircuitID", "Core.ItemSelected", "Core.RenderFrame", "Core.WindowMessage",
            "LongValueKey.Behavior", "LongValueKey.Category", "LongValueKey.Coverage", "LongValueKey.EquipableSlots",
            "LongValueKey.EquippedSlots", "LongValueKey.IconOutline", "LongValueKey.IconOverlay", "LongValueKey.IconUnderlay",
            "LongValueKey.ItemSlots", "LongValueKey.Material", "LongValueKey.MaximumMana", "LongValueKey.PackSlots", "LongValueKey.Slot",
            "LongValueKey.StackCount", "LongValueKey.StackMax", "LongValueKey.Type", "LongValueKey.UsesRemaining",
            "LongValueKey.UsesTotal",
            "ObjectClass.Container", "ObjectClass.Monster", "ObjectClass.Npc", "ObjectClass.Player",
            "StringValueKey.SecondaryName",
            "VitalType.MaximumHealth", "VitalType.MaximumMana", "VitalType.MaximumStamina",
            "WorldFilter", "WorldFilter.ChangeObject", "WorldFilter.CreateObject", "WorldFilter.GetByContainer",
            "WorldFilter.GetInventory", "WorldFilter.MoveObject", "WorldFilter.ReleaseObject",
            "WorldObject.Container", "WorldObject.Icon", "WorldObject.Id", "WorldObject.Name", "WorldObject.ObjectClass",
            "WorldObject.Values",
        }, DecalMembers());
    }

    // Decal's objects as the plugin names them: CoreManager.Current (or core), its CharacterFilter (character) and WorldFilter (world),
    // a WorldObject (item), and the value keys and enums an item or the character is read by.
    private static readonly Regex DecalMember = new(
        @"\b(?:(?<root>CoreManager\.Current|core|character|world|item)(?<chain>(?:\.[A-Z]\w*)+)|(?<key>(?:Long|String|Double|Bool)ValueKey|CharFilter\w+Type|VitalType|ObjectClass)\.(?<name>\w+))");

    private static string[] DecalMembers([CallerFilePath] string here = "")
    {
        var plugin = Path.Combine(Path.GetDirectoryName(here)!, "..", "LegACEy.Client.DecalPlugin");
        return Directory.GetFiles(plugin, "*.cs").SelectMany(file => DecalMember.Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["key"].Success ? $"{match.Groups["key"].Value}.{match.Groups["name"].Value}"
                : match.Groups["root"].Value switch
                {
                    "character" => "CharacterFilter",
                    "world" => "WorldFilter",
                    "item" => "WorldObject",
                    _ => "Core"
                } + match.Groups["chain"].Value)
            .Select(name => name.Replace("Core.CharacterFilter", "CharacterFilter").Replace("Core.WorldFilter", "WorldFilter"))
            .Distinct().Order(StringComparer.Ordinal).ToArray();
    }
}

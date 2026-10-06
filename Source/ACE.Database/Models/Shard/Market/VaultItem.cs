using System;

namespace ACE.Database.Models.Shard.Market;

/// <summary>
/// An item held in escrow. The item keeps its biota row and GUID, with no container, wielder or location; this row is the only record of who owns it.
/// The search columns are copied from the item at deposit.
/// </summary>
public class VaultItem
{
    /// <summary>
    /// biota.id of the escrowed item. MySQL refuses to delete or renumber that biota row while this row exists.
    /// </summary>
    public uint ItemGuid { get; set; }

    public uint AccountId { get; set; }

    /// <summary>
    /// The character that deposited the item
    /// </summary>
    public uint CharacterId { get; set; }

    /// <summary>
    /// One of <see cref="VaultItemState"/>
    /// </summary>
    public string State { get; set; }

    public DateTime DepositedTime { get; set; }

    /// <summary>
    /// Optimistic concurrency token: increment it on every change
    /// </summary>
    public uint RowVersion { get; set; }

    /// <summary>
    /// Where the player put the item in the Vault; null sorts after the arranged items, oldest deposit first. Not a concurrency change.
    /// </summary>
    public int? Position { get; set; }

    public uint Wcid { get; set; }

    public string Name { get; set; }

    public int ItemType { get; set; }

    public int StackSize { get; set; }

    public int Value { get; set; }

    public uint? IconUnderlay { get; set; }

    public uint? Icon { get; set; }

    public uint? IconOverlay { get; set; }

    public uint? IconOverlaySecondary { get; set; }

    public int? UiEffects { get; set; }

    public int? PaletteTemplate { get; set; }

    public uint? ClothingBase { get; set; }

    public int? Workmanship { get; set; }

    public int? ArcaneLore { get; set; }

    public int? WieldRequirements { get; set; }

    public int? WieldSkillType { get; set; }

    public int? WieldDifficulty { get; set; }

    public int? ArmorLevel { get; set; }

    public int? Damage { get; set; }

    public double? DamageMod { get; set; }

    public int? MaterialType { get; set; }

    public int? EquipmentSetId { get; set; }

    public int? ImbuedEffect { get; set; }
}

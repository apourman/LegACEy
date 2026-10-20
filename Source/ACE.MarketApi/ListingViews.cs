using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;

namespace ACE.MarketApi
{
    /// <summary>
    /// Catalog rows (the shared ListingCatalog) shaped for the API's display
    /// </summary>
    public static class ListingViews
    {
        /// <summary>
        /// A listing ready to display: a browse row with its one-line summary, and the listing page's basic facts (name, type, material, listed time, price, seller, wield)
        /// </summary>
        public static ListingView View(ListingCatalog.Row row, AppraisalItem item, GameData gameData) => new ListingView
        {
            Id = row.Listing.Id,
            ItemGuid = (uint)row.Listing.ItemGuid,
            Wcid = row.Item.Wcid,
            Name = row.Item.Name,
            ItemType = ListingCatalog.ItemTypeName(row.Item.ItemType),
            Material = AppraisalRules.MaterialName(item, row.Item.MaterialType),
            Workmanship = row.Item.Workmanship,
            Level = row.Item.WieldRequirements == (int)WieldRequirement.Level ? row.Item.WieldDifficulty : null,
            ArcaneLore = row.Item.ArcaneLore,
            Summary = AppraisalRules.Summary(item),
            Quantity = row.Item.StackSize,
            Price = row.Listing.Price,
            Seller = row.Seller,
            ListedTime = DateTime.SpecifyKind(row.Listing.CreatedTime, DateTimeKind.Utc),
            Wield = WieldText(row.Item),
            Icon = ItemIcons.For(row.Item, gameData),
        };

        /// <summary>
        /// The listing page: the browse row's facts plus the appraisal lines and the spells
        /// </summary>
        public static ListingView Detail(ListingCatalog.Row row, AppraisalItem item, AppraisalRules rules, GameData gameData)
        {
            var view = View(row, item, gameData);

            view.Lines = rules.Format(item);
            view.Spells = AppraisalRules.Spells(item);

            return view;
        }

        /// <summary>
        /// Serialized in camelCase. Lines and spells are on the listing page only.
        /// </summary>
        public sealed class ListingView
        {
            public long Id { get; set; }
            public uint ItemGuid { get; set; }
            public uint Wcid { get; set; }
            public string Name { get; set; }
            public string ItemType { get; set; }
            public string Material { get; set; }
            public int? Workmanship { get; set; }
            public int? Level { get; set; }
            public int? ArcaneLore { get; set; }
            public string Summary { get; set; }
            public int Quantity { get; set; }
            public long Price { get; set; }
            public string Seller { get; set; }
            public DateTime ListedTime { get; set; }
            public string Wield { get; set; }
            public ItemIcons.IconView Icon { get; set; }

            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public IReadOnlyList<string> Lines { get; set; }

            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public IReadOnlyList<AppraisalSpell> Spells { get; set; }
        }

        /// <summary>
        /// "Level 150" or "Missile Weapons 390"; null when the item has no level or skill requirement
        /// </summary>
        public static string WieldText(VaultItem item)
        {
            if (item.WieldDifficulty is not int difficulty)
                return null;

            switch ((WieldRequirement)(item.WieldRequirements ?? 0))
            {
                case WieldRequirement.Level:
                    return $"Level {difficulty}";

                case WieldRequirement.Skill:
                case WieldRequirement.RawSkill:
                    if (item.WieldSkillType is not int skill)
                        return null;
                    return AppraisalRules.SkillText(skill, difficulty);

                default:
                    return null;
            }
        }
    }
}

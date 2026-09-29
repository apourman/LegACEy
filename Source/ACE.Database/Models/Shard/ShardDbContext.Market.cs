using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Models.Shard;

/// <summary>
/// Market tables, mapped through the generated context's partial hook so that regenerating ShardDbContext.cs keeps them.
/// The schema comes from Database/Updates/Shard/2026-09-28-00-Market-Schema.sql.
/// When re-scaffolding the shard database, leave the market_* tables out: they're mapped here.
/// </summary>
public partial class ShardDbContext
{
    public virtual DbSet<VaultItem> MarketVaultItems { get; set; }

    public virtual DbSet<Listing> MarketListings { get; set; }

    public virtual DbSet<AccountBalance> MarketBalances { get; set; }

    public virtual DbSet<Transfer> MarketTransfers { get; set; }

    public virtual DbSet<LedgerEntry> MarketLedgerEntries { get; set; }

    public virtual DbSet<ItemEvent> MarketItemEvents { get; set; }

    public virtual DbSet<Ticket> MarketTickets { get; set; }

    public virtual DbSet<Request> MarketRequests { get; set; }

    public virtual DbSet<LinkCode> MarketLinkCodes { get; set; }

    public virtual DbSet<PluginToken> MarketPluginTokens { get; set; }

    public virtual DbSet<BlockedWcid> MarketBlockedWcids { get; set; }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // The vault item's foreign key to biota exists only in MySQL (ON DELETE RESTRICT), so Biota's generated mapping is left untouched.

        modelBuilder.Entity<VaultItem>(entity =>
        {
            entity.HasKey(e => e.ItemGuid).HasName("PRIMARY");

            entity.ToTable("market_vault_item");

            entity.Property(e => e.ItemGuid).HasColumnName("item_Guid").ValueGeneratedNever();
            entity.Property(e => e.AccountId).HasColumnName("account_Id");
            entity.Property(e => e.CharacterId).HasColumnName("character_Id");
            entity.Property(e => e.State).IsRequired().HasMaxLength(16).HasColumnName("state");
            entity.Property(e => e.DepositedTime).HasColumnType("datetime(6)").HasColumnName("deposited_Time");
            entity.Property(e => e.RowVersion).HasColumnName("row_Version").IsConcurrencyToken();
            entity.Property(e => e.Wcid).HasColumnName("wcid");
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255).HasColumnName("name");
            entity.Property(e => e.ItemType).HasColumnName("item_Type");
            entity.Property(e => e.StackSize).HasColumnName("stack_Size");
            entity.Property(e => e.Value).HasColumnName("value");
            entity.Property(e => e.IconUnderlay).HasColumnName("icon_Underlay");
            entity.Property(e => e.Icon).HasColumnName("icon");
            entity.Property(e => e.IconOverlay).HasColumnName("icon_Overlay");
            entity.Property(e => e.IconOverlaySecondary).HasColumnName("icon_Overlay_Secondary");
            entity.Property(e => e.UiEffects).HasColumnName("ui_Effects");
            entity.Property(e => e.PaletteTemplate).HasColumnName("palette_Template");
            entity.Property(e => e.ClothingBase).HasColumnName("clothing_Base");
            entity.Property(e => e.Workmanship).HasColumnName("workmanship");
            entity.Property(e => e.ArcaneLore).HasColumnName("arcane_Lore");
            entity.Property(e => e.WieldRequirements).HasColumnName("wield_Requirements");
            entity.Property(e => e.WieldSkillType).HasColumnName("wield_Skill_Type");
            entity.Property(e => e.WieldDifficulty).HasColumnName("wield_Difficulty");
            entity.Property(e => e.ArmorLevel).HasColumnName("armor_Level");
            entity.Property(e => e.Damage).HasColumnName("damage");
            entity.Property(e => e.DamageMod).HasColumnName("damage_Mod");
            entity.Property(e => e.MaterialType).HasColumnName("material_Type");
            entity.Property(e => e.EquipmentSetId).HasColumnName("equipment_Set_Id");
            entity.Property(e => e.ImbuedEffect).HasColumnName("imbued_Effect");
        });

        modelBuilder.Entity<Listing>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("market_listing");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ItemGuid).HasColumnName("item_Guid");
            entity.Property(e => e.SellerAccountId).HasColumnName("seller_Account_Id");
            entity.Property(e => e.SellerCharacterId).HasColumnName("seller_Character_Id");
            entity.Property(e => e.Price).HasColumnName("price");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(16).HasColumnName("status");
            entity.Property(e => e.BuyerAccountId).HasColumnName("buyer_Account_Id");
            entity.Property(e => e.BuyerCharacterId).HasColumnName("buyer_Character_Id");
            entity.Property(e => e.CreatedTime).HasColumnType("datetime(6)").HasColumnName("created_Time");
            entity.Property(e => e.ClosedTime).HasColumnType("datetime(6)").HasColumnName("closed_Time");
            entity.Property(e => e.RowVersion).HasColumnName("row_Version").IsConcurrencyToken();
        });

        modelBuilder.Entity<AccountBalance>(entity =>
        {
            entity.HasKey(e => e.AccountId).HasName("PRIMARY");

            entity.ToTable("market_balance");

            entity.Property(e => e.AccountId).HasColumnName("account_Id").ValueGeneratedNever();
            entity.Property(e => e.Balance).HasColumnName("balance");
            entity.Property(e => e.LastSequence).HasColumnName("last_Sequence");
            entity.Property(e => e.RowVersion).HasColumnName("row_Version").IsConcurrencyToken();
        });

        modelBuilder.Entity<Transfer>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("market_transfer");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(16).HasColumnName("kind");
            entity.Property(e => e.ActorAccountId).HasColumnName("actor_Account_Id");
            entity.Property(e => e.ActorCharacterId).HasColumnName("actor_Character_Id");
            entity.Property(e => e.ListingId).HasColumnName("listing_Id");
            entity.Property(e => e.TicketId).HasColumnName("ticket_Id");
            entity.Property(e => e.RequestKey).HasMaxLength(64).UseCollation("utf8mb4_bin").HasColumnName("request_Key");
            entity.Property(e => e.ReversesTransferId).HasColumnName("reverses_Transfer_Id");
            entity.Property(e => e.Memo).HasMaxLength(512).HasColumnName("memo");
            entity.Property(e => e.CreatedTime).HasColumnType("datetime(6)").HasColumnName("created_Time");
        });

        modelBuilder.Entity<LedgerEntry>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("market_ledger_entry");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TransferId).HasColumnName("transfer_Id");
            entity.Property(e => e.AccountId).HasColumnName("account_Id");
            entity.Property(e => e.SystemAccount).HasMaxLength(8).HasColumnName("system_Account");
            entity.Property(e => e.Amount).HasColumnName("amount");
            entity.Property(e => e.Sequence).HasColumnName("sequence");
            entity.Property(e => e.BalanceAfter).HasColumnName("balance_After");
            entity.Property(e => e.Memo).HasMaxLength(255).HasColumnName("memo");

            entity.HasOne(d => d.Transfer).WithMany(p => p.Entries)
                .HasForeignKey(d => d.TransferId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("market_ledger_entry_transfer");
        });

        modelBuilder.Entity<ItemEvent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("market_item_event");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ItemGuid).HasColumnName("item_Guid");
            entity.Property(e => e.AccountId).HasColumnName("account_Id");
            entity.Property(e => e.CharacterId).HasColumnName("character_Id");
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(16).HasColumnName("kind");
            entity.Property(e => e.ListingId).HasColumnName("listing_Id");
            entity.Property(e => e.TransferId).HasColumnName("transfer_Id");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.EventTime).HasColumnType("datetime(6)").HasColumnName("event_Time");
        });

        modelBuilder.Entity<Ticket>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("market_ticket");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(32).HasColumnName("kind");
            entity.Property(e => e.AccountId).HasColumnName("account_Id");
            entity.Property(e => e.CharacterId).HasColumnName("character_Id");
            entity.Property(e => e.Payload).HasColumnType("json").HasColumnName("payload");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(8).HasColumnName("status");
            entity.Property(e => e.ResultCode).HasMaxLength(32).HasColumnName("result_Code");
            entity.Property(e => e.ResultMessage).HasMaxLength(512).HasColumnName("result_Message");
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(64).UseCollation("utf8mb4_bin").HasColumnName("idempotency_Key");
            entity.Property(e => e.CreatedTime).HasColumnType("datetime(6)").HasColumnName("created_Time");
            entity.Property(e => e.ClaimedTime).HasColumnType("datetime(6)").HasColumnName("claimed_Time");
            entity.Property(e => e.FinishedTime).HasColumnType("datetime(6)").HasColumnName("finished_Time");
        });

        modelBuilder.Entity<Request>(entity =>
        {
            entity.HasKey(e => new { e.AccountId, e.IdempotencyKey }).HasName("PRIMARY");

            entity.ToTable("market_request");

            entity.Property(e => e.AccountId).HasColumnName("account_Id");
            entity.Property(e => e.IdempotencyKey).HasMaxLength(64).UseCollation("utf8mb4_bin").HasColumnName("idempotency_Key");
            entity.Property(e => e.Kind).IsRequired().HasMaxLength(32).HasColumnName("kind");
            entity.Property(e => e.Result).HasColumnType("json").HasColumnName("result");
            entity.Property(e => e.CreatedTime).HasColumnType("datetime(6)").HasColumnName("created_Time");
        });

        modelBuilder.Entity<LinkCode>(entity =>
        {
            entity.HasKey(e => e.CodeHash).HasName("PRIMARY");

            entity.ToTable("market_link_code");

            entity.Property(e => e.CodeHash).HasMaxLength(64).HasColumnName("code_Hash");
            entity.Property(e => e.AccountId).HasColumnName("account_Id");
            entity.Property(e => e.CharacterId).HasColumnName("character_Id");
            entity.Property(e => e.ExpiresTime).HasColumnType("datetime(6)").HasColumnName("expires_Time");
            entity.Property(e => e.UsedTime).HasColumnType("datetime(6)").HasColumnName("used_Time");
        });

        modelBuilder.Entity<PluginToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PRIMARY");

            entity.ToTable("market_plugin_token");

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(64).HasColumnName("token_Hash");
            entity.Property(e => e.AccountId).HasColumnName("account_Id");
            entity.Property(e => e.Label).HasMaxLength(64).HasColumnName("label");
            entity.Property(e => e.CreatedTime).HasColumnType("datetime(6)").HasColumnName("created_Time");
            entity.Property(e => e.LastUsedTime).HasColumnType("datetime(6)").HasColumnName("last_Used_Time");
            entity.Property(e => e.ExpiresTime).HasColumnType("datetime(6)").HasColumnName("expires_Time");
            entity.Property(e => e.RevokedTime).HasColumnType("datetime(6)").HasColumnName("revoked_Time");
            entity.Property(e => e.PasswordFingerprint).IsRequired().HasMaxLength(64).HasColumnName("password_Fingerprint");
        });

        modelBuilder.Entity<BlockedWcid>(entity =>
        {
            entity.HasKey(e => e.Wcid).HasName("PRIMARY");

            entity.ToTable("market_blocked_wcid");

            entity.Property(e => e.Wcid).HasColumnName("wcid").ValueGeneratedNever();
            entity.Property(e => e.Reason).IsRequired().HasMaxLength(255).HasColumnName("reason");
            entity.Property(e => e.AddedByAccountId).HasColumnName("added_By_Account_Id");
            entity.Property(e => e.AddedTime).HasColumnType("datetime(6)").HasColumnName("added_Time");
        });
    }
}

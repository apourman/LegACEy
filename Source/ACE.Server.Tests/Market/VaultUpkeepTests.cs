using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command.Handlers;
using ACE.Server.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Seam 2: admin upkeep in game. /market block and unblock change what the Vault accepts, and /market refresh re-copies the Vault search columns
    /// from the stored items after their rows were changed behind the game's back.
    /// </summary>
    public partial class VaultTests
    {
        private const uint AtlatlWcid = 20034; // atlatlispariangoodnostone: a missile weapon with a damage modifier and a skill requirement, not attuned

        [TestMethod]
        public void MarketBlock_RefusesNewDepositsOfTheWcid_AndUnblockAllowsThemAgain()
        {
            var admin = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var first = VaultTestWorld.Give(player, VaultTestWorld.NewItem(AtlatlWcid));
            var second = VaultTestWorld.Give(player, VaultTestWorld.NewItem(AtlatlWcid));

            try
            {
                VaultTestWorld.TakeSent(admin);
                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "block", AtlatlWcid.ToString(CultureInfo.InvariantCulture), "dupe", "exploit", "#12"));
                WaitForChat(admin, $"Blocked {AtlatlWcid}");

                var row = MarketTestDatabase.Rows(Db, $"SELECT CONCAT(reason, '|', added_By_Account_Id, '|', TIMESTAMPDIFF(SECOND, added_Time, UTC_TIMESTAMP(6)) < 60) FROM market_blocked_wcid WHERE wcid = {AtlatlWcid};").Single();
                Assert.AreEqual($"dupe exploit #12|{admin.Session.AccountId}|1", row, "the block records the reason, the admin and the time");

                VaultTestWorld.TakeSent(admin);
                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "blocked"));
                var listed = WaitForChat(admin, $"{AtlatlWcid}");
                Assert.IsTrue(listed.Any(c => c.Contains("dupe exploit #12", StringComparison.Ordinal)), "/market blocked shows the reason: " + string.Join("\n", listed));

                var refused = VaultTestWorld.Deposit(player, first.Guid.Full);
                Assert.AreEqual(VaultOutcome.BlockedWcid, refused.Outcome, refused.Message);
                Assert.IsNotNull(player.GetInventoryItem(first.Guid), "a refused item stays in the pack");

                VaultTestWorld.TakeSent(admin);
                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "unblock", AtlatlWcid.ToString(CultureInfo.InvariantCulture)));
                WaitForChat(admin, $"Unblocked {AtlatlWcid}");
                Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_blocked_wcid WHERE wcid = {AtlatlWcid};"));

                var deposited = VaultTestWorld.Deposit(player, second.Guid.Full);
                Assert.AreEqual(VaultOutcome.Deposited, deposited.Outcome, deposited.Message);
            }
            finally
            {
                MarketTestDatabase.Execute(Db, $"DELETE FROM market_blocked_wcid WHERE wcid = {AtlatlWcid};");
            }
        }

        [TestMethod]
        public void MarketBlock_RefusesUnknownWcidsMissingReasonsAndTheConsole_AndASecondBlockKeepsTheFirst()
        {
            var admin = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var other = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var wcid = AtlatlWcid.ToString(CultureInfo.InvariantCulture);

            try
            {
                VaultTestWorld.TakeSent(admin);
                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "block", "4000000000", "no such weenie"));
                WaitForChat(admin, "No weenie 4000000000");

                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "block", wcid));
                WaitForChat(admin, "Usage: /market block");

                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(null, "block", wcid, "from", "the", "console"));
                Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_blocked_wcid WHERE wcid = {AtlatlWcid};"), "a block from the console has no admin to record");

                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "block", wcid, "first"));
                WaitForChat(admin, $"Blocked {AtlatlWcid}");

                VaultTestWorld.TakeSent(other);
                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(other.Session, "block", wcid, "second"));
                WaitForChat(other, "already blocked");
                Assert.AreEqual($"first|{admin.Session.AccountId}", MarketTestDatabase.Rows(Db, $"SELECT CONCAT(reason, '|', added_By_Account_Id) FROM market_blocked_wcid WHERE wcid = {AtlatlWcid};").Single());

                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "unblock", wcid));
                WaitForChat(admin, $"Unblocked {AtlatlWcid}");

                VaultTestWorld.TakeSent(admin);
                VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, "unblock", wcid));
                WaitForChat(admin, "is not blocked");
            }
            finally
            {
                MarketTestDatabase.Execute(Db, $"DELETE FROM market_blocked_wcid WHERE wcid = {AtlatlWcid};");
            }
        }

        [TestMethod]
        public void MarketRefresh_AfterTheItemIsChangedInTheDatabase_UpdatesItsSearchColumns_AndNothingElse()
        {
            var admin = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var (_, guid) = DepositedItem();
            var (_, untouched) = DepositedItem();

            // a listed row is refreshed too, and keeps its state and row version
            MarketTestDatabase.Execute(Db, $"UPDATE market_vault_item SET state = '{VaultItemState.Listed}', row_Version = row_Version + 1 WHERE item_Guid = {guid};");
            var before = RowOutsideTheSearchColumns(guid);
            var untouchedBefore = SearchColumns(untouched);

            // a shard SQL update fixing the item, without telling the game
            MarketTestDatabase.Execute(Db,
                $"UPDATE biota_properties_string SET value = 'Fixed Sword' WHERE object_Id = {guid} AND type = {(int)PropertyString.Name};" +
                $"REPLACE INTO biota_properties_int (object_Id, type, value) VALUES ({guid}, {(int)PropertyInt.ItemWorkmanship}, 9), ({guid}, {(int)PropertyInt.Damage}, 77);" +
                $"REPLACE INTO biota_properties_float (object_Id, type, value) VALUES ({guid}, {(int)PropertyFloat.DamageMod}, 1.5);");

            Refresh(admin, VaultTestWorld.SwordWcid);

            var row = MarketTestDatabase.Rows(Db, $"SELECT CONCAT(name, '|', workmanship, '|', damage, '|', damage_Mod) FROM market_vault_item WHERE item_Guid = {guid};").Single();
            Assert.AreEqual("Fixed Sword|9|77|1.5", row);
            Assert.AreEqual(before, RowOutsideTheSearchColumns(guid), "state, owner, deposit time and row version are unchanged");
            Assert.AreEqual(untouchedBefore, SearchColumns(untouched), "an unchanged item's columns stay the same");
        }

        [TestMethod]
        public void MarketRefresh_ForOneWcid_LeavesOtherRowsAlone()
        {
            var admin = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var (_, sword) = DepositedItem();
            var helm = NewVaultRow(VaultTestWorld.HelmWcid);

            MarketTestDatabase.Execute(Db, $"UPDATE market_vault_item SET name = 'stale' WHERE item_Guid IN ({sword}, {helm});");

            Refresh(admin, VaultTestWorld.HelmWcid);

            Assert.AreEqual("stale", MarketTestDatabase.Rows(Db, $"SELECT name FROM market_vault_item WHERE item_Guid = {sword};").Single(), "another WCID's row is left alone");
            Assert.AreNotEqual("stale", MarketTestDatabase.Rows(Db, $"SELECT name FROM market_vault_item WHERE item_Guid = {helm};").Single());
        }

        /// <summary>
        /// The columns are compared with the item's own property rows, read by their numeric types, so a wrong accessor in NewVaultItem shows up here
        /// </summary>
        [TestMethod]
        public void MarketRefresh_CopiesEveryColumnFromTheItemsProperties_OnVariedItems()
        {
            var admin = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());

            var items = new Dictionary<string, uint>
            {
                ["melee"] = NewVaultRow(VaultTestWorld.SwordWcid),
                ["missile"] = NewVaultRow(AtlatlWcid),
                ["armor with a set, palette and clothing base"] = NewVaultRow(33574), // ace33574-relicalduressacoat
                ["caster with arcane lore and an imbue"] = NewVaultRow(36229), // ace36229-riftorb
                ["attuned jewelry with a set"] = NewVaultRow(34704), // ace34704-blueempyreanring
                ["stack"] = NewVaultRow(33459, stackSize: 250), // ace33459-shadowbolt
            };

            // loot-style properties no weenie above carries, written straight to the item rows
            var melee = items["melee"];
            MarketTestDatabase.Execute(Db,
                $"REPLACE INTO biota_properties_int (object_Id, type, value) VALUES ({melee}, {(int)PropertyInt.ItemWorkmanship}, 6), ({melee}, {(int)PropertyInt.MaterialType}, 61), " +
                $"({melee}, {(int)PropertyInt.ImbuedEffect}, 512), ({melee}, {(int)PropertyInt.UiEffects}, 256), ({melee}, {(int)PropertyInt.ItemDifficulty}, 140), ({melee}, {(int)PropertyInt.Value}, 4321);" +
                $"REPLACE INTO biota_properties_d_i_d (object_Id, type, value) VALUES ({melee}, {(int)PropertyDataId.IconUnderlay}, 100667493), ({melee}, {(int)PropertyDataId.IconOverlay}, 100673767), ({melee}, {(int)PropertyDataId.IconOverlaySecondary}, 100673768);");

            Refresh(admin, null);

            foreach (var (kind, guid) in items)
            {
                var expected = string.Join("|",
                    Str(guid, PropertyString.Name),
                    Int(guid, PropertyInt.ItemType) ?? "0",
                    Int(guid, PropertyInt.StackSize) ?? "1",
                    Int(guid, PropertyInt.Value) ?? "0",
                    Did(guid, PropertyDataId.IconUnderlay), Did(guid, PropertyDataId.Icon), Did(guid, PropertyDataId.IconOverlay), Did(guid, PropertyDataId.IconOverlaySecondary),
                    Int(guid, PropertyInt.UiEffects), Int(guid, PropertyInt.PaletteTemplate), Did(guid, PropertyDataId.ClothingBase),
                    Int(guid, PropertyInt.ItemWorkmanship), Int(guid, PropertyInt.ItemDifficulty),
                    Int(guid, PropertyInt.WieldRequirements) ?? "0", Int(guid, PropertyInt.WieldSkillType), Int(guid, PropertyInt.WieldDifficulty),
                    Int(guid, PropertyInt.ArmorLevel), Int(guid, PropertyInt.Damage), Float(guid, PropertyFloat.DamageMod),
                    Int(guid, PropertyInt.MaterialType), Int(guid, PropertyInt.EquipmentSetId), Int(guid, PropertyInt.ImbuedEffect));

                Assert.AreEqual(expected, SearchColumns(guid), kind);
            }

            // the fixtures really are varied: each column is set on at least one of them
            var columns = items.Values.Select(g => SearchColumns(g).Split('|')).ToList();
            for (var i = 0; i < columns[0].Length; i++)
                Assert.IsTrue(columns.Any(c => c[i] != "-" && c[i] != "0"), $"column {i} is exercised by some item");

            Assert.AreEqual("250", Int(items["stack"], PropertyInt.StackSize));
        }

        [TestMethod]
        public void Startup_DeletesOldRequestsDeadLinkCodesAndLongDeadTokens_KeepsLiveOnes()
        {
            var account = VaultTestWorld.NewAccountId();

            MarketTestDatabase.Execute(Db,
                "INSERT INTO market_request (account_Id, idempotency_Key, kind, result, created_Time) VALUES " +
                $"({account}, 'old', 'purchase', '{{}}', UTC_TIMESTAMP(6) - INTERVAL 31 DAY), ({account}, 'recent', 'purchase', '{{}}', UTC_TIMESTAMP(6) - INTERVAL 29 DAY);" +
                "INSERT INTO market_link_code (code_Hash, account_Id, character_Id, expires_Time, used_Time) VALUES " +
                $"(UNHEX(MD5('{account}-used')), {account}, 1, UTC_TIMESTAMP(6) + INTERVAL 4 MINUTE, UTC_TIMESTAMP(6)), " +
                $"(UNHEX(MD5('{account}-expired')), {account}, 1, UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE, NULL), " +
                $"(UNHEX(MD5('{account}-live')), {account}, 1, UTC_TIMESTAMP(6) + INTERVAL 4 MINUTE, NULL);" +
                "INSERT INTO market_plugin_token (token_Hash, account_Id, label, created_Time, expires_Time, revoked_Time, password_Fingerprint) VALUES " +
                $"(UNHEX(MD5('{account}-dead')), {account}, 'dead', UTC_TIMESTAMP(6) - INTERVAL 200 DAY, UTC_TIMESTAMP(6) - INTERVAL 31 DAY, NULL, X'00'), " +
                $"(UNHEX(MD5('{account}-revoked')), {account}, 'revoked', UTC_TIMESTAMP(6) - INTERVAL 10 DAY, UTC_TIMESTAMP(6) + INTERVAL 80 DAY, UTC_TIMESTAMP(6) - INTERVAL 1 DAY, X'00'), " +
                $"(UNHEX(MD5('{account}-live')), {account}, 'live', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6) + INTERVAL 90 DAY, NULL, X'00');");

            Vault.Initialize();

            Assert.AreEqual("recent", MarketTestDatabase.Rows(Db, $"SELECT idempotency_Key FROM market_request WHERE account_Id = {account};").Single());
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_link_code WHERE account_Id = {account};"), "only the live link code is left");
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_link_code WHERE code_Hash = UNHEX(MD5('{account}-live'));"));
            CollectionAssert.AreEquivalent(new[] { "revoked", "live" }, MarketTestDatabase.Rows(Db, $"SELECT label FROM market_plugin_token WHERE account_Id = {account};"));
        }

        // ---- helpers

        private static void Refresh(Player admin, uint? wcid)
        {
            VaultTestWorld.TakeSent(admin);

            var parameters = wcid == null ? new[] { "refresh" } : new[] { "refresh", wcid.Value.ToString(CultureInfo.InvariantCulture) };
            VaultTestWorld.OnWorldThread(() => MarketCommands.HandleMarket(admin.Session, parameters));

            var told = WaitForChat(admin, "Refreshed");
            Assert.IsFalse(told.Any(c => c.Contains("could not", StringComparison.Ordinal)), string.Join("\n", told));
        }

        /// <summary>
        /// A saved item with a Vault row whose search columns are all stale, written straight to the database (deposit rules don't apply)
        /// </summary>
        private static uint NewVaultRow(uint wcid, int? stackSize = null)
        {
            var item = VaultTestWorld.NewItem(wcid);

            if (stackSize != null)
                item.SetStackSize(stackSize);

            VaultTestWorld.Save(item);

            var guid = item.Guid.Full;
            MarketTestDatabase.Execute(Db,
                "INSERT INTO market_vault_item (item_Guid, account_Id, character_Id, state, deposited_Time, wcid, name, item_Type) " +
                $"VALUES ({guid}, {VaultTestWorld.NewAccountId()}, 1, '{VaultItemState.Held}', UTC_TIMESTAMP(6), {wcid}, 'stale', 0);");

            return guid;
        }

        private static string SearchColumns(uint guid) => MarketTestDatabase.Rows(Db,
            "SELECT CONCAT_WS('|', name, item_Type, stack_Size, value, " +
            "IFNULL(icon_Underlay, '-'), IFNULL(icon, '-'), IFNULL(icon_Overlay, '-'), IFNULL(icon_Overlay_Secondary, '-'), " +
            "IFNULL(ui_Effects, '-'), IFNULL(palette_Template, '-'), IFNULL(clothing_Base, '-'), IFNULL(workmanship, '-'), IFNULL(arcane_Lore, '-'), " +
            "IFNULL(wield_Requirements, '-'), IFNULL(wield_Skill_Type, '-'), IFNULL(wield_Difficulty, '-'), IFNULL(armor_Level, '-'), IFNULL(damage, '-'), " +
            "IFNULL(damage_Mod, '-'), IFNULL(material_Type, '-'), IFNULL(equipment_Set_Id, '-'), IFNULL(imbued_Effect, '-')) " +
            $"FROM market_vault_item WHERE item_Guid = {guid};").Single();

        private static string RowOutsideTheSearchColumns(uint guid) => MarketTestDatabase.Rows(Db,
            $"SELECT CONCAT_WS('|', account_Id, character_Id, state, deposited_Time, row_Version, wcid) FROM market_vault_item WHERE item_Guid = {guid};").Single();

        private static string Property(string table, uint guid, int type) =>
            MarketTestDatabase.Rows(Db, $"SELECT value FROM {table} WHERE object_Id = {guid} AND type = {type};").SingleOrDefault();

        /// <summary>
        /// The property's value; when the item lacks it, "-" for a nullable column, or null for a column that has a default instead (the caller supplies it)
        /// </summary>
        private static string Int(uint guid, PropertyInt type) => Property("biota_properties_int", guid, (int)type) ?? (ColumnHasDefault(type) ? null : "-");

        private static bool ColumnHasDefault(PropertyInt type) => type == PropertyInt.ItemType || type == PropertyInt.StackSize || type == PropertyInt.Value || type == PropertyInt.WieldRequirements;

        private static string Float(uint guid, PropertyFloat type) => Property("biota_properties_float", guid, (int)type) ?? "-";

        private static string Did(uint guid, PropertyDataId type) => Property("biota_properties_d_i_d", guid, (int)type) ?? "-";

        private static string Str(uint guid, PropertyString type) => Property("biota_properties_string", guid, (int)type);
    }
}

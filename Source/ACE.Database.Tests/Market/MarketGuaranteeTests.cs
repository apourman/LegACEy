using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using MySqlConnector;

using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Seam 3: the escrow and money guarantees MySQL itself enforces, market settings, and the market entities in the shard context.
    /// Runs against a scratch shard built by the fresh-install path (base script plus every update).
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class MarketGuaranteeTests
    {
        private const string Db = "ace_shard_market_test";

        // MySQL error numbers
        private const int RowIsReferenced = 1451;       // ER_ROW_IS_REFERENCED_2: foreign key refuses a parent delete/update
        private const int SignalException = 1644;       // ER_SIGNAL_EXCEPTION: SIGNAL SQLSTATE '45000' from a trigger
        private const int CheckConstraintViolated = 3819;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            MarketTestDatabase.InitializeConfig();

            var failures = MarketTestDatabase.CreateFresh(Db);
            if (failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex))
                throw ex;
        }

        [ClassCleanup]
        public static void TestCleanup()
        {
            MarketTestDatabase.Drop(Db);
        }

        [TestMethod]
        public void DeleteBiota_WithVaultRow_FailsWithForeignKeyError()
        {
            const uint guid = 0x80000101;

            MarketTestDatabase.Execute(Db, $"INSERT INTO biota (id, weenie_Class_Id, weenie_Type) VALUES ({guid}, 20630, 51);");
            MarketTestDatabase.Execute(Db, $"INSERT INTO biota_properties_int (object_Id, type, value) VALUES ({guid}, 12, 5);");
            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketVaultItems.Add(NewVaultItem(guid, accountId: 1));
                context.SaveChanges();
            }

            // hand SQL, the way the orphan purge and admin fixes delete
            var ex = MarketTestDatabase.ExpectMySqlError(Db, $"DELETE FROM biota WHERE id = {guid};");
            Assert.AreEqual(RowIsReferenced, ex.Number, ex.Message);

            // renumbering the GUID (the GUID consolidator) is refused too
            ex = MarketTestDatabase.ExpectMySqlError(Db, $"UPDATE biota SET id = {guid + 1} WHERE id = {guid};");
            Assert.AreEqual(RowIsReferenced, ex.Number, ex.Message);

            // through Entity Framework, the way ShardDatabase.RemoveBiota deletes
            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                var biota = context.Biota.First(b => b.Id == guid);
                context.Biota.Remove(biota);

                var dbEx = Assert.ThrowsExactly<DbUpdateException>(() => context.SaveChanges());
                Assert.AreEqual(RowIsReferenced, ((MySqlException)dbEx.InnerException).Number, dbEx.InnerException.Message);
            }

            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM biota WHERE id = {guid};"));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM biota_properties_int WHERE object_Id = {guid};"));
            Assert.AreEqual(1, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
        }

        [TestMethod]
        public void DeleteBiota_WithoutVaultRow_Succeeds()
        {
            const uint guid = 0x80000102;

            MarketTestDatabase.Execute(Db, $"INSERT INTO biota (id, weenie_Class_Id, weenie_Type) VALUES ({guid}, 20630, 51);");
            MarketTestDatabase.Execute(Db, $"DELETE FROM biota WHERE id = {guid};");

            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM biota WHERE id = {guid};"));
        }

        [TestMethod]
        public void Transfer_UpdateAndDelete_RejectedByTrigger()
        {
            var transferId = InsertNoteDepositTransfer(accountId: 11, amount: 25);

            var ex = MarketTestDatabase.ExpectMySqlError(Db, $"UPDATE market_transfer SET memo = 'edited' WHERE id = {transferId};");
            Assert.AreEqual(SignalException, ex.Number, ex.Message);
            StringAssert.Contains(ex.Message, "append-only");

            ex = MarketTestDatabase.ExpectMySqlError(Db, $"DELETE FROM market_transfer WHERE id = {transferId};");
            Assert.AreEqual(SignalException, ex.Number, ex.Message);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                var transfer = context.MarketTransfers.First(t => t.Id == transferId);
                transfer.Memo = "edited";
                Assert.ThrowsExactly<DbUpdateException>(() => context.SaveChanges());
            }

            Assert.AreEqual("NULL", MarketTestDatabase.Rows(Db, $"SELECT memo FROM market_transfer WHERE id = {transferId};").Single());
        }

        [TestMethod]
        public void LedgerEntry_UpdateAndDelete_RejectedByTrigger()
        {
            var transferId = InsertNoteDepositTransfer(accountId: 12, amount: 40);

            var ex = MarketTestDatabase.ExpectMySqlError(Db, $"UPDATE market_ledger_entry SET amount = amount * 2 WHERE transfer_Id = {transferId};");
            Assert.AreEqual(SignalException, ex.Number, ex.Message);
            StringAssert.Contains(ex.Message, "append-only");

            ex = MarketTestDatabase.ExpectMySqlError(Db, $"DELETE FROM market_ledger_entry WHERE transfer_Id = {transferId};");
            Assert.AreEqual(SignalException, ex.Number, ex.Message);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                var entry = context.MarketLedgerEntries.First(e => e.TransferId == transferId && e.AccountId != null);
                context.MarketLedgerEntries.Remove(entry);
                Assert.ThrowsExactly<DbUpdateException>(() => context.SaveChanges());
            }

            Assert.AreEqual(2, MarketTestDatabase.Scalar(Db, $"SELECT COUNT(*) FROM market_ledger_entry WHERE transfer_Id = {transferId};"));
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, $"SELECT SUM(amount) FROM market_ledger_entry WHERE transfer_Id = {transferId};"));
        }

        [TestMethod]
        public void Balance_BelowZero_RejectedByCheck()
        {
            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketBalances.Add(new AccountBalance { AccountId = 21, Balance = -1 });

                var dbEx = Assert.ThrowsExactly<DbUpdateException>(() => context.SaveChanges());
                Assert.AreEqual(CheckConstraintViolated, ((MySqlException)dbEx.InnerException).Number, dbEx.InnerException.Message);
            }

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketBalances.Add(new AccountBalance { AccountId = 22, Balance = 0 });
                context.SaveChanges();
            }

            var ex = MarketTestDatabase.ExpectMySqlError(Db, "UPDATE market_balance SET balance = balance - 1 WHERE account_Id = 22;");
            Assert.AreEqual(CheckConstraintViolated, ex.Number, ex.Message);

            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, "SELECT COUNT(*) FROM market_balance WHERE account_Id = 21;"));
            Assert.AreEqual(0, MarketTestDatabase.Scalar(Db, "SELECT balance FROM market_balance WHERE account_Id = 22;"));
        }

        [TestMethod]
        public void MarketSettings_Defaults_MatchSpec()
        {
            Assert.AreEqual(1000, MarketSettings.VaultSize.Default);
            Assert.AreEqual(200, MarketSettings.ActiveListings.Default);
            Assert.AreEqual(14, MarketSettings.ListingLifetimeDays.Default);
            Assert.AreEqual(10, MarketSettings.PurchasesPerMinute.Default);
            Assert.AreEqual(5, MarketSettings.SignInAccountFailures.Default);
            Assert.AreEqual(15, MarketSettings.SignInAccountWindowMinutes.Default);
            Assert.AreEqual(15, MarketSettings.SignInAccountLockMinutes.Default);
            Assert.AreEqual(20, MarketSettings.SignInIpFailures.Default);
            Assert.AreEqual(15, MarketSettings.SignInIpWindowMinutes.Default);
            Assert.AreEqual(5, MarketSettings.LinkCodeMinutes.Default);
            Assert.AreEqual(15, MarketSettings.SignInIpLockMinutes.Default);
            Assert.AreEqual(90, MarketSettings.PluginTokenDays.Default);
            Assert.AreEqual(60, MarketSettings.ChannelSeconds.Default);
            Assert.AreEqual("vault_channel_seconds", MarketSettings.ChannelSeconds.Key);

            Assert.AreEqual(MarketSettings.All.Count, MarketSettings.All.Select(s => s.Key).Distinct().Count(), "duplicate keys");
            foreach (var setting in MarketSettings.All)
                Assert.IsFalse(string.IsNullOrWhiteSpace(setting.Description), setting.Key);
        }

        [TestMethod]
        public void MarketSettings_NoRow_ResolvesToDefault()
        {
            DeleteMarketSettingRows();

            using var context = MarketTestDatabase.CreateContext(Db);

            foreach (var setting in MarketSettings.All)
                Assert.AreEqual(setting.Default, MarketSettings.Get(context, setting), setting.Key);
        }

        [TestMethod]
        public void MarketSettings_RowPresent_ResolvesToRowValue()
        {
            foreach (var setting in MarketSettings.All)
                MarketTestDatabase.Execute(Db, $"REPLACE INTO config_properties_long (`key`, `value`, description) VALUES ('{setting.Key}', {setting.Default + 1234}, 'test');");

            try
            {
                using var context = MarketTestDatabase.CreateContext(Db);

                foreach (var setting in MarketSettings.All)
                    Assert.AreEqual(setting.Default + 1234, MarketSettings.Get(context, setting), setting.Key);
            }
            finally
            {
                DeleteMarketSettingRows();
            }
        }

        [TestMethod]
        public void MarketEntities_SaveAndLoad_RoundTrip()
        {
            const uint guid = 0x80000201;
            var now = new DateTime(2026, 9, 28, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234560);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                // an existing entity through its unchanged mapping, next to the new ones, in one save
                var biota = new Biota { Id = guid, WeenieClassId = 20630, WeenieType = 51 };
                biota.BiotaPropertiesInt.Add(new BiotaPropertiesInt { Type = 12, Value = 7 });
                context.Biota.Add(biota);

                var vaultItem = NewVaultItem(guid, accountId: 31);
                vaultItem.DepositedTime = now;
                context.MarketVaultItems.Add(vaultItem);

                context.MarketListings.Add(new Listing { ItemGuid = guid, SellerAccountId = 31, SellerCharacterId = 0x50000001, Price = 100, Status = ListingStatus.Active, CreatedTime = now });
                context.MarketBalances.Add(new AccountBalance { AccountId = 31, Balance = 100, LastSequence = 1 });

                var transfer = new Transfer { Kind = TransferKind.AdminAdjust, ActorAccountId = 1, Memo = "grant", CreatedTime = now };
                transfer.Entries.Add(new LedgerEntry { AccountId = 31, Amount = 100, Sequence = 1, BalanceAfter = 100 });
                transfer.Entries.Add(new LedgerEntry { SystemAccount = SystemAccount.Admin, Amount = -100 });
                context.MarketTransfers.Add(transfer);

                context.MarketItemEvents.Add(new ItemEvent { ItemGuid = guid, AccountId = 31, CharacterId = 0x50000001, Kind = ItemEventKind.Deposit, EventTime = now });
                context.MarketTickets.Add(new Ticket { Kind = "vault_withdraw", AccountId = 31, CharacterId = 0x50000001, Payload = "{\"itemGuid\": 2147484161}", Status = TicketStatus.Waiting, IdempotencyKey = "k1", CreatedTime = now });
                context.MarketRequests.Add(new Request { AccountId = 31, IdempotencyKey = "k1", Kind = "purchase", Result = "{\"code\": \"ok\"}", CreatedTime = now });
                context.MarketLinkCodes.Add(new LinkCode { CodeHash = new byte[] { 1, 2, 3 }, AccountId = 31, CharacterId = 0x50000001, ExpiresTime = now.AddMinutes(5) });
                context.MarketPluginTokens.Add(new PluginToken { TokenHash = new byte[] { 4, 5, 6 }, AccountId = 31, Label = "laptop", CreatedTime = now, ExpiresTime = now.AddDays(90), PasswordFingerprint = new byte[] { 7, 8 } });
                context.MarketBlockedWcids.Add(new BlockedWcid { Wcid = 12345, Reason = "exploit", AddedByAccountId = 1, AddedTime = now });

                context.SaveChanges();
            }

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                var biota = context.Biota.Include(b => b.BiotaPropertiesInt).First(b => b.Id == guid);
                Assert.AreEqual(7, biota.BiotaPropertiesInt.Single().Value);

                var vaultItem = context.MarketVaultItems.First(v => v.ItemGuid == guid);
                Assert.AreEqual(31u, vaultItem.AccountId);
                Assert.AreEqual(VaultItemState.Held, vaultItem.State);
                Assert.AreEqual(now, DateTime.SpecifyKind(vaultItem.DepositedTime, DateTimeKind.Utc), "datetime(6) keeps microseconds");
                Assert.AreEqual("Trade Note", vaultItem.Name);
                Assert.AreEqual(0x06001234u, vaultItem.Icon);
                Assert.AreEqual(1.65, vaultItem.DamageMod);

                var listing = context.MarketListings.First(l => l.ItemGuid == guid);
                Assert.AreEqual(100, listing.Price);
                Assert.AreEqual(ListingStatus.Active, listing.Status);
                Assert.IsNull(listing.BuyerAccountId);

                Assert.AreEqual(100, context.MarketBalances.First(b => b.AccountId == 31).Balance);

                var transfer = context.MarketTransfers.Include(t => t.Entries).First(t => t.Memo == "grant");
                Assert.AreEqual(2, transfer.Entries.Count);
                Assert.AreEqual(0, transfer.Entries.Sum(e => e.Amount));
                Assert.AreEqual(SystemAccount.Admin, transfer.Entries.Single(e => e.AccountId == null).SystemAccount);

                Assert.AreEqual(ItemEventKind.Deposit, context.MarketItemEvents.First(e => e.ItemGuid == guid).Kind);
                Assert.AreEqual(TicketStatus.Waiting, context.MarketTickets.First(t => t.AccountId == 31).Status);
                StringAssert.Contains(context.MarketRequests.First(r => r.AccountId == 31).Result, "ok");
                Assert.AreEqual(31u, context.MarketLinkCodes.First(c => c.CodeHash == new byte[] { 1, 2, 3 }).AccountId);
                Assert.AreEqual("laptop", context.MarketPluginTokens.First(t => t.AccountId == 31).Label);
                Assert.AreEqual("exploit", context.MarketBlockedWcids.First(w => w.Wcid == 12345).Reason);
            }
        }

        [TestMethod]
        public void MarketEntities_RowVersion_IsConcurrencyToken()
        {
            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketBalances.Add(new AccountBalance { AccountId = 41, Balance = 10 });
                context.SaveChanges();
            }

            using var first = MarketTestDatabase.CreateContext(Db);
            using var second = MarketTestDatabase.CreateContext(Db);

            var firstBalance = first.MarketBalances.First(x => x.AccountId == 41);
            var secondBalance = second.MarketBalances.First(x => x.AccountId == 41);

            firstBalance.Balance -= 5;
            firstBalance.RowVersion++;
            first.SaveChanges();

            secondBalance.Balance -= 5;
            secondBalance.RowVersion++;
            Assert.ThrowsExactly<DbUpdateConcurrencyException>(() => second.SaveChanges());

            Assert.AreEqual(5, MarketTestDatabase.Scalar(Db, "SELECT balance FROM market_balance WHERE account_Id = 41;"));
        }

        [TestMethod]
        public void IdempotencyKeys_DifferingOnlyInCase_AreDistinct()
        {
            var now = DateTime.UtcNow;

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketTickets.Add(new Ticket { Kind = "vault_withdraw", AccountId = 51, Status = TicketStatus.Waiting, IdempotencyKey = "abc", CreatedTime = now });
                context.MarketTickets.Add(new Ticket { Kind = "vault_withdraw", AccountId = 51, Status = TicketStatus.Waiting, IdempotencyKey = "ABC", CreatedTime = now });
                context.MarketRequests.Add(new Request { AccountId = 51, IdempotencyKey = "abc", Kind = "purchase", CreatedTime = now });
                context.MarketRequests.Add(new Request { AccountId = 51, IdempotencyKey = "ABC", Kind = "purchase", CreatedTime = now });
                context.SaveChanges();
            }

            // the same key twice for one account is still refused
            var ex = MarketTestDatabase.ExpectMySqlError(Db, "INSERT INTO market_ticket (kind, account_Id, status, idempotency_Key, created_Time) VALUES ('vault_withdraw', 51, 'WAITING', 'abc', UTC_TIMESTAMP(6));");
            Assert.AreEqual(1062, ex.Number, ex.Message); // ER_DUP_ENTRY

            using (var context = MarketTestDatabase.CreateContext(Db))
                Assert.AreEqual("ABC", context.MarketRequests.Single(r => r.AccountId == 51 && r.IdempotencyKey == "ABC").IdempotencyKey);
        }

        [TestMethod]
        public void ShardModel_ExistingEntities_HaveNoMarketRelationships()
        {
            using var context = MarketTestDatabase.CreateContext(Db);

            var entityTypes = context.Model.GetEntityTypes().ToList();
            var market = entityTypes.Where(t => t.ClrType.Namespace == typeof(VaultItem).Namespace).ToList();
            var existing = entityTypes.Except(market).ToList();

            Assert.AreEqual(11, market.Count, string.Join(", ", market.Select(t => t.ClrType.Name)));
            foreach (var type in market)
                StringAssert.StartsWith(type.GetTableName(), "market_", type.ClrType.Name);

            // the generated entities keep their tables and gain no keys, properties or relationships to market entities
            Assert.AreEqual(40, existing.Count, "the generated context maps 40 entities");
            foreach (var type in existing)
            {
                Assert.IsFalse(type.GetTableName().StartsWith("market"), type.ClrType.Name);
                Assert.AreEqual(typeof(Biota).Namespace, type.ClrType.Namespace, type.ClrType.Name);
                Assert.IsFalse(type.GetForeignKeys().Any(fk => market.Contains(fk.PrincipalEntityType)), type.ClrType.Name);
                Assert.IsFalse(type.GetReferencingForeignKeys().Any(fk => market.Contains(fk.DeclaringEntityType)), type.ClrType.Name);
            }
        }

        private static void DeleteMarketSettingRows()
        {
            MarketTestDatabase.Execute(Db, "DELETE FROM config_properties_long WHERE `key` IN (" + string.Join(",", MarketSettings.All.Select(s => $"'{s.Key}'")) + ");");
        }

        private static VaultItem NewVaultItem(uint guid, uint accountId)
        {
            return new VaultItem
            {
                ItemGuid = guid,
                AccountId = accountId,
                CharacterId = 0x50000001,
                State = VaultItemState.Held,
                DepositedTime = DateTime.UtcNow,
                Wcid = 20630,
                Name = "Trade Note",
                ItemType = 0x2000,
                StackSize = 5,
                Value = 250000,
                Icon = 0x06001234,
                DamageMod = 1.65,
            };
        }

        private static long InsertNoteDepositTransfer(uint accountId, long amount)
        {
            using var context = MarketTestDatabase.CreateContext(Db);

            var transfer = new Transfer { Kind = TransferKind.NoteDeposit, ActorAccountId = accountId, CreatedTime = DateTime.UtcNow };
            transfer.Entries.Add(new LedgerEntry { AccountId = accountId, Amount = amount, Sequence = 1, BalanceAfter = amount });
            transfer.Entries.Add(new LedgerEntry { SystemAccount = SystemAccount.Notes, Amount = -amount });
            context.MarketTransfers.Add(transfer);
            context.SaveChanges();

            return transfer.Id;
        }
    }
}

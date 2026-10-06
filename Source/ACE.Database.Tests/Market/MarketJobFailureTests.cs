using System;
using System.Collections.Generic;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

using EntityBiota = ACE.Entity.Models.Biota;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Seam 3: how the save-queue jobs fail. A job always answers (so the game never strands an item or a busy player), refuses value leaving a banned account,
    /// and never applies a ledger change twice when a save that already committed reports a failure.
    /// ShardDatabase builds its contexts from config, so the configured shard and auth databases point at scratch ones for this class.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class MarketJobFailureTests
    {
        private const string Db = "ace_shard_market_jobs";
        private const string AuthDb = "ace_auth_market_jobs";

        private const uint CharacterId = 0x50000001;

        private static string originalShardDatabase;
        private static string originalAuthDatabase;

        private static uint nextAccountId = 780000;
        private static uint nextGuid = 0x80002000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            MarketTestDatabase.InitializeConfig();

            var failures = MarketTestDatabase.CreateFresh(Db);
            if (failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex))
                throw ex;

            MarketTestDatabase.CreateAuth(AuthDb);

            originalShardDatabase = ConfigManager.Config.MySql.Shard.Database;
            originalAuthDatabase = ConfigManager.Config.MySql.Authentication.Database;
            ConfigManager.Config.MySql.Shard.Database = Db;
            ConfigManager.Config.MySql.Authentication.Database = AuthDb;
        }

        [ClassCleanup]
        public static void TestCleanup()
        {
            ConfigManager.Config.MySql.Shard.Database = originalShardDatabase;
            ConfigManager.Config.MySql.Authentication.Database = originalAuthDatabase;

            MarketTestDatabase.Drop(Db);
            MarketTestDatabase.Drop(AuthDb);
        }

        // ---- a save that committed but reported a failure

        [TestMethod]
        public void DepositNotes_SaveAlreadyCommittedThenReportsALostRace_IsNotCreditedTwice()
        {
            var account = NewAccount();
            var note = nextGuid++;
            long committed = 0;
            IDisposable failTransfers = null;

            // Each try starts by evicting the notes. The first try's commit lands but its acknowledgement is lost, so the retry strategy's second try
            // fails on the rows the first wrote, which looks like a lost race. That is played here as: the first try's rows are written as it starts,
            // and its own insert fails as a duplicate. Later tries could save again.
            var database = new HookedShardDatabase(attempt =>
            {
                if (attempt == 1)
                {
                    committed = PlantNoteDeposit(account, note, 5, DateTime.UtcNow);
                    failTransfers = FailInsertsInto("market_transfer", duplicateKey: true);
                }
                else
                    failTransfers?.Dispose();
            });

            MarketJobResult result;
            try
            {
                result = database.DepositNotes(account, CharacterId, new[] { new NoteStack(note, 5) }, out _);
            }
            finally
            {
                failTransfers?.Dispose();
            }

            Assert.AreEqual(MarketJobResult.Saved, result, "the job sees its own commit instead of rebuilding the deposit");
            Assert.AreEqual(1, Count($"SELECT COUNT(DISTINCT transfer_Id) FROM market_ledger_entry WHERE account_Id = {account};"), "one deposit, not two");
            Assert.AreEqual(committed, Count($"SELECT transfer_Id FROM market_ledger_entry WHERE account_Id = {account};"));
            Assert.AreEqual("5", MarketTestDatabase.Rows(Db, $"SELECT balance FROM market_balance WHERE account_Id = {account};")[0]);
        }

        [TestMethod]
        public void DepositNotes_EverySaveLosesARace_ReportsFailedAndCreditsNothing()
        {
            var account = NewAccount();
            var note = nextGuid++;

            MarketJobResult result;
            using (FailInsertsInto("market_transfer", duplicateKey: true))
                result = new ShardDatabase().DepositNotes(account, CharacterId, new[] { new NoteStack(note, 5) }, out _);

            Assert.AreEqual(MarketJobResult.Failed, result);
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_ledger_entry WHERE account_Id = {account};"));
        }

        // ---- bans

        [TestMethod]
        public void WithdrawNotes_AccountBanned_RefusesAndPaysNothing()
        {
            var account = NewAccount();
            PlantNoteDeposit(account, nextGuid++, 50, DateTime.UtcNow);
            Ban(account);

            var note = NewNote(10);
            var result = new ShardDatabase().WithdrawNotes(account, CharacterId, new[] { (note, new ReaderWriterLockSlim()) }, 10, out var balance);

            Assert.AreEqual(MarketJobResult.Banned, result);
            Assert.AreEqual(50, balance, "the balance is unchanged");
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota WHERE id = {note.Id};"), "no notes were made");
            Assert.AreEqual("50", MarketTestDatabase.Rows(Db, $"SELECT balance FROM market_balance WHERE account_Id = {account};")[0]);
        }

        [TestMethod]
        public void WithdrawFromVault_AccountBanned_RefusesAndTheItemStaysInEscrow()
        {
            var account = NewAccount();
            var guid = SeedVaultItem(account);
            Ban(account);

            var result = new ShardDatabase().WithdrawFromVault(InPack(guid), new ReaderWriterLockSlim(), account, CharacterId, expectedRowVersion: 0);

            Assert.AreEqual(MarketJobResult.Banned, result);
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"));
        }

        [TestMethod]
        public void WithdrawFromVault_ExpiredBan_Withdraws()
        {
            var account = NewAccount();
            var guid = SeedVaultItem(account);
            MarketTestDatabase.Execute(AuthDb, $"UPDATE account SET banned_Time = UTC_TIMESTAMP() - INTERVAL 2 DAY, ban_Expire_Time = UTC_TIMESTAMP() - INTERVAL 1 DAY WHERE accountId = {account};");

            Assert.AreEqual(MarketJobResult.Saved, new ShardDatabase().WithdrawFromVault(InPack(guid), new ReaderWriterLockSlim(), account, CharacterId, expectedRowVersion: 0));
        }

        // ---- a job that fails before its save still answers

        [TestMethod]
        public void WithdrawFromVault_ReadFailsBeforeTheSave_TheQueueStillCallsBackAndNothingMoves()
        {
            var account = NewAccount();
            var guid = SeedVaultItem(account);

            var queue = new SerializedShardDatabase(new ShardDatabase());
            var results = new List<MarketJobResult>();

            // the ban check reads the auth database; with it gone the read throws inside the job
            ConfigManager.Config.MySql.Authentication.Database = "ace_auth_market_jobs_missing";
            queue.Start();
            try
            {
                queue.WithdrawFromVault(InPack(guid), new ReaderWriterLockSlim(), account, CharacterId, 0, results.Add);
                queue.WithdrawNotes(account, CharacterId, new[] { (NewNote(1), new ReaderWriterLockSlim()) }, 1, (r, _) => results.Add(r));
            }
            finally
            {
                queue.Stop(); // drains the queue
                ConfigManager.Config.MySql.Authentication.Database = AuthDb;
            }

            CollectionAssert.AreEqual(new[] { MarketJobResult.Failed, MarketJobResult.Failed }, results, "every job answers, so the game can put the item back and free the player");
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {guid};"));
        }

        // ---- helpers

        private static uint NewAccount()
        {
            var account = Interlocked.Increment(ref nextAccountId);

            MarketTestDatabase.Execute(AuthDb, $"INSERT INTO account (accountId, accountName, passwordHash, passwordSalt, accessLevel) VALUES ({account}, 'jobs{account}', 'x', 'use bcrypt', 0);");

            return account;
        }

        private static void Ban(uint account)
        {
            MarketTestDatabase.Execute(AuthDb, $"UPDATE account SET banned_Time = UTC_TIMESTAMP(), ban_Expire_Time = UTC_TIMESTAMP() + INTERVAL 1 DAY WHERE accountId = {account};");
        }

        /// <summary>
        /// A note_deposit of amount as DepositNotes writes it, with its destroyed-note event. Returns the transfer id.
        /// </summary>
        private static long PlantNoteDeposit(uint account, uint noteGuid, long amount, DateTime time)
        {
            using var context = MarketTestDatabase.CreateContext(Db);

            var transfer = new Transfer { Kind = TransferKind.NoteDeposit, ActorAccountId = account, ActorCharacterId = CharacterId, CreatedTime = DateTime.UtcNow };
            transfer.Entries.Add(Ledger.PlayerEntry(account, amount));
            transfer.Entries.Add(Ledger.SystemEntry(SystemAccount.Notes, -amount));
            Assert.IsTrue(Ledger.TryAdd(context, transfer));

            context.MarketItemEvents.Add(new ItemEvent { ItemGuid = noteGuid, AccountId = account, CharacterId = CharacterId, Kind = ItemEventKind.Deposit, Transfer = transfer, Quantity = (int)amount, EventTime = time });
            context.SaveChanges();

            return transfer.Id;
        }

        /// <summary>
        /// A trade note stack the world thread made for a withdrawal: not saved, pointed at the character
        /// </summary>
        private static EntityBiota NewNote(int stackSize)
        {
            return new EntityBiota
            {
                Id = nextGuid++,
                WeenieClassId = ShardDatabase.TradeNoteWcid,
                WeenieType = WeenieType.Stackable,
                PropertiesIID = new Dictionary<PropertyInstanceId, uint> { [PropertyInstanceId.Container] = CharacterId, [PropertyInstanceId.Owner] = CharacterId },
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.StackSize] = stackSize },
            };
        }

        /// <summary>
        /// An escrowed item: no container, with a held Vault row at row version 0
        /// </summary>
        private static uint SeedVaultItem(uint account)
        {
            var guid = nextGuid++;

            MarketTestDatabase.Execute(Db, $"INSERT INTO biota (id, weenie_Class_Id, weenie_Type, populated_Collection_Flags) VALUES ({guid}, 3000, {(int)WeenieType.Generic}, 0);");
            MarketTestDatabase.Execute(Db, $"INSERT INTO market_vault_item (item_Guid, account_Id, character_Id, state, deposited_Time, row_Version, wcid, name, item_Type) VALUES ({guid}, {account}, {CharacterId}, '{VaultItemState.Held}', UTC_TIMESTAMP(6), 0, 3000, 'Job Test Item', 1);");

            return guid;
        }

        /// <summary>
        /// The escrowed item as the world thread points it back at the character's pack
        /// </summary>
        private static EntityBiota InPack(uint guid)
        {
            return new EntityBiota
            {
                Id = guid,
                WeenieClassId = 3000,
                WeenieType = WeenieType.Generic,
                PropertiesIID = new Dictionary<PropertyInstanceId, uint> { [PropertyInstanceId.Container] = CharacterId },
            };
        }

        /// <summary>
        /// Makes every insert into the table fail inside the save (a test trigger) until disposed; as a duplicate key, the error a lost race gives, if asked
        /// </summary>
        private static IDisposable FailInsertsInto(string table, bool duplicateKey = false)
        {
            var trigger = $"test_fail_{table}";
            var signal = duplicateKey ? "SIGNAL SQLSTATE '23000' SET MYSQL_ERRNO = 1062, MESSAGE_TEXT = 'injected duplicate entry'" : "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'injected failure'";

            MarketTestDatabase.Execute(Db, $"CREATE TRIGGER `{trigger}` BEFORE INSERT ON `{table}` FOR EACH ROW {signal};");

            return new Cleanup(() => MarketTestDatabase.Execute(Db, $"DROP TRIGGER IF EXISTS `{trigger}`;"));
        }

        private sealed class Cleanup : IDisposable
        {
            private readonly Action action;
            public Cleanup(Action action) => this.action = action;
            public void Dispose() => action();
        }

        private static long Count(string sql) => MarketTestDatabase.Scalar(Db, sql);

        /// <summary>
        /// The plain shard database, with a hook where each try of a note deposit starts (it evicts the notes first): called with the try's number
        /// </summary>
        private sealed class HookedShardDatabase : ShardDatabase
        {
            private readonly Action<int> onTry;
            private int tries;

            public HookedShardDatabase(Action<int> onTry) => this.onTry = onTry;

            public override bool EvictBiota(uint id)
            {
                onTry(++tries);
                return false;
            }
        }
    }
}

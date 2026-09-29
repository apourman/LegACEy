using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// The market update script on a fresh install and on an existing shard, its idempotence, and the startup schema check.
    /// Each test builds its own scratch database on the configured MySQL server.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class MarketSchemaInstallTests
    {
        private const string FreshDb = "ace_shard_market_fresh";
        private const string ExistingDb = "ace_shard_market_existing";

        private static readonly string[] ExpectedTables =
        {
            "market_vault_item", "market_listing", "market_balance", "market_transfer", "market_ledger_entry", "market_item_event",
            "market_ticket", "market_request", "market_link_code", "market_plugin_token", "market_blocked_wcid",
        };

        // table|index|unique(0/1)|columns in order
        private static readonly string[] ExpectedIndexes =
        {
            "market_vault_item|PRIMARY|1|item_Guid",
            "market_vault_item|market_vault_item_account_state_idx|0|account_Id,state",
            "market_listing|PRIMARY|1|id",
            "market_listing|market_listing_item_idx|0|item_Guid",
            "market_listing|market_listing_status_created_idx|0|status,created_Time",
            "market_listing|market_listing_status_price_idx|0|status,price",
            "market_listing|market_listing_seller_status_idx|0|seller_Account_Id,status",
            "market_balance|PRIMARY|1|account_Id",
            "market_transfer|PRIMARY|1|id",
            "market_transfer|market_transfer_reverses_uidx|1|reverses_Transfer_Id",
            "market_transfer|market_transfer_listing_idx|0|listing_Id",
            "market_ledger_entry|PRIMARY|1|id",
            "market_ledger_entry|market_ledger_entry_transfer_idx|0|transfer_Id",
            "market_ledger_entry|market_ledger_entry_account_sequence_uidx|1|account_Id,sequence",
            "market_ledger_entry|market_ledger_entry_system_idx|0|system_Account",
            "market_item_event|PRIMARY|1|id",
            "market_item_event|market_item_event_item_idx|0|item_Guid",
            "market_item_event|market_item_event_account_idx|0|account_Id,id",
            "market_item_event|market_item_event_transfer_idx|0|transfer_Id",
            "market_ticket|PRIMARY|1|id",
            "market_ticket|market_ticket_account_key_uidx|1|account_Id,idempotency_Key",
            "market_ticket|market_ticket_status_idx|0|status,id",
            "market_ticket|market_ticket_finished_idx|0|finished_Time",
            "market_request|PRIMARY|1|account_Id,idempotency_Key",
            "market_request|market_request_created_idx|0|created_Time",
            "market_link_code|PRIMARY|1|code_Hash",
            "market_link_code|market_link_code_account_idx|0|account_Id",
            "market_plugin_token|PRIMARY|1|id",
            "market_plugin_token|market_plugin_token_hash_uidx|1|token_Hash",
            "market_plugin_token|market_plugin_token_account_idx|0|account_Id",
            "market_blocked_wcid|PRIMARY|1|wcid",
        };

        // constraint|table|referenced table|delete rule|update rule
        private static readonly string[] ExpectedForeignKeys =
        {
            "market_vault_item_biota|market_vault_item|biota|RESTRICT|RESTRICT",
            "market_transfer_reverses|market_transfer|market_transfer|RESTRICT|RESTRICT",
            "market_ledger_entry_transfer|market_ledger_entry|market_transfer|RESTRICT|RESTRICT",
        };

        private static readonly string[] ExpectedChecks =
        {
            "market_vault_item_state_chk",
            "market_listing_price_chk",
            "market_listing_status_chk",
            "market_balance_nonnegative_chk",
            "market_transfer_kind_chk",
            "market_transfer_memo_chk",
            "market_ledger_entry_owner_chk",
            "market_ledger_entry_system_chk",
            "market_ledger_entry_player_chk",
            "market_ledger_entry_sysfields_chk",
            "market_item_event_kind_chk",
            "market_ticket_status_chk",
        };

        // trigger|table|event|timing
        private static readonly string[] ExpectedTriggers =
        {
            "market_transfer_no_update|market_transfer|UPDATE|BEFORE",
            "market_transfer_no_delete|market_transfer|DELETE|BEFORE",
            "market_ledger_entry_no_update|market_ledger_entry|UPDATE|BEFORE",
            "market_ledger_entry_no_delete|market_ledger_entry|DELETE|BEFORE",
        };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            MarketTestDatabase.InitializeConfig();
        }

        [ClassCleanup]
        public static void TestCleanup()
        {
            MarketTestDatabase.Drop(FreshDb);
            MarketTestDatabase.Drop(ExistingDb);
        }

        [TestMethod]
        public void FreshInstall_BaseAndAllUpdates_HasFullMarketSchema()
        {
            var failures = MarketTestDatabase.CreateFresh(FreshDb);

            Assert.IsTrue(System.IO.File.Exists(MarketTestDatabase.MarketUpdateScriptPath), "market update script is missing");
            Assert.IsFalse(failures.ContainsKey(MarketTestDatabase.MarketUpdateScript), failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex) ? ex.ToString() : null);

            AssertFullMarketSchema(FreshDb);
        }

        [TestMethod]
        public void ExistingDatabase_ApplyMarketUpdate_HasFullMarketSchemaAndKeepsData()
        {
            // an existing server: base plus every earlier update, with live data, before the market script arrives
            MarketTestDatabase.CreateFromBase(ExistingDb);
            MarketTestDatabase.ApplyAllUpdates(ExistingDb, f => f.Name != MarketTestDatabase.MarketUpdateScript);

            MarketTestDatabase.Execute(ExistingDb, "INSERT INTO biota (id, weenie_Class_Id, weenie_Type) VALUES (2147483649, 20630, 51);");
            MarketTestDatabase.Execute(ExistingDb, "INSERT INTO config_properties_long (`key`, `value`) VALUES ('existing_setting', 7);");

            Assert.AreEqual(0, MarketTestDatabase.Scalar(ExistingDb, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'market%';"));

            MarketTestDatabase.ApplyUpdate(ExistingDb, MarketTestDatabase.MarketUpdateScriptPath);

            AssertFullMarketSchema(ExistingDb);
            Assert.AreEqual(1, MarketTestDatabase.Scalar(ExistingDb, "SELECT COUNT(*) FROM biota WHERE id = 2147483649;"));
            Assert.AreEqual(7, MarketTestDatabase.Scalar(ExistingDb, "SELECT `value` FROM config_properties_long WHERE `key` = 'existing_setting';"));
        }

        [TestMethod]
        public void MarketUpdate_AppliedTwice_SucceedsAndChangesNothing()
        {
            MarketTestDatabase.CreateFromBase(ExistingDb);
            MarketTestDatabase.ApplyUpdate(ExistingDb, MarketTestDatabase.MarketUpdateScriptPath);

            // data in market tables must survive a second run
            MarketTestDatabase.Execute(ExistingDb, "INSERT INTO market_balance (account_Id, balance, last_Sequence, row_Version) VALUES (1, 5, 1, 1);");
            MarketTestDatabase.Execute(ExistingDb, "INSERT INTO market_blocked_wcid (wcid, reason, added_By_Account_Id, added_Time) VALUES (1, 'test', 1, UTC_TIMESTAMP(6));");

            var before = SchemaSnapshot(ExistingDb);

            MarketTestDatabase.ApplyUpdate(ExistingDb, MarketTestDatabase.MarketUpdateScriptPath);

            var after = SchemaSnapshot(ExistingDb);

            CollectionAssert.AreEqual(before, after, "second run changed the schema or data:\n" + string.Join("\n", before.Except(after).Concat(after.Except(before))));
        }

        [TestMethod]
        public void SchemaCheck_AllTablesPresent_ReportsOk()
        {
            MarketTestDatabase.CreateFresh(FreshDb);

            using var context = MarketTestDatabase.CreateContext(FreshDb);
            var result = MarketSchema.Check(context);

            Assert.AreEqual(MarketSchemaStatus.Ok, result.Status, result.Report);
            Assert.AreEqual("ok", result.Report);
            Assert.AreEqual(0, result.Missing.Count);
        }

        [TestMethod]
        public void SchemaCheck_NoMarketTables_ReportsMissing()
        {
            MarketTestDatabase.CreateFromBase(ExistingDb);

            using var context = MarketTestDatabase.CreateContext(ExistingDb);
            var result = MarketSchema.Check(context);

            Assert.AreEqual(MarketSchemaStatus.Missing, result.Status);
            Assert.IsTrue(result.Report.StartsWith("missing"), result.Report);
            foreach (var table in ExpectedTables)
                CollectionAssert.Contains(result.Missing.ToList(), table);
        }

        [TestMethod]
        public void SchemaCheck_AnyOneTableMissing_ReportsMissing()
        {
            MarketTestDatabase.CreateFromBase(ExistingDb);
            MarketTestDatabase.ApplyUpdate(ExistingDb, MarketTestDatabase.MarketUpdateScriptPath);

            // drop tables one at a time, children before the tables they reference
            foreach (var table in new[] { "market_ledger_entry", "market_transfer", "market_vault_item", "market_listing", "market_balance", "market_item_event", "market_ticket", "market_request", "market_link_code", "market_plugin_token", "market_blocked_wcid" })
            {
                MarketTestDatabase.Execute(ExistingDb, $"DROP TABLE `{table}`;");

                using var context = MarketTestDatabase.CreateContext(ExistingDb);
                var result = MarketSchema.Check(context);

                Assert.AreEqual(MarketSchemaStatus.Missing, result.Status, $"after dropping {table}");
                CollectionAssert.Contains(result.Missing.ToList(), table);
            }
        }

        [TestMethod]
        public void SchemaCheck_TriggerMissing_ReportsMissing()
        {
            // the runner marks a failed script applied, so a script that created the tables but not the triggers must not pass
            MarketTestDatabase.CreateFromBase(ExistingDb);
            MarketTestDatabase.ApplyUpdate(ExistingDb, MarketTestDatabase.MarketUpdateScriptPath);
            MarketTestDatabase.Execute(ExistingDb, "DROP TRIGGER market_ledger_entry_no_delete;");

            using var context = MarketTestDatabase.CreateContext(ExistingDb);
            var result = MarketSchema.Check(context);

            Assert.AreEqual(MarketSchemaStatus.Missing, result.Status);
            CollectionAssert.Contains(result.Missing.ToList(), "market_ledger_entry_no_delete");
        }

        private static void AssertFullMarketSchema(string database)
        {
            var tables = MarketTestDatabase.Rows(database,
                "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'market%' ORDER BY TABLE_NAME;");
            CollectionAssert.AreEquivalent(ExpectedTables, tables, "tables: " + string.Join(", ", tables));

            var engines = MarketTestDatabase.Rows(database,
                "SELECT CONCAT(TABLE_NAME, '|', ENGINE, '|', TABLE_COLLATION) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'market%';");
            foreach (var row in engines)
                StringAssert.Contains(row, "|InnoDB|utf8mb4", row);

            var indexes = MarketTestDatabase.Rows(database,
                "SELECT CONCAT(TABLE_NAME, '|', INDEX_NAME, '|', IF(NON_UNIQUE = 0, 1, 0), '|', GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX)) " +
                "FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'market%' GROUP BY TABLE_NAME, INDEX_NAME, NON_UNIQUE;");
            foreach (var expected in ExpectedIndexes)
                CollectionAssert.Contains(indexes, expected, "missing index " + expected);

            var foreignKeys = MarketTestDatabase.Rows(database,
                "SELECT CONCAT(CONSTRAINT_NAME, '|', TABLE_NAME, '|', REFERENCED_TABLE_NAME, '|', DELETE_RULE, '|', UPDATE_RULE) " +
                "FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'market%';");
            CollectionAssert.AreEquivalent(ExpectedForeignKeys, foreignKeys, "foreign keys: " + string.Join(", ", foreignKeys));

            var checks = MarketTestDatabase.Rows(database,
                "SELECT tc.CONSTRAINT_NAME FROM information_schema.TABLE_CONSTRAINTS tc " +
                "WHERE tc.CONSTRAINT_SCHEMA = DATABASE() AND tc.CONSTRAINT_TYPE = 'CHECK' AND tc.TABLE_NAME LIKE 'market%' AND tc.ENFORCED = 'YES';");
            CollectionAssert.AreEquivalent(ExpectedChecks, checks, "checks: " + string.Join(", ", checks));

            var triggers = MarketTestDatabase.Rows(database,
                "SELECT CONCAT(TRIGGER_NAME, '|', EVENT_OBJECT_TABLE, '|', EVENT_MANIPULATION, '|', ACTION_TIMING) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE();");
            CollectionAssert.AreEquivalent(ExpectedTriggers, triggers, "triggers: " + string.Join(", ", triggers));

            // spec style: int unsigned for GUIDs and account ids, bigint for MMD and market ids, datetime(6) for times
            var columns = MarketTestDatabase.Rows(database,
                "SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME, '|', COLUMN_TYPE) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'market%';");
            foreach (var column in columns)
            {
                var name = column.Split('|')[0];
                var type = column.Split('|')[1];

                if (name.EndsWith("_Guid") || name.EndsWith("Account_Id") || name.EndsWith(".account_Id") || name.EndsWith("Character_Id") || name.EndsWith(".character_Id"))
                    Assert.AreEqual("int unsigned", type, name);
                if (name.EndsWith("_Time"))
                    Assert.AreEqual("datetime(6)", type, name);
            }
            foreach (var mmd in new[] { "market_listing.price", "market_balance.balance", "market_ledger_entry.amount", "market_ledger_entry.balance_After", "market_listing.id", "market_transfer.id", "market_ledger_entry.id", "market_ticket.id" })
                CollectionAssert.Contains(columns, mmd + "|bigint", mmd);
        }

        private static List<string> SchemaSnapshot(string database)
        {
            var snapshot = new List<string>();

            snapshot.AddRange(MarketTestDatabase.Rows(database,
                "SELECT TABLE_NAME, ENGINE, TABLE_COLLATION, CREATE_TIME, TABLE_COMMENT FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME;"));
            snapshot.AddRange(MarketTestDatabase.Rows(database,
                "SELECT TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, ORDINAL_POSITION;"));
            snapshot.AddRange(MarketTestDatabase.Rows(database,
                "SELECT TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME, NON_UNIQUE FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX;"));
            snapshot.AddRange(MarketTestDatabase.Rows(database,
                "SELECT CONSTRAINT_NAME, TABLE_NAME, CONSTRAINT_TYPE, ENFORCED FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() ORDER BY TABLE_NAME, CONSTRAINT_NAME;"));
            snapshot.AddRange(MarketTestDatabase.Rows(database,
                "SELECT CONSTRAINT_NAME, CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() ORDER BY CONSTRAINT_NAME;"));
            snapshot.AddRange(MarketTestDatabase.Rows(database,
                "SELECT TRIGGER_NAME, EVENT_OBJECT_TABLE, EVENT_MANIPULATION, ACTION_TIMING, ACTION_STATEMENT, CREATED FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE() ORDER BY TRIGGER_NAME;"));

            foreach (var table in ExpectedTables)
                snapshot.Add($"{table} rows: {MarketTestDatabase.Scalar(database, $"SELECT COUNT(*) FROM `{table}`;")}");
            snapshot.Add("balance: " + string.Join(",", MarketTestDatabase.Rows(database, "SELECT * FROM market_balance ORDER BY account_Id;")));

            return snapshot;
        }
    }
}

using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;

namespace ACE.Database.Market
{
    public enum MarketSchemaStatus
    {
        Ok,
        Missing,
    }

    public sealed class MarketSchemaCheckResult
    {
        public MarketSchemaStatus Status { get; }

        /// <summary>
        /// The market tables and triggers that don't exist, empty when Status is Ok
        /// </summary>
        public IReadOnlyList<string> Missing { get; }

        /// <summary>
        /// "ok", or "missing: " and the missing names
        /// </summary>
        public string Report => Status == MarketSchemaStatus.Ok ? "ok" : "missing: " + string.Join(", ", Missing);

        public MarketSchemaCheckResult(IReadOnlyList<string> missing)
        {
            Missing = missing;
            Status = missing.Count == 0 ? MarketSchemaStatus.Ok : MarketSchemaStatus.Missing;
        }
    }

    /// <summary>
    /// The startup check for the market's tables. The update runner marks even a failed script as applied,
    /// so the game server and the Market API run this at startup and refuse to start the market unless it reports Ok.
    /// </summary>
    public static class MarketSchema
    {
        public static readonly IReadOnlyList<string> Tables = new[]
        {
            "market_vault_item",
            "market_listing",
            "market_balance",
            "market_transfer",
            "market_ledger_entry",
            "market_item_event",
            "market_ticket",
            "market_request",
            "market_link_code",
            "market_plugin_token",
            "market_blocked_wcid",
            "market_web_session",
        };

        /// <summary>
        /// The append-only triggers are the last statements of the update script, so a partly failed script shows up here
        /// </summary>
        public static readonly IReadOnlyList<string> Triggers = new[]
        {
            "market_transfer_no_update",
            "market_transfer_no_delete",
            "market_ledger_entry_no_update",
            "market_ledger_entry_no_delete",
        };

        public static readonly IReadOnlyList<string> RequiredColumns = new[]
        {
            "market_ticket.progress",
            "market_ticket.progress_Time",
            "market_ticket.progress_Until",
            "market_ticket.result",
        };

        /// <summary>
        /// Checks the configured shard database
        /// </summary>
        public static MarketSchemaCheckResult Check()
        {
            using (var context = new ShardDbContext())
                return Check(context);
        }

        /// <summary>
        /// Checks the database the given shard context is connected to
        /// </summary>
        public static MarketSchemaCheckResult Check(ShardDbContext context)
        {
            var tables = context.Database
                .SqlQueryRaw<string>("SELECT TABLE_NAME AS `Value` FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'market%'")
                .ToList();

            var triggers = context.Database
                .SqlQueryRaw<string>("SELECT TRIGGER_NAME AS `Value` FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE() AND TRIGGER_NAME LIKE 'market%'")
                .ToList();

            var columns = context.Database
                .SqlQueryRaw<string>("SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME) AS `Value` FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'market_ticket'")
                .ToList();

            var missing = Tables.Where(t => !tables.Contains(t))
                .Concat(Triggers.Where(t => !triggers.Contains(t)))
                .Concat(RequiredColumns.Where(c => !columns.Contains(c)))
                .ToList();

            return new MarketSchemaCheckResult(missing);
        }
    }
}

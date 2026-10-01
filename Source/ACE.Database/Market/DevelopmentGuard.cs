using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using MySqlConnector;

using ACE.Common;

namespace ACE.Database.Market
{
    /// <summary>
    /// A database a development tool would write: the auth or the shard database, and how it's reached
    /// </summary>
    public sealed record DevelopmentTarget(string Role, MySqlConfiguration Connection)
    {
        public const string AuthRole = "auth";
        public const string ShardRole = "shard";

        public string Endpoint => $"{Connection.Host}:{Connection.Port}";
    }

    /// <summary>
    /// What the development guard lets a tool write: exact endpoints ("host:port") and exact database names
    /// </summary>
    public sealed class DevelopmentGuardSettings
    {
        /// <summary>
        /// The local Docker MySQL (scripts/db-bootstrap), never the ace-db on 3306
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultEndpoints = new[] { "127.0.0.1:3310", "localhost:3310", "::1:3310" };

        /// <summary>
        /// The local market stack's own databases (scripts/market)
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultDatabases = new[] { "ace_market_auth", "ace_market_shard" };

        public IReadOnlyList<string> AllowedEndpoints { get; set; } = DefaultEndpoints;

        public IReadOnlyList<string> AllowedDatabases { get; set; } = DefaultDatabases;
    }

    /// <param name="FailedCheck">the check that failed ("shard database name", "auth marker", ...), or null when every check passed</param>
    public sealed record DevelopmentGuardResult(string FailedCheck, string Detail)
    {
        public static readonly DevelopmentGuardResult Pass = new DevelopmentGuardResult(null, "every check passed");

        public bool Passed => FailedCheck == null;
    }

    /// <summary>
    /// The development database guard, shared by the development tools that write the database directly (the seed tool, the ticket fixture).
    /// Before any write it checks, for both the auth and the shard target:
    /// - the endpoint is on an exact allow-list;
    /// - the database name is on an exact allow-list;
    /// - the database has the development marker row.
    /// The first two read only the configuration, so a target that fails them is never connected to. The marker check only reads.
    /// If any check fails, nothing is written anywhere.
    /// This is a safeguard against mistakes, not a guarantee: a tunnel to a marked database would pass. That's why the marker is written only by the local
    /// bootstrap or an interactive step, and never by an update script that runs on a server.
    /// </summary>
    public static class DevelopmentGuard
    {
        public const string MarkerTable = "legacey_dev_marker";

        public const string MarkerPurpose = "development";

        private static readonly string[] Roles = { DevelopmentTarget.AuthRole, DevelopmentTarget.ShardRole };

        /// <summary>
        /// Runs every check. Only reads, and connects only to targets whose endpoint and name are allowed.
        /// </summary>
        public static DevelopmentGuardResult Check(IReadOnlyList<DevelopmentTarget> targets, DevelopmentGuardSettings settings)
        {
            foreach (var role in Roles)
            {
                if (targets.Count(t => t.Role == role) != 1)
                    return new DevelopmentGuardResult($"{role} target", $"expected exactly one {role} database to check, got {targets.Count(t => t.Role == role)}");
            }

            var unknown = targets.FirstOrDefault(t => !Roles.Contains(t.Role));
            if (unknown != null)
                return new DevelopmentGuardResult("target", $"unknown target role '{unknown.Role}'");

            foreach (var target in targets)
            {
                if (!IsAllowedEndpoint(target.Connection, settings.AllowedEndpoints))
                    return new DevelopmentGuardResult($"{target.Role} endpoint", $"{target.Role} endpoint {target.Endpoint} is not allowed (allowed: {string.Join(", ", settings.AllowedEndpoints)})");
            }

            foreach (var target in targets)
            {
                if (!settings.AllowedDatabases.Contains(target.Connection.Database, StringComparer.Ordinal))
                    return new DevelopmentGuardResult($"{target.Role} database name", $"{target.Role} database '{target.Connection.Database}' is not allowed (allowed: {string.Join(", ", settings.AllowedDatabases)})");
            }

            foreach (var target in targets)
            {
                string marker;

                try
                {
                    marker = ReadMarker(target.Connection);
                }
                catch (MySqlException ex)
                {
                    return new DevelopmentGuardResult($"{target.Role} marker", $"{target.Role} database '{target.Connection.Database}' at {target.Endpoint} could not be read: {ex.Message}");
                }

                if (marker != MarkerPurpose)
                    return new DevelopmentGuardResult($"{target.Role} marker", $"{target.Role} database '{target.Connection.Database}' has no development marker (run the interactive mark step if it really is a development database)");
            }

            return DevelopmentGuardResult.Pass;
        }

        /// <summary>
        /// Calls write only if every check passes
        /// </summary>
        public static DevelopmentGuardResult Run(IReadOnlyList<DevelopmentTarget> targets, DevelopmentGuardSettings settings, Action write)
        {
            var result = Check(targets, settings);

            if (result.Passed)
                write();

            return result;
        }

        /// <summary>
        /// Marks the database as a development database. Only the local bootstrap and an interactive step call this; no update script does.
        /// </summary>
        public static void Mark(MySqlConfiguration connection)
        {
            using var db = Open(connection);

            Execute(db, $"CREATE TABLE IF NOT EXISTS `{MarkerTable}` (" +
                "`id` tinyint unsigned NOT NULL, `purpose` varchar(32) NOT NULL, `marked_Time` datetime(6) NOT NULL, PRIMARY KEY (`id`)" +
                ") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='This database is a local development database: development tools may write it. Never create this on a server.';");
            Execute(db, $"INSERT IGNORE INTO `{MarkerTable}` (`id`, `purpose`, `marked_Time`) VALUES (1, '{MarkerPurpose}', UTC_TIMESTAMP(6));");
        }

        private static bool IsAllowedEndpoint(MySqlConfiguration connection, IReadOnlyList<string> allowed)
        {
            var endpoint = $"{connection.Host?.Trim().ToLowerInvariant()}:{connection.Port.ToString(CultureInfo.InvariantCulture)}";

            return allowed.Any(a => string.Equals(a.Trim().ToLowerInvariant(), endpoint, StringComparison.Ordinal));
        }

        /// <summary>
        /// The marker's purpose, or null when the database has no marker. Reads only.
        /// </summary>
        private static string ReadMarker(MySqlConfiguration connection)
        {
            using var db = Open(connection);

            using (var exists = new MySqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @table;", db))
            {
                exists.Parameters.AddWithValue("@table", MarkerTable);

                if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                    return null;
            }

            using var read = new MySqlCommand($"SELECT `purpose` FROM `{MarkerTable}` WHERE `id` = 1;", db);

            return read.ExecuteScalar() as string;
        }

        private static MySqlConnection Open(MySqlConfiguration connection)
        {
            var builder = new MySqlConnectionStringBuilder(connection.ConnectionOptions)
            {
                Server = connection.Host,
                Port = connection.Port,
                UserID = connection.Username,
                Password = connection.Password,
                Database = connection.Database,
            };

            var db = new MySqlConnection(builder.ConnectionString);
            db.Open();

            return db;
        }

        private static void Execute(MySqlConnection db, string sql)
        {
            using var command = new MySqlCommand(sql, db);
            command.ExecuteNonQuery();
        }
    }
}

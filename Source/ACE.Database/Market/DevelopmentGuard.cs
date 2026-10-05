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
    /// The guard's checks, in the order they run
    /// </summary>
    public enum DevelopmentCheck
    {
        None,
        Target,
        Endpoint,
        DatabaseName,
        Marker,
    }

    /// <summary>
    /// What the development guard lets a tool write: exact endpoints ("host:port") and, per role, exact database names
    /// </summary>
    public sealed class DevelopmentGuardSettings
    {
        public const string E2EAuthDatabase = "ace_market_e2e_auth";
        public const string E2EShardDatabase = "ace_market_e2e_shard";

        /// <summary>
        /// The local Docker MySQL (scripts/db-bootstrap), never the ace-db on 3306
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultEndpoints = new[] { "127.0.0.1:3310", "localhost:3310", "::1:3310" };

        public IReadOnlyList<string> AllowedEndpoints { get; set; } = DefaultEndpoints;

        /// <summary>
        /// The local market stack's own auth database (scripts/market)
        /// </summary>
        public IReadOnlyList<string> AllowedAuthDatabases { get; set; } = new[] { "ace_market_auth", E2EAuthDatabase };

        /// <summary>
        /// The local market stack's own shard database (scripts/market)
        /// </summary>
        public IReadOnlyList<string> AllowedShardDatabases { get; set; } = new[] { "ace_market_shard", E2EShardDatabase };

        public IReadOnlyList<string> AllowedDatabases(string role) => role == DevelopmentTarget.AuthRole ? AllowedAuthDatabases : AllowedShardDatabases;
    }

    /// <param name="Check">the check that failed, or None when every check passed</param>
    /// <param name="FailedCheck">the failed check named for people ("shard database name", "auth marker", ...), or null</param>
    public sealed record DevelopmentGuardResult(DevelopmentCheck Check, string FailedCheck, string Detail)
    {
        public static readonly DevelopmentGuardResult Pass = new DevelopmentGuardResult(DevelopmentCheck.None, null, "every check passed");

        public bool Passed => Check == DevelopmentCheck.None;
    }

    /// <summary>
    /// The development database guard, shared by the development tools that write the database directly (the seed tool, the ticket fixture).
    /// Before any write it checks, for both the auth and the shard target:
    /// - the endpoint is on an exact allow-list;
    /// - the database name is on an exact allow-list, and the end-to-end databases are named only as a pair;
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
            var targetShape = CheckTargetShape(targets);
            if (!targetShape.Passed)
                return targetShape;

            var endpoints = CheckEndpoints(targets, settings.AllowedEndpoints);
            if (!endpoints.Passed)
                return endpoints;

            foreach (var target in targets)
            {
                var allowed = settings.AllowedDatabases(target.Role);

                if (!allowed.Contains(target.Connection.Database, StringComparer.Ordinal))
                    return new DevelopmentGuardResult(DevelopmentCheck.DatabaseName, $"{target.Role} database name", $"{target.Role} database '{target.Connection.Database}' is not allowed (allowed: {string.Join(", ", allowed)})");
            }

            // each name is allowed on its own; the end-to-end databases are also only ever used together, so a mixed
            // configuration can't seed end-to-end accounts into the development auth database (or the reverse)
            var endToEndNames = targets.Count(IsEndToEndDatabase);
            if (endToEndNames != 0 && endToEndNames != targets.Count)
                return new DevelopmentGuardResult(DevelopmentCheck.DatabaseName, "database pair", $"{string.Join(" and ", targets.Select(t => $"{t.Role} database '{t.Connection.Database}'"))} are not one pair: {DevelopmentGuardSettings.E2EAuthDatabase} and {DevelopmentGuardSettings.E2EShardDatabase} are only used together");

            foreach (var target in targets)
            {
                string marker;

                try
                {
                    marker = ReadMarker(target.Connection);
                }
                catch (MySqlException ex)
                {
                    return new DevelopmentGuardResult(DevelopmentCheck.Marker, $"{target.Role} marker", $"{target.Role} database '{target.Connection.Database}' at {target.Endpoint} could not be read: {ex.Message}");
                }

                if (marker != MarkerPurpose)
                    return new DevelopmentGuardResult(DevelopmentCheck.Marker, $"{target.Role} marker", $"{target.Role} database '{target.Connection.Database}' has no development marker (run the interactive mark step if it really is a development database)");
            }

            return DevelopmentGuardResult.Pass;
        }

        /// <summary>
        /// Validates the exact local targets that the destructive fresh command is allowed to replace.
        /// This check is configuration-only: it never opens a connection, so fresh can refuse before its first write.
        /// </summary>
        public static DevelopmentGuardResult CheckFreshTargets(IReadOnlyList<DevelopmentTarget> targets)
        {
            var targetShape = CheckTargetShape(targets);
            if (!targetShape.Passed)
                return targetShape;

            var endpoints = CheckEndpoints(targets, DevelopmentGuardSettings.DefaultEndpoints);
            if (!endpoints.Passed)
                return endpoints;

            var other = targets.FirstOrDefault(t => !IsEndToEndDatabase(t));
            return other == null
                ? DevelopmentGuardResult.Pass
                : new DevelopmentGuardResult(DevelopmentCheck.DatabaseName, $"{other.Role} database name", $"{other.Role} database '{other.Connection.Database}' is not the fresh target '{EndToEndDatabase(other.Role)}'");
        }

        /// <summary>
        /// True when the targets are exactly the end-to-end pair, which the seed fills with the per-test-file accounts. Reads only the configuration.
        /// </summary>
        public static bool IsEndToEndPair(IReadOnlyList<DevelopmentTarget> targets) => CheckTargetShape(targets).Passed && targets.All(IsEndToEndDatabase);

        private static string EndToEndDatabase(string role) => role == DevelopmentTarget.AuthRole ? DevelopmentGuardSettings.E2EAuthDatabase : DevelopmentGuardSettings.E2EShardDatabase;

        private static bool IsEndToEndDatabase(DevelopmentTarget target) => string.Equals(target.Connection.Database, EndToEndDatabase(target.Role), StringComparison.Ordinal);

        private static DevelopmentGuardResult CheckEndpoints(IReadOnlyList<DevelopmentTarget> targets, IReadOnlyList<string> allowed)
        {
            var refused = targets.FirstOrDefault(t => !IsAllowedEndpoint(t.Connection, allowed));
            return refused == null
                ? DevelopmentGuardResult.Pass
                : new DevelopmentGuardResult(DevelopmentCheck.Endpoint, $"{refused.Role} endpoint", $"{refused.Role} endpoint {refused.Endpoint} is not allowed (allowed: {string.Join(", ", allowed)})");
        }

        private static DevelopmentGuardResult CheckTargetShape(IReadOnlyList<DevelopmentTarget> targets)
        {
            foreach (var role in Roles)
            {
                if (targets.Count(t => t.Role == role) != 1)
                    return new DevelopmentGuardResult(DevelopmentCheck.Target, $"{role} target", $"expected exactly one {role} database to check, got {targets.Count(t => t.Role == role)}");
            }

            var unknown = targets.FirstOrDefault(t => !Roles.Contains(t.Role));
            return unknown == null
                ? DevelopmentGuardResult.Pass
                : new DevelopmentGuardResult(DevelopmentCheck.Target, "target", $"unknown target role '{unknown.Role}'");
        }

        /// <summary>
        /// Calls the destructive fresh operation only after its fixed database names and local MySQL endpoint pass validation.
        /// </summary>
        public static DevelopmentGuardResult RunFresh(IReadOnlyList<DevelopmentTarget> targets, Action write)
        {
            var result = CheckFreshTargets(targets);

            if (result.Passed)
                write();

            return result;
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
            using var command = new MySqlCommand(MarkerSql.Value, db);

            command.ExecuteNonQuery();
        }

        /// <summary>
        /// scripts/market/dev-marker.sql, embedded: the bootstrap script runs the same file
        /// </summary>
        private static readonly Lazy<string> MarkerSql = new Lazy<string>(() =>
        {
            using var stream = typeof(DevelopmentGuard).Assembly.GetManifestResourceStream("ACE.Database.Market.dev-marker.sql")
                ?? throw new InvalidOperationException("the development marker script isn't embedded");
            using var reader = new System.IO.StreamReader(stream);

            return reader.ReadToEnd();
        });

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
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using MySqlConnector;

using ACE.Common;
using ACE.Database.Models.Shard;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Builds scratch shard databases on the configured MySQL server the same way ACE does:
    /// the base script through the setup path, then update scripts through the update runner's path.
    /// </summary>
    internal static class MarketTestDatabase
    {
        public const string MarketUpdateScript = "2026-09-28-00-Market-Schema.sql";

        public const string TicketProgressUpdateScript = "2026-10-01-00-Market-Ticket-Progress.sql";

        public const string WebSessionsUpdateScript = "2026-10-02-00-Market-Web-Sessions.sql";

        private static bool configInitialized;

        public static void InitializeConfig()
        {
            if (configInitialized)
                return;

            // copy config.js from ACE.Server, cross-platform, whatever the output folder depth (bin/Debug or bin/x64/Debug)
            var testDir = AppContext.BaseDirectory;
            var sourceDir = FindUp(testDir, dir => Directory.Exists(Path.Combine(dir, "ACE.Server")) && Directory.Exists(Path.Combine(dir, "ACE.Database.Tests")));
            var serverDir = Path.Combine(sourceDir, "ACE.Server");
            var configSource = Path.Combine(serverDir, "Config.js");

            if (!File.Exists(configSource))
                configSource = Path.Combine(serverDir, "Config.js.example");

            File.Copy(configSource, Path.Combine(testDir, "Config.js"), true);
            ConfigManager.Initialize(Path.Combine(testDir, "Config.js"));

            configInitialized = true;
        }

        public static string RepositoryRoot => FindUp(AppContext.BaseDirectory, dir => Directory.Exists(Path.Combine(dir, "Database", "Updates", "Shard")));

        public static string BaseScriptPath => Path.Combine(RepositoryRoot, "Database", "Base", "ShardBase.sql");

        public static string UpdatesPath => Path.Combine(RepositoryRoot, "Database", "Updates", "Shard");

        public static IEnumerable<FileInfo> UpdateScripts => new DirectoryInfo(UpdatesPath).GetFiles("*.sql").OrderBy(f => f.Name, StringComparer.Ordinal);

        public static string MarketUpdateScriptPath => Path.Combine(UpdatesPath, MarketUpdateScript);

        public static string TicketProgressUpdateScriptPath => Path.Combine(UpdatesPath, TicketProgressUpdateScript);

        public static string WebSessionsUpdateScriptPath => Path.Combine(UpdatesPath, WebSessionsUpdateScript);

        /// <summary>
        /// Drops the database if present and runs the base shard script into it, as Program_Setup does.
        /// </summary>
        public static void CreateFromBase(string database)
        {
            Drop(database);

            var sql = RenameDatabases(File.ReadAllText(BaseScriptPath), database);

            using var connection = new MySqlConnection(ConnectionString());
            connection.Open();
            using var command = new MySqlCommand(sql, connection);
            command.CommandTimeout = 600;
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// A fresh scratch auth database from the base script, which names ace_auth itself
        /// </summary>
        public static void CreateAuth(string database)
        {
            Drop(database);

            var sql = File.ReadAllText(Path.Combine(RepositoryRoot, "Database", "Base", "AuthenticationBase.sql")).Replace("ace_auth", database);

            using var connection = new MySqlConnection(ConnectionString());
            connection.Open();
            using var command = new MySqlCommand(sql, connection);
            command.ExecuteNonQuery();
        }

        public static void Drop(string database)
        {
            using var connection = new MySqlConnection(ConnectionString());
            connection.Open();
            using var command = new MySqlCommand($"DROP DATABASE IF EXISTS `{database}`;", connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Applies one update script exactly as Program_DbUpdates.PatchDatabase does: one command, the runner's connection string, database names replaced.
        /// </summary>
        public static void ApplyUpdate(string database, string scriptPath)
        {
            var sql = RenameDatabases(File.ReadAllText(scriptPath), database);

            using var connection = new MySqlConnection(RunnerConnectionString(database));
            connection.Open();
            using var command = new MySqlCommand(sql, connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Applies shard updates in filename order. Like the runner, a failing script doesn't stop the rest; failures are returned by file name.
        /// </summary>
        public static Dictionary<string, MySqlException> ApplyAllUpdates(string database, Func<FileInfo, bool> filter = null)
        {
            var failures = new Dictionary<string, MySqlException>();

            foreach (var file in UpdateScripts)
            {
                if (filter != null && !filter(file))
                    continue;

                try
                {
                    ApplyUpdate(database, file.FullName);
                }
                catch (MySqlException ex)
                {
                    failures[file.Name] = ex;
                }
            }

            return failures;
        }

        /// <summary>
        /// A base install plus every shard update, the fresh-install path. Returns the update failures.
        /// </summary>
        public static Dictionary<string, MySqlException> CreateFresh(string database)
        {
            CreateFromBase(database);

            return ApplyAllUpdates(database);
        }

        /// <summary>
        /// The update runner's connection options, including support for idempotent scripts using prepared statements.
        /// </summary>
        public static string RunnerConnectionString(string database) => ServerConnectionString(database, "DefaultCommandTimeout=120;SslMode=None;AllowPublicKeyRetrieval=true;AllowUserVariables=true");

        /// <summary>
        /// The configured shard connection options, on the given database or on none
        /// </summary>
        public static string ConnectionString(string database = null) => ServerConnectionString(database, ConfigManager.Config.MySql.Shard.ConnectionOptions);

        private static string ServerConnectionString(string database, string options)
        {
            var config = ConfigManager.Config.MySql.Shard;
            var databaseOption = database == null ? "" : $"database={database};";

            return $"server={config.Host};port={config.Port};user={config.Username};password={config.Password};{databaseOption}{options}";
        }

        /// <summary>
        /// A shard context on the given database, configured like ShardDbContext.OnConfiguring (retry on failure included).
        /// </summary>
        public static ShardDbContext CreateContext(string database)
        {
            var connectionString = ConnectionString(database);

            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql(connectionString, DatabaseManager.CachedServerVersionAutoDetect(database, connectionString), builder =>
                {
                    builder.EnableRetryOnFailure(10);
                })
                .Options;

            return new ShardDbContext(options);
        }

        public static void Execute(string database, string sql)
        {
            using var connection = new MySqlConnection(ConnectionString(database));
            connection.Open();
            using var command = new MySqlCommand(sql, connection);
            command.ExecuteNonQuery();
        }

        public static long Scalar(string database, string sql)
        {
            using var connection = new MySqlConnection(ConnectionString(database));
            connection.Open();
            using var command = new MySqlCommand(sql, connection);
            return Convert.ToInt64(command.ExecuteScalar());
        }

        /// <summary>
        /// Every row of a query, each flattened to "a|b|c".
        /// </summary>
        public static List<string> Rows(string database, string sql)
        {
            var rows = new List<string>();

            using var connection = new MySqlConnection(ConnectionString(database));
            connection.Open();
            using var command = new MySqlCommand(sql, connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                rows.Add(string.Join("|", values));
            }

            return rows;
        }

        /// <summary>
        /// Runs sql and returns the MySqlException it throws, or fails the test if it succeeds.
        /// </summary>
        public static MySqlException ExpectMySqlError(string database, string sql)
        {
            try
            {
                Execute(database, sql);
            }
            catch (MySqlException ex)
            {
                return ex;
            }

            throw new Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException($"Expected a MySQL error from: {sql}");
        }

        private static string RenameDatabases(string sql, string shardDatabase)
        {
            var config = ConfigManager.Config.MySql;

            sql = sql.Replace("ace_auth", config.Authentication.Database);
            sql = sql.Replace("ace_shard", shardDatabase);
            sql = sql.Replace("ace_world", config.World.Database);

            return sql;
        }

        private static string FindUp(string start, Func<string, bool> match)
        {
            var dir = new DirectoryInfo(start);

            while (dir != null)
            {
                if (match(dir.FullName))
                    return dir.FullName;

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException($"No matching parent directory above {start}");
        }
    }
}

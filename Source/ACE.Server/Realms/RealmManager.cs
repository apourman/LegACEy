using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using log4net;

using ACE.Entity;

namespace ACE.Server.Realms
{
    /// <summary>
    /// The realms loaded from Content/realms at startup. Realm 0, the base world, is always there.
    /// </summary>
    public static class RealmManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static IReadOnlyDictionary<ushort, RealmDefinition> realms = new Dictionary<ushort, RealmDefinition> { [0] = RealmDefinition.BaseWorld };

        public static string ContentFolder => Path.Combine(AppContext.BaseDirectory, "Content", "realms");

        /// <summary>
        /// Loads the realm files. A bad folder or file is logged and the server boots without those realms.
        /// </summary>
        public static void Initialize()
        {
            var loaded = new Dictionary<ushort, RealmDefinition> { [0] = RealmDefinition.BaseWorld };

            try
            {
                var result = RealmLoader.LoadDirectory(ContentFolder);

                foreach (var diagnostic in result.Diagnostics)
                    log.Error($"[REALMS] {diagnostic}");

                foreach (var realm in result.Realms)
                    loaded[realm.Id] = realm;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Error($"[REALMS] could not read {ContentFolder}; no realms loaded ({ex.Message})");
            }

            realms = loaded;
            log.Info($"Loaded {realms.Count - 1} realm(s) from {ContentFolder}");
        }

        public static IEnumerable<RealmDefinition> All => realms.Values.OrderBy(realm => realm.Id);

        public static RealmDefinition Get(ushort id) => realms.TryGetValue(id, out var realm) ? realm : null;

        public static bool Exists(ushort id) => realms.ContainsKey(id);

        /// <summary>A realm by its decimal id or its name (case-insensitive)</summary>
        public static RealmDefinition Find(string idOrName)
        {
            if (ushort.TryParse(idOrName, out var id))
                return Get(id);

            return All.FirstOrDefault(realm => string.Equals(realm.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The value of a rule in a realm: what the file sets, else the registry default</summary>
        public static bool GetRule(ushort realmId, RealmRule rule) =>
            realms.TryGetValue(realmId, out var realm) && realm.Rules.TryGetValue(rule.Key, out var value) ? value : rule.Default;

        /// <summary>The realm of a position's instance. Null is the base world.</summary>
        public static ushort RealmOf(uint? instance)
        {
            Position.ParseInstanceID(instance ?? 0, out _, out var realmId, out _);
            return realmId;
        }
    }
}

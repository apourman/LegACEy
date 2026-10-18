using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Entity;
using ACE.Server.Managers;

namespace ACE.Server.Realms
{
    /// <summary>
    /// Private, temporary copies of a dungeon template. A copy is destroyed when it has been empty past EmptyGrace,
    /// or when its ttlMinutes run out. Players still inside are moved to the template's exit first. Nothing in a copy
    /// persists; only players leave it.
    /// </summary>
    public static class EphemeralInstances
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>How long a copy may stay empty before it is destroyed</summary>
        public static readonly TimeSpan EmptyGrace = TimeSpan.FromMinutes(2);

        private static readonly TimeSpan sweepInterval = TimeSpan.FromSeconds(1);

        private static readonly object gate = new object();
        private static readonly ShortInstanceAllocator shortIds = new ShortInstanceAllocator();
        private static readonly Dictionary<uint, Copy> copies = new Dictionary<uint, Copy>();
        private static DateTime nextSweep;

        /// <summary>A copy as the commands read it. A snapshot, so readers never touch the live copy.</summary>
        public sealed record CopyInfo(uint Instance, string TemplateName, DateTime CreatedAt, TimeSpan Ttl, bool Destroying, int Players);

        /// <summary>A live copy. Only touched under the gate.</summary>
        private sealed class Copy
        {
            public uint Instance { get; init; }
            public RealmDefinition Template { get; init; }
            public DateTime CreatedAt { get; init; }
            public TimeSpan Ttl => TimeSpan.FromMinutes(Template.TtlMinutes.Value);

            /// <summary>When the copy last became empty. Null while a player is inside.</summary>
            public DateTime? EmptySince { get; set; }

            /// <summary>Set once the copy has ended. Its players are being moved out, and nobody may enter it.</summary>
            public bool Destroying { get; set; }

            /// <summary>Players already sent to the exit, by guid, so each is teleported once</summary>
            public HashSet<ulong> SentToExit { get; } = new HashSet<ulong>();

            /// <summary>Players inside at the last sweep</summary>
            public int Players { get; set; }
        }

        /// <summary>
        /// The copy's destruction decision. Destroy once the ttl has run out, even if players are inside, or once it has
        /// been empty (EmptySince is null while occupied) for the grace period.
        /// </summary>
        public static bool ShouldDestroy(DateTime now, DateTime createdAt, TimeSpan ttl, DateTime? emptySince, TimeSpan grace) =>
            now - createdAt >= ttl || (emptySince is DateTime since && now - since >= grace);

        /// <summary>A new copy of the template, with a fresh short instance id. Null when the realm has none left.</summary>
        public static CopyInfo Create(RealmDefinition template, DateTime now)
        {
            lock (gate)
            {
                var shortInstance = shortIds.Allocate(template.Id);
                if (shortInstance == null)
                    return null;

                var copy = new Copy
                {
                    Instance = Position.InstanceIDFromVars(template.Id, shortInstance.Value, isEphemeral: true),
                    Template = template,
                    CreatedAt = now,
                    EmptySince = now,
                };

                copies[copy.Instance] = copy;
                log.Info($"[INSTANCE] created 0x{copy.Instance:X8} from {template.Name} (ttl {copy.Ttl.TotalMinutes} min)");
                return Snapshot(copy);
            }
        }

        /// <summary>True for a copy that is live: created, and not being destroyed</summary>
        public static bool IsLive(uint instance)
        {
            lock (gate)
                return copies.TryGetValue(instance, out var copy) && !copy.Destroying;
        }

        public static IReadOnlyList<CopyInfo> All()
        {
            lock (gate)
                return copies.Values.Select(Snapshot).ToList();
        }

        private static CopyInfo Snapshot(Copy copy) =>
            new CopyInfo(copy.Instance, copy.Template.Name, copy.CreatedAt, copy.Ttl, copy.Destroying, copy.Players);

        /// <summary>
        /// Runs from the landblock tick, once a second. Ends copies whose time is up, moves their players to the exit,
        /// unloads them once empty, and frees the short id once none of their landblocks are loaded.
        /// </summary>
        public static void Sweep(DateTime now)
        {
            lock (gate)
            {
                if (copies.Count == 0 || now < nextSweep)
                    return;

                nextSweep = now + sweepInterval;

                // ponytail: polls once a second and scans every loaded landblock and online player; index by instance if copies get numerous
                var loaded = LandblockManager.GetLoadedLandblocks();
                var online = PlayerManager.GetAllOnline().Where(player => player.CurrentLandblock?.IsEphemeral == true).ToList();

                foreach (var copy in copies.Values.ToList())
                {
                    var players = online.Where(player => player.CurrentLandblock.Instance == copy.Instance).ToList();
                    var landblocks = loaded.Where(landblock => landblock.Instance == copy.Instance).ToList();

                    copy.Players = players.Count;
                    copy.EmptySince = players.Count > 0 ? null : copy.EmptySince ?? now;

                    if (!copy.Destroying && ShouldDestroy(now, copy.CreatedAt, copy.Ttl, copy.EmptySince, EmptyGrace))
                    {
                        copy.Destroying = true;
                        log.Info($"[INSTANCE] 0x{copy.Instance:X8} ends ({(players.Count > 0 ? $"{players.Count} player(s) moved to the exit" : "empty")})");
                    }

                    if (!copy.Destroying)
                        continue;

                    // the teleport runs on the world thread later, so CurrentLandblock still shows the copy until then
                    foreach (var player in players)
                        if (copy.SentToExit.Add(player.Guid.Full))
                            WorldManager.ThreadSafeTeleport(player, RealmPolicy.InHomeRealm(player, copy.Template.Exit));

                    // a copy unloads only when nobody is inside; its id is kept until its landblocks have gone.
                    // A landblock already queued by its own idle unload is skipped by AddToDestructionQueue.
                    if (players.Count == 0)
                    {
                        foreach (var landblock in landblocks)
                            LandblockManager.AddToDestructionQueue(landblock);

                        if (landblocks.Count == 0)
                        {
                            copies.Remove(copy.Instance);
                            shortIds.Release(copy.Template.Id, (ushort)copy.Instance);
                            log.Info($"[INSTANCE] 0x{copy.Instance:X8} destroyed");
                        }
                    }
                }
            }
        }
    }
}

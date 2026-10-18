using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

using log4net;

using ACE.Database;
using ACE.Database.Market;
using ACE.Server.ClientChannel;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

namespace ACE.Server.Market
{
    /// <summary>
    /// Appraises an item in the player's Vault for the LegACEy client's appraisal window. Vault items are not in the world, so the game's own
    /// identify cannot reach them: the server builds the same appraisal from the stored item and sends it on the channel, as the 0x00C9 body
    /// (item guid, then the appraisal), which the client reads as it reads the game's.
    ///
    /// Cost, since a player can ask repeatedly: the channel's rate limit and the Vault station gate apply, and one appraisal runs at a time per
    /// player. Each request reads the item's small Vault row by key (owner and row version, which changes whenever the row does, from this
    /// server or the market website). The item itself is read and built only when no appraisal for that row version is cached.
    /// </summary>
    public static class VaultAppraisal
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // Cleared when it grows past this many items, rather than tracking use: a Vault appraisal rebuilt is one read.
        private const int CacheLimit = 2000;

        private static readonly ConcurrentDictionary<uint, (uint RowVersion, byte[] Body)> cache = new ConcurrentDictionary<uint, (uint, byte[])>();

        // Players with an appraisal under way: one at a time each.
        private static readonly ConcurrentDictionary<uint, byte> appraising = new ConcurrentDictionary<uint, byte>();

        /// <summary>
        /// vault.appraise (body: the item guid). The reply is the 0x00C9 body for the item. Another account's item, or one not in a Vault, is refused
        /// the same way, without its name.
        /// </summary>
        public static void Handle(ChannelContext context)
        {
            uint itemGuid;
            try
            {
                using (var body = context.Body())
                    itemGuid = body.ReadUInt32();
            }
            catch (EndOfStreamException)
            {
                context.Fail(ChannelStatus.BadRequest, "An item id is required.");
                return;
            }

            if (!Vault.Available)
            {
                context.Fail(ChannelStatus.Unavailable, "The Vault is not available.");
                return;
            }

            var player = context.Player;

            if (!appraising.TryAdd(player.Guid.ClientGUID, 0))
            {
                context.Fail(ChannelStatus.RateLimited, "Still appraising the last item.");
                return;
            }

            var accountId = player.Character.AccountId;

            // database reads: off the world thread
            Task.Run(() =>
            {
                try
                {
                    var row = VaultStore.Get(itemGuid);

                    if (row == null || row.AccountId != accountId)
                    {
                        Finish(player, () => context.Fail(ChannelStatus.BadRequest, "That item is not in your Vault."));
                        return;
                    }

                    if (cache.TryGetValue(itemGuid, out var cached) && cached.RowVersion == row.RowVersion)
                    {
                        Finish(player, () => context.Reply(cached.Body));
                        return;
                    }

                    var biota = DatabaseManager.Shard.BaseDatabase.GetBiota(itemGuid, doNotAddToCache: true);

                    // the item is built and appraised on the world thread, as a withdrawal's is
                    WorldManager.EnqueueAction(new ActionEventDelegate(() => Finish(player, () => Build(context, player, itemGuid, row.RowVersion, biota))));
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] Appraisal of 0x{itemGuid:X8} for {player.Name} failed: {ex}");
                    Finish(player, () => context.Fail(ChannelStatus.Error, "The item could not be appraised."));
                }
            });
        }

        private static void Build(ChannelContext context, Player player, uint itemGuid, uint rowVersion, ACE.Database.Models.Shard.Biota biota)
        {
            try
            {
                BuildCore(context, player, itemGuid, rowVersion, biota);
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] Appraisal of 0x{itemGuid:X8} for {player.Name} failed: {ex}");
                context.Fail(ChannelStatus.Error, "The item could not be appraised.");
            }
        }

        private static void BuildCore(ChannelContext context, Player player, uint itemGuid, uint rowVersion, ACE.Database.Models.Shard.Biota biota)
        {
            var item = biota == null ? null : WorldObjectFactory.CreateWorldObject(biota);

            if (item == null)
            {
                log.Error($"[VAULT] {player.Name} appraised 0x{itemGuid:X8}, which has a Vault row but no item that could be built");
                context.Fail(ChannelStatus.Error, "The item could not be appraised.");
                return;
            }

            var body = ChannelWire.Body(w =>
            {
                w.Write(itemGuid);
                w.Write(new AppraiseInfo(item, player));
            });

            // Only what every player sees is cached: a staff examiner sees the scribe's account, and a lock's pick chance is the examiner's own.
            if (!player.IsAdmin && !player.IsSentinel && !player.IsEnvoy && !player.IsArch && !player.IsPsr && !item.IsLocked)
            {
                if (cache.Count >= CacheLimit)
                    cache.Clear();

                cache[itemGuid] = (rowVersion, body);
            }

            context.Reply(body);
        }

        /// <summary>Ends the player's appraisal, then sends its reply, so a request made on the reply is not refused as busy.</summary>
        private static void Finish(Player player, Action reply)
        {
            appraising.TryRemove(player.Guid.ClientGUID, out _);
            reply();
        }
    }
}

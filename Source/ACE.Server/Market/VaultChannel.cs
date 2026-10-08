using System;

using log4net;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Market
{
    /// <summary>
    /// The frozen channel every Vault item deposit and withdrawal at the chest takes (vault_channel_seconds, default 60), so the Vault is never a way out of a PK fight.
    /// It is its own state, not the PK logout flag: that flag makes every physical attack on the player a critical hit.
    /// A landed player attack (the PK timer update), death and logout cancel it. When the time is up it calls the Vault, which re-checks every rule.
    /// MMD notes don't channel.
    /// </summary>
    public sealed class VaultChannel
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public bool IsDeposit { get; }

        public uint ItemGuid { get; }

        public string ItemName { get; }

        /// <summary>
        /// For a withdrawal: the version the Vault row got when the channel marked it withdrawing
        /// </summary>
        public uint MarkedRowVersion { get; }

        /// <summary>
        /// The game bridge ticket that asked for this channel, marked done in the same save as the item
        /// </summary>
        public long? TicketId { get; }

        private readonly Action<VaultResult> completed;

        /// <summary>
        /// True if the channel froze the player, so it only lifts its own freeze
        /// </summary>
        private bool froze;

        private VaultChannel(bool isDeposit, uint itemGuid, string itemName, uint markedRowVersion, Action<VaultResult> completed, long? ticketId = null)
        {
            IsDeposit = isDeposit;
            ItemGuid = itemGuid;
            ItemName = itemName;
            MarkedRowVersion = markedRowVersion;
            TicketId = ticketId;
            this.completed = completed;
        }

        /// <summary>
        /// Starts a deposit channel, on the world thread. A refusal is reported at once; otherwise the Vault's result is reported when the channel ends.
        /// </summary>
        public static void StartDeposit(Player player, uint itemGuid, Action<VaultResult> completed = null, long? ticketId = null, bool afterConfirmation = false)
        {
            WorldObject item = null;
            var refusal = CheckStart(player, afterConfirmation) ?? Vault.CheckDeposit(player, itemGuid, out item);

            if (refusal != null)
            {
                Vault.Finish(player, refusal.Value, item?.Name, itemGuid, completed);
                return;
            }

            Begin(player, new VaultChannel(isDeposit: true, itemGuid, item.Name, markedRowVersion: 0, completed, ticketId));
        }

        /// <summary>
        /// Starts a withdrawal channel, on the world thread. The Vault row is marked withdrawing now, so it can't be listed while the player channels.
        /// A game bridge ticket given as ticketId is marked done in the same save as the item.
        /// </summary>
        public static void StartWithdraw(Player player, uint itemGuid, Action<VaultResult> completed = null, long? ticketId = null)
        {
            VaultItem row = null;
            VaultOutcome? refusal;

            try
            {
                refusal = CheckStart(player) ?? Vault.CheckWithdraw(player, itemGuid, null, out row, out _);

                if (refusal == null)
                {
                    var marked = VaultStore.TryMarkWithdrawing(itemGuid, player.Character.AccountId, row.RowVersion);

                    if (marked != null)
                    {
                        Begin(player, new VaultChannel(isDeposit: false, itemGuid, row.Name, marked.Value, completed, ticketId));
                        return;
                    }

                    // the row changed since it was read: say why, as the next attempt would
                    refusal = Vault.CheckWithdraw(player, itemGuid, null, out row, out _) ?? VaultOutcome.Withdrawing;
                }
            }
            catch (Exception ex)
            {
                // the checks and the mark are single statements, so nothing is half done. A mark whose answer was lost stays until the next restart.
                log.Error($"[VAULT] Withdrawal channel of 0x{itemGuid:X8} for {player.Name} failed to start: {ex}");
                refusal = VaultOutcome.SaveFailed;
            }

            Vault.Finish(player, refusal.Value, row?.Name, itemGuid, completed);
        }

        /// <summary>
        /// Cancels the player's channel, if any: the player is unfrozen now, and the item stays where it is.
        /// Safe from any thread the PK timer, death or logout run on; the result is reported on the world thread.
        /// </summary>
        public static void Cancel(Player player)
        {
            var channel = player.EndVaultChannel();

            if (channel == null)
                return;

            Unfreeze(player, channel);

            WorldManager.EnqueueAction(new ActionEventDelegate(() => Interrupt(player, channel)));
        }

        /// <summary>
        /// The reasons a channel can't start, whatever the item: another channel, a recent player fight, a trade, or anything else keeping the player busy
        /// </summary>
        internal static VaultOutcome? CheckStart(Player player, bool afterConfirmation = false)
        {
            if (player.IsVaultChannelling)
                return VaultOutcome.Channelling;

            // the same 2 minute window that delays a PK's logout
            if (player.PKLogoutActive)
                return VaultOutcome.RecentPlayerFight;

            if (player.IsTrading)
                return VaultOutcome.Trading;

            if (player.IsInDeathProcess || player.IsDead)
                return afterConfirmation ? VaultOutcome.Interrupted : VaultOutcome.Busy;

            if (player.IsBusy || player.Teleporting || player.suicideInProgress || player.IsLoggingOut || player.PKLogout)
                return VaultOutcome.Busy;

            return null;
        }

        private static void Begin(Player player, VaultChannel channel)
        {
            var seconds = (int)Math.Max(0, MarketSettings.Get(MarketSettings.ChannelSeconds));

            if (!player.TryStartVaultChannel(channel))
            {
                Interrupt(player, channel, VaultOutcome.Channelling);
                return;
            }

            if (!(player.IsFrozen ?? false))
            {
                player.IsFrozen = true;
                player.EnqueueBroadcastPhysicsState();
                channel.froze = true;
            }

            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(VaultMessages.ChannelStarted(channel.IsDeposit, channel.ItemName, seconds), ChatMessageType.Broadcast));

            if (channel.TicketId is long ticketId)
            {
                var progressTime = DateTime.UtcNow;
                DatabaseManager.Shard.SetTicketProgress(ticketId, TicketProgress.Channelling, progressTime, progressTime.AddSeconds(seconds), updated =>
                {
                    if (!updated)
                        log.Warn($"[VAULT] Ticket {ticketId} was no longer claimed when its withdrawal channel progress was written");
                });
            }

            // the world queue, not the player's: the Vault runs on the world thread
            var chain = new ActionChain();
            chain.AddDelaySeconds(seconds);
            chain.AddAction(WorldManager.ActionQueue, () => Complete(player, channel));
            chain.EnqueueChain();
        }

        private static void Complete(Player player, VaultChannel channel)
        {
            // cancelled meanwhile, perhaps followed by a new channel: this timer is not for it
            if (!player.TryEndVaultChannel(channel))
                return;

            Unfreeze(player, channel);

            // death and logout cancel the channel themselves; this is the backstop for any path that got past them
            if (player.IsInDeathProcess || player.IsDead || player.IsLoggingOut)
            {
                Interrupt(player, channel);
                return;
            }

            if (channel.IsDeposit)
            {
                Vault.Deposit(player, channel.ItemGuid, channel.completed, channel.TicketId);
                return;
            }

            Vault.Withdraw(player, channel.ItemGuid, channel.MarkedRowVersion, result =>
            {
                // a withdrawal that didn't happen leaves the item held again
                if (!result.Success)
                    Release(channel);

                channel.completed?.Invoke(result);
            }, channel.TicketId);
        }

        /// <summary>
        /// Ends a channel that won't reach the Vault: a marked withdrawal goes back to held, and the outcome is reported
        /// </summary>
        private static void Interrupt(Player player, VaultChannel channel, VaultOutcome outcome = VaultOutcome.Interrupted)
        {
            if (!channel.IsDeposit)
                Release(channel);

            Vault.Finish(player, outcome, channel.ItemName, channel.ItemGuid, channel.completed);
        }

        /// <summary>
        /// Lifts the channel's freeze. Called on whatever thread ends the channel, as PK logout's freeze is.
        /// </summary>
        private static void Unfreeze(Player player, VaultChannel channel)
        {
            // only the freeze this channel set, and a PK logout keeps its own
            if (!channel.froze || player.PKLogout || !(player.IsFrozen ?? false))
                return;

            player.IsFrozen = false;
            player.EnqueueBroadcastPhysicsState();
        }

        private static void Release(VaultChannel channel)
        {
            try
            {
                if (!VaultStore.TryReleaseWithdrawing(channel.ItemGuid, channel.MarkedRowVersion))
                    log.Warn($"[VAULT] Could not put 0x{channel.ItemGuid:X8} back to held after its withdrawal channel ended: the row changed or is gone");
            }
            catch (Exception ex)
            {
                // the row stays marked withdrawing, so it can't be listed or withdrawn, until the next restart releases it
                log.Error($"[VAULT] Could not put 0x{channel.ItemGuid:X8} back to held after its withdrawal channel ended; the next restart will: {ex.Message}");
            }
        }
    }
}

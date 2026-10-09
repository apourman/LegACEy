using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using log4net;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.Server.ClientChannel;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Market
{
    /// <summary>
    /// The Vault's actions on the in-band server channel, for the LegACEy client's Vault window. They call the Vault's own entry points,
    /// so every rule and the chat messages are unchanged. A single deposit or withdrawal goes through the transfer channel; a batch withdrawal skips it,
    /// as it is instant. Bodies are written with ChannelWire; the plugin's VaultProtocol reads them.
    /// </summary>
    public static class VaultChannelActions
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string List = "vault.list";
        public const string Deposit = "vault.deposit";
        public const string Withdraw = "vault.withdraw";
        public const string Check = "vault.check";
        public const string Move = "vault.move";
        public const string WithdrawBatch = "vault.withdraw_batch";

        /// <summary>
        /// The most items one vault.list reply holds. Must match VaultProtocol.PageSize in the LegACEy Vault client.
        /// It is also the most one batch withdrawal names.
        /// </summary>
        public const int PageSize = 100;

        /// <summary>
        /// The station every action above requires: the Vault chest in Yaraq
        /// </summary>
        public const string Station = "vault";

        /// <summary>
        /// Pushed after every Vault deposit, withdrawal or trade note change, whatever started it: this window's channel, a /vault trade note command or the website
        /// </summary>
        public const string Changed = "vault.changed";

        public static void Register()
        {
            ServerChannel.Register(List, HandleList, Station);
            ServerChannel.Register(Deposit, context => HandleTransfer(context, deposit: true), Station);
            ServerChannel.Register(Withdraw, context => HandleTransfer(context, deposit: false), Station);
            ServerChannel.Register(Check, HandleCheck, Station);
            ServerChannel.Register(Move, HandleMove, Station);
            ServerChannel.Register(WithdrawBatch, HandleWithdrawBatch, Station);
        }

        /// <summary>
        /// Withdraws a set of items at once, instantly and all or none (body: a count, then the item guids). The reply is the outcome: a byte for
        /// withdrawn, then the message, which is the first refusal's reason when nothing moved.
        /// </summary>
        private static void HandleWithdrawBatch(ChannelContext context)
        {
            if (!TryReadGuids(context, out var itemGuids))
                return;

            Vault.WithdrawMany(context.Player, itemGuids, result => context.Reply(TransferBody(result.Success, result.Message)));
        }

        /// <summary>
        /// A reply that says whether the action went ahead, and the message the player is told: the reason when it did not
        /// </summary>
        private static byte[] TransferBody(bool accepted, string message) => ChannelWire.Body(w =>
        {
            w.Write((byte)(accepted ? 1 : 0));
            ChannelWire.WriteString(w, message);
        });

        /// <summary>
        /// Reads a batch withdrawal's guids: a count from 1 to PageSize, then that many distinct guids. Anything else is a bad request.
        /// </summary>
        private static bool TryReadGuids(ChannelContext context, out uint[] itemGuids)
        {
            try
            {
                using (var body = context.Body())
                {
                    var count = body.ReadInt32();
                    if (count >= 1 && count <= PageSize)
                    {
                        var guids = new uint[count];
                        for (var index = 0; index < count; index++)
                            guids[index] = body.ReadUInt32();

                        if (guids.Distinct().Count() == count)
                        {
                            itemGuids = guids;
                            return true;
                        }
                    }
                }
            }
            catch (EndOfStreamException)
            {
                // a body cut short is the same bad request as one with bad bounds
            }

            itemGuids = null;
            context.Fail(ChannelStatus.BadRequest, $"Withdraw between 1 and {PageSize} different items.");
            return false;
        }

        /// <summary>
        /// Whether a deposit of the item would start now, and if not why: the same checks a deposit makes, without moving anything.
        /// The window asks while an item is dragged over it, to show the drop as invalid.
        /// </summary>
        private static void HandleCheck(ChannelContext context)
        {
            if (!TryReadGuid(context, out var itemGuid))
                return;

            WorldObject item = null;
            var refusal = Vault.Available ? VaultChannel.CheckStart(context.Player) ?? Vault.CheckDeposit(context.Player, itemGuid, out item) : VaultOutcome.NotAvailable;

            context.Reply(ChannelWire.Body(w =>
            {
                w.Write(itemGuid);
                w.Write((byte)(refusal == null ? 1 : 0));
                ChannelWire.WriteString(w, refusal == null ? string.Empty : VaultMessages.For(refusal.Value, item?.Name));
            }));
        }

        /// <summary>
        /// Moves an item to another place in the account's Vault order (body: item guid, index). Presentation only; any item state.
        /// </summary>
        private static void HandleMove(ChannelContext context)
        {
            uint itemGuid;
            int toIndex;

            try
            {
                using (var body = context.Body())
                {
                    itemGuid = body.ReadUInt32();
                    toIndex = body.ReadInt32();
                }
            }
            catch (EndOfStreamException)
            {
                context.Fail(ChannelStatus.BadRequest, "An item id and a position are required.");
                return;
            }

            var accountId = context.Player.Character.AccountId;

            Task.Run(() =>
            {
                try
                {
                    var moved = Vault.Available && VaultStore.Move(accountId, itemGuid, toIndex);
                    context.Reply(TransferBody(moved, moved ? string.Empty : "That item is no longer in your Vault."));
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] Channel move of 0x{itemGuid:X8} for account {accountId} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "Your Vault could not be rearranged.");
                }
            });
        }

        private static bool TryReadGuid(ChannelContext context, out uint itemGuid)
        {
            try
            {
                using (var body = context.Body())
                    itemGuid = body.ReadUInt32();
                return true;
            }
            catch (EndOfStreamException)
            {
                itemGuid = 0;
                context.Fail(ChannelStatus.BadRequest, "An item id is required.");
                return false;
            }
        }

        private readonly record struct ListRequest(string Search, int Offset, int Count);

        /// <summary>
        /// Reads what vault.list asks for: a search (empty for none), an offset and a count, in that order. The offset is kept at 0 or more and the count
        /// to 1..PageSize. Fields after the count are for a later version and ignored.
        /// </summary>
        private static bool TryReadListRequest(ChannelContext context, out ListRequest request)
        {
            try
            {
                using (var body = context.Body())
                {
                    var search = ChannelWire.ReadString(body);
                    var offset = body.ReadInt32();
                    var count = body.ReadInt32();
                    request = new ListRequest(search, Math.Max(0, offset), Math.Clamp(count, 1, PageSize));
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                request = default;
                context.Fail(ChannelStatus.BadRequest, "A search, an offset and a count are required.");
                return false;
            }
        }

        private static void HandleList(ChannelContext context)
        {
            if (!Vault.Available)
            {
                context.Reply(ChannelWire.Body(w => w.Write((byte)0)));
                return;
            }

            if (!TryReadListRequest(context, out var request))
                return;

            var player = context.Player;
            var capacity = (int)MarketSettings.Get(MarketSettings.VaultSize);
            var marketOpen = Vault.MarketEnabled;

            // database reads: off the world thread, replying when done
            Task.Run(() =>
            {
                try
                {
                    var page = Vault.Page(player, request.Search, request.Offset, request.Count);
                    // NoBalance while the marketplace is closed: the window shows no MMD
                    var balance = marketOpen ? Vault.Balance(player) : NoBalance;
                    context.Reply(ListBody(page, balance, capacity));
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] Channel list for {player.Name} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "Your Vault could not be read.");
                }
            });
        }

        /// <summary>
        /// The balance vault.list sends while market_enabled is off
        /// </summary>
        public const long NoBalance = -1;

        internal static byte[] ListBody(VaultPage page, long balance, int capacity)
        {
            return ChannelWire.Body(w =>
            {
                w.Write((byte)1);
                w.Write(balance);
                w.Write(capacity);
                w.Write(page.VaultCount);
                w.Write(page.Total);
                w.Write(page.Items.Count);

                foreach (var item in page.Items)
                {
                    w.Write(item.ItemGuid);
                    ChannelWire.WriteString(w, item.Name);
                    w.Write(unchecked((uint)item.ItemType));
                    w.Write(item.StackSize);
                    w.Write(item.Value);
                    ChannelWire.WriteString(w, item.State);
                    ChannelWire.WriteString(w, PlayerManager.FindByGuid(item.CharacterId)?.Name ?? string.Empty);
                    w.Write(new DateTimeOffset(DateTime.SpecifyKind(item.DepositedTime, DateTimeKind.Utc)).ToUnixTimeSeconds());
                    w.Write(Plate(item.ItemType));
                    w.Write(item.IconUnderlay ?? 0);
                    w.Write(item.Icon ?? 0);
                    w.Write(item.IconOverlay ?? 0);
                    w.Write(item.IconOverlaySecondary ?? 0);
                    w.Write(item.UiEffects ?? 0);
                }
            });
        }

        /// <summary>
        /// Starts a deposit or withdrawal channel. The reply says whether it started; the outcome comes later as a vault.changed push.
        /// </summary>
        private static void HandleTransfer(ChannelContext context, bool deposit)
        {
            if (!TryReadGuid(context, out var itemGuid))
                return;

            // a refusal is reported through the callback before Start returns; a channel that started reports when it ends
            var starting = true;
            VaultResult refusal = null;

            void Completed(VaultResult result)
            {
                if (starting)
                    refusal = result;
            }

            if (deposit)
                VaultChannel.StartDeposit(context.Player, itemGuid, Completed);
            else
                VaultChannel.StartWithdraw(context.Player, itemGuid, Completed);

            starting = false;

            context.Reply(TransferBody(refusal == null, refusal?.Message ?? string.Empty));
        }

        /// <summary>
        /// Tells the player's Vault window about a finished deposit, withdrawal or trade note change. Any thread.
        /// </summary>
        public static void PushChanged(Player player, VaultResult result)
        {
            if (!ServerChannel.IsConnected(player))
                return;

            ServerChannel.Push(player, Changed, ChannelWire.Body(w =>
            {
                w.Write((byte)(result.Success ? 1 : 0));
                ChannelWire.WriteString(w, result.Outcome.ToString());
                ChannelWire.WriteString(w, result.Message);
                w.Write(result.ItemGuid);
            }));
        }

        /// <summary>
        /// The plate the client draws under an icon, by item type (as ACE.MarketApi's ItemIcons)
        /// </summary>
        private static uint Plate(int itemType)
        {
            var type = (ItemType)unchecked((uint)itemType);

            if ((type & ItemType.WeaponOrCaster) != 0)
                return 0x060011D2;
            if ((type & ItemType.Armor) != 0)
                return 0x060011CF;
            if ((type & ItemType.Clothing) != 0)
                return 0x060011F3;
            if ((type & ItemType.Jewelry) != 0)
                return 0x060011D5;
            if ((type & ItemType.Gem) != 0)
                return 0x060011D3;

            return 0x060011D4;
        }
    }
}

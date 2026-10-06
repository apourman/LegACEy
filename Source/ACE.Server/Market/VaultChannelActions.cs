using System;
using System.Collections.Generic;
using System.IO;
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
    /// The Vault's actions on the in-band server channel, for the LegACEy client's Vault window. They call the same entry points as the /vault commands,
    /// so every rule, the transfer channel and the chat messages are unchanged. Bodies are written with ChannelWire; the plugin's VaultProtocol reads them.
    /// </summary>
    public static class VaultChannelActions
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string Hello = "channel.hello";
        public const string List = "vault.list";
        public const string Deposit = "vault.deposit";
        public const string Withdraw = "vault.withdraw";

        /// <summary>
        /// Pushed after every Vault deposit, withdrawal or trade note change, whatever started it: this window, a /vault command or the website
        /// </summary>
        public const string Changed = "vault.changed";

        private static readonly string[] actions = { Hello, List, Deposit, Withdraw };

        public static void Register()
        {
            ServerChannel.Register(Hello, HandleHello);
            ServerChannel.Register(List, HandleList);
            ServerChannel.Register(Deposit, context => HandleTransfer(context, deposit: true));
            ServerChannel.Register(Withdraw, context => HandleTransfer(context, deposit: false));
        }

        private static void HandleHello(ChannelContext context)
        {
            context.Reply(ChannelWire.Body(w =>
            {
                w.Write(ChannelWire.Version);
                ChannelWire.WriteString(w, context.Player.Name);
                w.Write(actions.Length);
                foreach (var action in actions)
                    ChannelWire.WriteString(w, action);
            }));
        }

        private static void HandleList(ChannelContext context)
        {
            if (!Vault.Available)
            {
                context.Reply(ChannelWire.Body(w => w.Write((byte)0)));
                return;
            }

            var player = context.Player;
            var capacity = (int)MarketSettings.Get(MarketSettings.VaultSize);

            // database reads: off the world thread, replying when done
            Task.Run(() =>
            {
                try
                {
                    var items = Vault.List(player);
                    var balance = Vault.Balance(player);
                    context.Reply(ListBody(items, balance, capacity));
                }
                catch (Exception ex)
                {
                    log.Error($"[VAULT] Channel list for {player.Name} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "Your Vault could not be read.");
                }
            });
        }

        internal static byte[] ListBody(IReadOnlyList<VaultItem> items, long balance, int capacity)
        {
            return ChannelWire.Body(w =>
            {
                w.Write((byte)1);
                w.Write(balance);
                w.Write(capacity);
                w.Write(items.Count);

                foreach (var item in items)
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

            context.Reply(ChannelWire.Body(w =>
            {
                w.Write((byte)(refusal == null ? 1 : 0));
                ChannelWire.WriteString(w, refusal?.Message ?? string.Empty);
            }));
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

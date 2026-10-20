using System;
using System.IO;
using System.Threading.Tasks;

using log4net;

using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Server.ClientChannel;

namespace ACE.Server.Market
{
    /// <summary>
    /// The Activity screen's actions on the in-band server channel: the ledger's history with its cursors, and trade notes withdrawn into the pack.
    /// Like MarketChannelActions, each needs the market station and is refused with a plain message while the market is closed
    /// (MarketChannelActions.Open). Withdrawal is the Vault's own, so its rules and messages are unchanged. Bodies are written with ChannelWire.
    /// </summary>
    public static class MarketActivityActions
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string History = "market.history";
        public const string WithdrawNotes = "market.withdraw_notes";

        /// <summary>
        /// The cursor that means the newest page
        /// </summary>
        public const long Newest = -1;

        /// <summary>
        /// The week's sales and purchases cover this many days of the ledger
        /// </summary>
        private static readonly TimeSpan Week = TimeSpan.FromDays(7);

        public static void Register()
        {
            ServerChannel.Register(History, HandleHistory, MarketChannelActions.Station);
            ServerChannel.Register(WithdrawNotes, HandleWithdrawNotes, MarketChannelActions.Station);
        }

        /// <summary>
        /// The history request: the ledger cursor, then the item movement cursor (int64 each). Each is the id a previous page ended before, or -1 for the newest page.
        /// The reply: balance (int64); MMD earned from sales and spent on purchases this week (int64 each) and the items sold and bought (int32 each), all
        /// zero unless this is the newest page; the ledger lines (int32 count, then sequence, amount, balance after and time as int64, then the wording);
        /// the next ledger cursor (int64, -1 when there is no older page); the item movements (int32 count, then id and time as int64, then the kind and the
        /// wording); the next item cursor (int64, -1 when there is no older page). Times are Unix seconds.
        /// </summary>
        private static void HandleHistory(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context))
                return;

            long transfersBefore;
            long itemsBefore;

            try
            {
                using var body = context.Body();
                transfersBefore = body.ReadInt64();
                itemsBefore = body.ReadInt64();
            }
            catch (EndOfStreamException)
            {
                context.Fail(ChannelStatus.BadRequest, "The history cursors are missing.");
                return;
            }

            if (!ValidCursor(transfersBefore) || !ValidCursor(itemsBefore))
            {
                context.Fail(ChannelStatus.BadRequest, "A history cursor is an id from an earlier page, or -1 for the newest.");
                return;
            }

            var newest = transfersBefore == Newest && itemsBefore == Newest;
            var accountId = context.Player.Character.AccountId;
            var characterName = context.Player.Name;

            // database reads: off the world thread, replying when done
            Task.Run(async () =>
            {
                try
                {
                    using var shard = new ShardDbContext();

                    var page = await MarketHistory.ReadAsync(shard, accountId, null, Cursor(transfersBefore), Cursor(itemsBefore), null, null);

                    // the week's figures are the newest page's; a page scrolled back to doesn't need them again
                    var flow = newest ? await MarketHistory.ReadFlowAsync(shard, accountId, DateTime.UtcNow - Week) : new MarketHistory.Flow(0, 0, 0, 0);

                    context.Reply(ChannelWire.Body(w =>
                    {
                        w.Write(page.Balance);
                        w.Write(flow.Earned);
                        w.Write(flow.Spent);
                        w.Write(flow.ItemsSold);
                        w.Write(flow.ItemsBought);

                        w.Write(page.Lines.Count);
                        foreach (var line in page.Lines)
                        {
                            w.Write(line.Sequence);
                            w.Write(line.Amount);
                            w.Write(line.BalanceAfter);
                            w.Write(MarketSellActions.UnixSeconds(line.Time));
                            ChannelWire.WriteString(w, line.Text);
                        }
                        w.Write(page.NextTransfersBefore ?? Newest);

                        w.Write(page.Movements.Count);
                        foreach (var movement in page.Movements)
                        {
                            w.Write(movement.Id);
                            w.Write(MarketSellActions.UnixSeconds(movement.Time));
                            ChannelWire.WriteString(w, movement.Kind);
                            ChannelWire.WriteString(w, movement.Text);
                        }
                        w.Write(page.NextItemsBefore ?? Newest);
                    }));
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] Channel history for {characterName} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "Your history could not be read.");
                }
            });
        }

        /// <summary>
        /// Withdraws MMD as trade notes into the pack (body: the amount, int64). The reply is as the Vault's note withdrawal: a byte for
        /// withdrawn, then the message, which is the reason when nothing moved.
        /// </summary>
        private static void HandleWithdrawNotes(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context))
                return;

            long amount;

            try
            {
                using var body = context.Body();
                amount = body.ReadInt64();
            }
            catch (EndOfStreamException)
            {
                context.Fail(ChannelStatus.BadRequest, "An amount of MMD is required.");
                return;
            }

            Vault.WithdrawNotes(context.Player, amount, result => context.Reply(ChannelWire.Body(w =>
            {
                w.Write((byte)(result.Success ? 1 : 0));
                ChannelWire.WriteString(w, result.Message);
            })));
        }

        private static bool ValidCursor(long cursor) => cursor == Newest || cursor >= 1;

        private static long? Cursor(long cursor) => cursor == Newest ? null : cursor;
    }
}

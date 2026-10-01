using System;
using System.Collections.Generic;

using ACE.Common.Extensions;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database
{
    /// <summary>
    /// The game server's side of the game bridge queue, and the market cleanup that rides on its timer. Run these on the serialized shard save queue (SerializedShardDatabase), the game's database thread.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Claims up to limit waiting tickets for this server. An empty list if there are none, or if the read failed (logged).
        /// </summary>
        public List<Ticket> ClaimTickets(int limit)
        {
            try
            {
                using (var context = new ShardDbContext())
                    return TicketStore.Claim(context, limit, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][BRIDGE] Claiming tickets failed: {ex.GetFullMessage()}");
                return new List<Ticket>();
            }
        }

        /// <summary>
        /// Marks a claimed ticket FAILED. False if it isn't CLAIMED any more, or the write failed (logged).
        /// </summary>
        public bool FailTicket(long ticketId, string resultCode, string message)
        {
            try
            {
                using (var context = new ShardDbContext())
                    return TicketStore.Fail(context, ticketId, resultCode, message, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][BRIDGE] Failing ticket {ticketId} ({resultCode}) failed: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Deletes tickets that finished more than TicketStore.KeepDays ago. Returns how many, or 0 if the delete failed (logged).
        /// </summary>
        public int DeleteFinishedTickets()
        {
            try
            {
                using (var context = new ShardDbContext())
                    return TicketStore.DeleteFinished(context, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][BRIDGE] Deleting finished tickets failed: {ex.GetFullMessage()}");
                return 0;
            }
        }

        /// <summary>
        /// Deletes market rows nothing reads any more (see MarketCleanup.Run). Null if the delete failed (logged).
        /// </summary>
        public MarketCleanupReport DeleteExpiredMarketRows()
        {
            try
            {
                using (var context = new ShardDbContext())
                    return MarketCleanup.Run(context, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][MARKET] Deleting expired market rows failed: {ex.GetFullMessage()}");
                return null;
            }
        }
    }
}

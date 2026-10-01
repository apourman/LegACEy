using System;
using System.Collections.Generic;

using ACE.Common.Extensions;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database
{
    /// <summary>
    /// The game server's side of the game bridge queue. Run these on the serialized shard save queue (SerializedShardDatabase), the game's database thread.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>
        /// Claims up to limit waiting tickets for this server. An empty list if there are none, or if the claim failed (logged).
        /// A claim that failed part way puts the tickets it had claimed back to WAITING, so the next poll gets them;
        /// if even that fails, FailAbandonedTickets fails them later.
        /// </summary>
        public List<Ticket> ClaimTickets(int limit)
        {
            var claimed = new List<long>();

            try
            {
                using (var context = new ShardDbContext())
                    return TicketStore.Claim(context, limit, DateTime.UtcNow, claimed);
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][BRIDGE] Claiming tickets failed: {ex.GetFullMessage()}");

                if (claimed.Count > 0)
                {
                    try
                    {
                        using (var context = new ShardDbContext())
                            log.Warn($"[DATABASE][BRIDGE] Put {TicketStore.Unclaim(context, claimed):N0} of the {claimed.Count:N0} ticket(s) the failed claim had claimed back to waiting");
                    }
                    catch (Exception unclaimEx)
                    {
                        log.Error($"[DATABASE][BRIDGE] Could not put tickets {string.Join(", ", claimed)} back to waiting; they will be failed as abandoned: {unclaimEx.GetFullMessage()}");
                    }
                }

                return new List<Ticket>();
            }
        }

        /// <summary>
        /// Fails tickets claimed more than claimedFor ago that aren't in running, the tickets the game is working on now (see TicketStore.FailAbandoned).
        /// Returns how many, or 0 if the update failed (logged).
        /// </summary>
        public int FailAbandonedTickets(IReadOnlyCollection<long> running, TimeSpan claimedFor, string message)
        {
            try
            {
                var now = DateTime.UtcNow;

                using (var context = new ShardDbContext())
                    return TicketStore.FailAbandoned(context, running, now - claimedFor, message, now);
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][BRIDGE] Failing abandoned tickets failed: {ex.GetFullMessage()}");
                return 0;
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
    }
}

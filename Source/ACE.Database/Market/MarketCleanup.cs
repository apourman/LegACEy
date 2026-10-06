using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;

namespace ACE.Database.Market
{
    /// <summary>
    /// How many rows one cleanup deleted, per table
    /// </summary>
    public sealed record MarketCleanupReport(int Requests, int LinkCodes, int PluginTokens, int WebSessions)
    {
        public int Total => Requests + LinkCodes + PluginTokens + WebSessions;
    }

    /// <summary>
    /// Deletes market rows nothing reads any more, so the tables don't grow forever.
    /// Finished game bridge tickets are not touched here: TicketStore.DeleteFinished is their cleanup, run by the game bridge.
    /// </summary>
    public static class MarketCleanup
    {
        /// <summary>
        /// Stored request results (purchase replays) are kept this long, as the spec says
        /// </summary>
        public const int RequestKeepDays = 30;

        /// <summary>
        /// Revoked and expired plugin tokens are kept this long after they stopped working, so an admin looking into a lost device can still see them.
        /// Nothing else reads them: a dead token is refused whether its row exists or not.
        /// </summary>
        public const int PluginTokenKeepDays = 30;

        /// <summary>
        /// Web sessions that ended (revoked, idle too long, or past their absolute expiry) are kept this long, so an admin can still see recent ones.
        /// Nothing else reads them: an ended session is refused whether its row exists or not.
        /// </summary>
        public const int WebSessionKeepDays = 7;

        /// <summary>
        /// Deletes, each in one statement:
        /// request results older than RequestKeepDays;
        /// link codes that were used or have expired (a code can never be redeemed again, so they're deleted at once);
        /// plugin tokens revoked or expired more than PluginTokenKeepDays ago;
        /// web sessions revoked or expired more than WebSessionKeepDays ago.
        /// </summary>
        public static MarketCleanupReport Run(ShardDbContext context, DateTime now)
        {
            var requestsBefore = now - TimeSpan.FromDays(RequestKeepDays);
            var tokensBefore = now - TimeSpan.FromDays(PluginTokenKeepDays);
            var sessionsBefore = now - TimeSpan.FromDays(WebSessionKeepDays);

            var requests = context.MarketRequests
                .Where(r => r.CreatedTime < requestsBefore)
                .ExecuteDelete();

            // PluginAuth.FindLinkCode only accepts a code that is unused and now < ExpiresTime
            var linkCodes = context.MarketLinkCodes
                .Where(c => c.UsedTime != null || c.ExpiresTime <= now)
                .ExecuteDelete();

            var pluginTokens = context.MarketPluginTokens
                .Where(t => t.RevokedTime < tokensBefore || t.ExpiresTime < tokensBefore)
                .ExecuteDelete();

            // the idle expiry is never past the absolute one, but both are checked so a hand-edited row is still cleaned up
            var webSessions = context.MarketWebSessions
                .Where(s => s.RevokedTime < sessionsBefore || s.IdleExpiresTime < sessionsBefore || s.AbsoluteExpiresTime < sessionsBefore)
                .ExecuteDelete();

            return new MarketCleanupReport(requests, linkCodes, pluginTokens, webSessions);
        }
    }
}

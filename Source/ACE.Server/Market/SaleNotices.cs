using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Market;
using ACE.Server.ClientChannel;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.Market
{
    /// <summary>
    /// Tells an online seller's client of each sale, whichever front door made it: a market.sold push with the listing, the item, the buyer, the
    /// price and the balance after. The game's buy pushes at once; the game bridge's poll, about once a second, finds the website's sales. Both read
    /// each online seller's ledger lines after the last one pushed (the per-account sequence, which every committed line is numbered by), so a sale
    /// is pushed once whoever finds it first. A seller is told only of sales made since their session's login; one made while they were offline
    /// shows only in the ledger. One client hears each sale: the listing's character when it's online, else the account's character logged in last.
    /// Nothing is stored: a restart forgets which lines were pushed, and starts again from each seller's login. The push needs no station.
    /// </summary>
    public static class SaleNotices
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string Sold = "market.sold";

        // ponytail: one gate for every seller; per-account gates if the pushes ever queue
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        /// <summary>The last ledger sequence pushed up to, by online seller account. Only the gate's holder touches it.</summary>
        private static readonly Dictionary<uint, long> pushedUpTo = new Dictionary<uint, long>();

        /// <summary>An online character whose client uses the channel, as the world thread read it</summary>
        private sealed record Seller(uint AccountId, uint CharacterId, DateTime LoggedIn, Session Session);

        /// <summary>The game bridge's poll, on the world thread: skipped while a push is still running</summary>
        public static void Poll()
        {
            if (gate.CurrentCount == 0)
                return;

            Start();
        }

        /// <summary>
        /// Pushes every online seller's sales since the last push. On the world thread, where the online characters are read; the database work
        /// runs off it.
        /// </summary>
        public static void Start()
        {
            var sellers = PlayerManager.GetAllOnline()
                .Where(p => !p.IsLoggingOut && p.LoginTimestamp != null && ServerChannel.IsConnected(p))
                .Select(p => new Seller(p.Character.AccountId, p.Guid.ClientGUID, DateTime.UnixEpoch.AddSeconds(p.LoginTimestamp.Value), p.Session))
                .ToList();

            Task.Run(() => Push(sellers));
        }

        private static async Task Push(IReadOnlyList<Seller> sellers)
        {
            await gate.WaitAsync();
            try
            {
                var online = sellers.GroupBy(s => s.AccountId).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var gone in pushedUpTo.Keys.Where(account => !online.ContainsKey(account)).ToList())
                    pushedUpTo.Remove(gone);

                if (online.Count == 0)
                    return;

                using var shard = MarketChannelActions.Database.CreateShard();
                var accounts = online.Keys.ToList();
                var heads = await shard.MarketBalances.AsNoTracking().Where(b => accounts.Contains(b.AccountId))
                    .Select(b => new { b.AccountId, b.LastSequence }).ToListAsync();

                foreach (var head in heads)
                {
                    try
                    {
                        pushedUpTo.TryGetValue(head.AccountId, out var since);
                        if (head.LastSequence <= since)
                            continue;

                        // from the session's login: a quick logout and login leaves the sales between them to the ledger
                        var characters = online[head.AccountId];
                        var sales = await MarketHistory.ReadSalesAsync(shard, head.AccountId, since, head.LastSequence, characters.Min(c => c.LoggedIn));
                        pushedUpTo[head.AccountId] = head.LastSequence;

                        foreach (var sale in sales)
                        {
                            var to = characters.FirstOrDefault(c => c.CharacterId == sale.SellerCharacterId) ?? characters.OrderByDescending(c => c.LoggedIn).First();
                            ServerChannel.Send(to.Session, ChannelEventKind.Push, ChannelStatus.Ok, 0, Sold, Body(sale));
                        }
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[MARKET] Pushing account {head.AccountId}'s sales failed: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] Pushing sales to their sellers failed: {ex}");
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>The push: listing id, item name, buyer name, price, balance after</summary>
        private static byte[] Body(MarketHistory.SaleNotice sale) => ChannelWire.Body(w =>
        {
            w.Write(sale.ListingId);
            ChannelWire.WriteString(w, sale.Item);
            ChannelWire.WriteString(w, sale.Buyer);
            w.Write(sale.Price);
            w.Write(sale.BalanceAfter);
        });
    }
}

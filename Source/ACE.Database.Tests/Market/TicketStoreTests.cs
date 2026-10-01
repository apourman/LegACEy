using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;

namespace ACE.Database.Tests.Market
{
    /// <summary>
    /// Seam 3: the game bridge's queue against real MySQL. Pollers claim with conditional updates, and a ticket is finished in the same save as its work.
    /// One scratch shard per test, so a claim only ever sees the test's own tickets.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class TicketStoreTests
    {
        private const string Db = "ace_shard_market_tickets";

        private static uint nextAccountId = 760000;

        [TestInitialize]
        public void Setup()
        {
            MarketTestDatabase.InitializeConfig();

            var failures = MarketTestDatabase.CreateFresh(Db);
            if (failures.TryGetValue(MarketTestDatabase.MarketUpdateScript, out var ex))
                throw ex;
        }

        [TestCleanup]
        public void Cleanup() => MarketTestDatabase.Drop(Db);

        [TestMethod]
        public void Claim_ManyPollersAtOnce_EachTicketIsClaimedExactlyOnce()
        {
            var created = Enumerable.Range(0, 60).Select(i => NewTicket($"claim-{i}")).ToList();

            var claims = new ConcurrentBag<long>();
            var start = new Barrier(4);

            var pollers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();

                while (true)
                {
                    using var context = MarketTestDatabase.CreateContext(Db);
                    var claimed = TicketStore.Claim(context, 7, DateTime.UtcNow);

                    if (claimed.Count == 0)
                        return;

                    foreach (var ticket in claimed)
                    {
                        Assert.AreEqual(TicketStatus.Claimed, ticket.Status, "a claimed ticket comes back CLAIMED");
                        claims.Add(ticket.Id);
                    }
                }
            })).ToArray();

            Assert.IsTrue(Task.WaitAll(pollers, TimeSpan.FromSeconds(60)), "the pollers finished");

            Assert.AreEqual(created.Count, claims.Count, "no ticket was claimed twice");
            CollectionAssert.AreEquivalent(created, claims.ToList(), "every ticket was claimed");
            Assert.AreEqual(created.Count, Count($"SELECT COUNT(*) FROM market_ticket WHERE status = '{TicketStatus.Claimed}' AND claimed_Time IS NOT NULL;"));
        }

        [TestMethod]
        public void Claim_TakesWaitingTicketsOnlyOldestFirst_UpToTheLimit()
        {
            var first = NewTicket("a");
            var second = NewTicket("b");
            var third = NewTicket("c");
            Fail(second);

            using var context = MarketTestDatabase.CreateContext(Db);

            CollectionAssert.AreEqual(new[] { first }, TicketStore.Claim(context, 1, DateTime.UtcNow).Select(t => t.Id).ToList());
            CollectionAssert.AreEqual(new[] { third }, TicketStore.Claim(context, 10, DateTime.UtcNow).Select(t => t.Id).ToList(), "a finished ticket is never claimed");
            Assert.AreEqual(0, TicketStore.Claim(context, 10, DateTime.UtcNow).Count);
        }

        [TestMethod]
        public void Fail_OnlyAClaimedTicket_WithItsReason()
        {
            var waiting = NewTicket("w");
            var claimed = NewTicket("c");
            Claim(claimed);

            using var context = MarketTestDatabase.CreateContext(Db);

            Assert.IsFalse(TicketStore.Fail(context, waiting, "offline", "not online", DateTime.UtcNow), "a waiting ticket wasn't claimed");
            Assert.IsTrue(TicketStore.Fail(context, claimed, "offline", "not online", DateTime.UtcNow));
            Assert.IsFalse(TicketStore.Fail(context, claimed, "offline", "again", DateTime.UtcNow), "a finished ticket stays as it finished");

            Assert.AreEqual($"{TicketStatus.Waiting}|NULL|NULL|0", Row(waiting));
            Assert.AreEqual($"{TicketStatus.Failed}|offline|not online|1", Row(claimed));
        }

        [TestMethod]
        public void FailAllClaimed_FailsEveryClaimedTicketAndNothingElse()
        {
            var waiting = NewTicket("w");
            var claimed = NewTicket("c1");
            var alsoClaimed = NewTicket("c2");
            var done = NewTicket("d");
            Claim(claimed);
            Claim(alsoClaimed);
            Claim(done);
            CompleteAlone(done);

            using var context = MarketTestDatabase.CreateContext(Db);

            Assert.AreEqual(2, TicketStore.FailAllClaimed(context, "restarted", DateTime.UtcNow));

            Assert.AreEqual($"{TicketStatus.Waiting}|NULL|NULL|0", Row(waiting), "a waiting ticket is still claimed later");
            Assert.AreEqual($"{TicketStatus.Failed}|{TicketStore.ServerRestart}|restarted|1", Row(claimed));
            Assert.AreEqual($"{TicketStatus.Failed}|{TicketStore.ServerRestart}|restarted|1", Row(alsoClaimed));
            Assert.AreEqual($"{TicketStatus.Done}|{TicketStore.Ok}|done|1", Row(done));
        }

        [TestMethod]
        public void Complete_SavedWithTheCallersOtherChanges_InOneSaveOrNotAtAll()
        {
            var claimed = NewTicket("c");
            Claim(claimed);
            var account = NewAccountId();

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketBlockedWcids.Add(new BlockedWcid { Wcid = account, Reason = "the caller's own change", AddedByAccountId = 1, AddedTime = DateTime.UtcNow });
                TicketStore.Complete(context, new TicketCompletion(claimed, "done"), DateTime.UtcNow);
                context.SaveChanges();
            }

            Assert.AreEqual($"{TicketStatus.Done}|{TicketStore.Ok}|done|1", Row(claimed));
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_blocked_wcid WHERE wcid = {account};"));

            // a ticket that is no longer CLAIMED (a restart failed it) takes the caller's whole save down with it
            var failed = NewTicket("f");
            Claim(failed);
            using (var context = MarketTestDatabase.CreateContext(Db))
                TicketStore.FailAllClaimed(context, "restarted", DateTime.UtcNow);

            using (var context = MarketTestDatabase.CreateContext(Db))
            {
                context.MarketBlockedWcids.Add(new BlockedWcid { Wcid = account + 1, Reason = "the caller's own change", AddedByAccountId = 1, AddedTime = DateTime.UtcNow });
                TicketStore.Complete(context, new TicketCompletion(failed, "done"), DateTime.UtcNow);
                Assert.ThrowsExactly<DbUpdateConcurrencyException>(() => context.SaveChanges());
            }

            Assert.AreEqual($"{TicketStatus.Failed}|{TicketStore.ServerRestart}|restarted|1", Row(failed));
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_blocked_wcid WHERE wcid = {account + 1};"), "nothing of the caller's save was written");
        }

        [TestMethod]
        public void DeleteFinished_RemovesFinishedTicketsOlderThanThirtyDays_KeepsTheRest()
        {
            var now = DateTime.UtcNow;
            var oldDone = NewTicket("old-done");
            var oldFailed = NewTicket("old-failed");
            var recentDone = NewTicket("recent-done");
            var oldWaiting = NewTicket("old-waiting");
            var oldClaimed = NewTicket("old-claimed");
            Claim(oldDone);
            Claim(oldFailed);
            Claim(recentDone);
            Claim(oldClaimed);
            CompleteAlone(oldDone);
            CompleteAlone(recentDone);
            using (var context = MarketTestDatabase.CreateContext(Db))
                Assert.IsTrue(TicketStore.Fail(context, oldFailed, "offline", "not online", now));

            Backdate(oldDone, finishedDaysAgo: 31);
            Backdate(oldFailed, finishedDaysAgo: 30.01);
            Backdate(recentDone, finishedDaysAgo: 29.99);
            Backdate(oldWaiting, finishedDaysAgo: null);
            Backdate(oldClaimed, finishedDaysAgo: null);

            using (var context = MarketTestDatabase.CreateContext(Db))
                Assert.AreEqual(2, TicketStore.DeleteFinished(context, now));

            CollectionAssert.AreEquivalent(new[] { recentDone, oldWaiting, oldClaimed }, Ids());
        }

        // ---- helpers

        private static uint NewAccountId() => Interlocked.Increment(ref nextAccountId);

        private static long NewTicket(string key)
        {
            using var context = MarketTestDatabase.CreateContext(Db);
            var result = TicketStore.Create(context, NewAccountId(), 0x50000001, TicketKind.MmdWithdraw, new TicketPayload(Amount: 5), key, DateTime.UtcNow);
            Assert.AreEqual(TicketCreateOutcome.Created, result.Outcome);
            return result.Ticket.Id;
        }

        private static void Claim(long ticketId)
        {
            MarketTestDatabase.Execute(Db, $"UPDATE market_ticket SET status = '{TicketStatus.Claimed}', claimed_Time = UTC_TIMESTAMP(6) WHERE id = {ticketId};");
        }

        private static void Fail(long ticketId)
        {
            MarketTestDatabase.Execute(Db, $"UPDATE market_ticket SET status = '{TicketStatus.Failed}', finished_Time = UTC_TIMESTAMP(6) WHERE id = {ticketId};");
        }

        private static void CompleteAlone(long ticketId)
        {
            using var context = MarketTestDatabase.CreateContext(Db);
            TicketStore.Complete(context, new TicketCompletion(ticketId, "done"), DateTime.UtcNow);
            context.SaveChanges();
        }

        private static void Backdate(long ticketId, double? finishedDaysAgo)
        {
            var finished = finishedDaysAgo == null ? "finished_Time" : $"UTC_TIMESTAMP(6) - INTERVAL {(long)(finishedDaysAgo.Value * 86400)} SECOND";
            MarketTestDatabase.Execute(Db, $"UPDATE market_ticket SET created_Time = UTC_TIMESTAMP(6) - INTERVAL 60 DAY, finished_Time = {finished} WHERE id = {ticketId};");
        }

        /// <summary>
        /// Status, result code, result message, and whether it has a finished time
        /// </summary>
        private static string Row(long ticketId) =>
            MarketTestDatabase.Rows(Db, $"SELECT status, result_Code, result_Message, finished_Time IS NOT NULL FROM market_ticket WHERE id = {ticketId};").Single();

        private static List<long> Ids() => MarketTestDatabase.Rows(Db, "SELECT id FROM market_ticket;").Select(long.Parse).ToList();

        private static long Count(string sql) => MarketTestDatabase.Scalar(Db, sql);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.MarketApi
{
    /// <summary>
    /// Purchase attempts per account in the last minute, in memory (the API is one process). An attempt the limit refuses isn't counted.
    /// </summary>
    public sealed class PurchaseLimiter
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        private const int PruneEvery = 1024;

        private readonly Dictionary<uint, Queue<DateTime>> attempts = new Dictionary<uint, Queue<DateTime>>();
        private int attemptsSincePrune;

        /// <summary>
        /// Counts an attempt, or returns false if the account already made perMinute attempts within the last minute
        /// </summary>
        public bool TryAcquire(uint accountId, DateTime now, long perMinute)
        {
            lock (attempts)
            {
                if (!attempts.TryGetValue(accountId, out var times))
                    attempts[accountId] = times = new Queue<DateTime>();

                while (times.Count > 0 && times.Peek() <= now - Window)
                    times.Dequeue();

                if (times.Count >= perMinute)
                    return false;

                times.Enqueue(now);

                if (++attemptsSincePrune >= PruneEvery)
                {
                    attemptsSincePrune = 0;
                    Prune(now);
                }

                return true;
            }
        }

        /// <summary>
        /// Forgets accounts with no attempt in the last minute
        /// </summary>
        private void Prune(DateTime now)
        {
            foreach (var accountId in attempts.Where(a => a.Value.Count == 0 || a.Value.Last() <= now - Window).Select(a => a.Key).ToList())
                attempts.Remove(accountId);
        }
    }
}

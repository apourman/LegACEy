using System;
using System.Collections.Generic;

namespace ACE.MarketApi
{
    /// <summary>
    /// Purchase attempts per account in the last minute, in memory (the API is one process). An attempt the limit refuses isn't counted.
    /// </summary>
    public sealed class PurchaseLimiter
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        private readonly Dictionary<uint, Queue<DateTime>> attempts = new Dictionary<uint, Queue<DateTime>>();

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
                return true;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Market;
using ACE.Database.Models.Shard;

namespace ACE.MarketApi
{
    /// <summary>
    /// The web sign-in lockout settings, read from the shard's server settings on each attempt so an admin's change applies at once
    /// </summary>
    public sealed class SignInLimits
    {
        public int AccountFailures { get; init; }
        public TimeSpan AccountWindow { get; init; }
        public TimeSpan AccountLock { get; init; }
        public int IpFailures { get; init; }
        public TimeSpan IpWindow { get; init; }
        public TimeSpan IpLock { get; init; }

        public static SignInLimits Read(ShardDbContext context)
        {
            return new SignInLimits
            {
                AccountFailures = (int)MarketSettings.Get(context, MarketSettings.SignInAccountFailures),
                AccountWindow = TimeSpan.FromMinutes(MarketSettings.Get(context, MarketSettings.SignInAccountWindowMinutes)),
                AccountLock = TimeSpan.FromMinutes(MarketSettings.Get(context, MarketSettings.SignInAccountLockMinutes)),
                IpFailures = (int)MarketSettings.Get(context, MarketSettings.SignInIpFailures),
                IpWindow = TimeSpan.FromMinutes(MarketSettings.Get(context, MarketSettings.SignInIpWindowMinutes)),
                IpLock = TimeSpan.FromMinutes(MarketSettings.Get(context, MarketSettings.SignInIpLockMinutes)),
            };
        }
    }

    /// <summary>
    /// Failed web sign-in counters, in memory (there is one API process).
    /// Enough failures within the window lock the account's web sign-in, or block the IP, for the lock time; a locked attempt isn't checked at all.
    /// </summary>
    public sealed class SignInLimiter
    {
        private sealed class Counter
        {
            public readonly Queue<DateTimeOffset> Failures = new Queue<DateTimeOffset>();
            public DateTimeOffset LockedUntil = DateTimeOffset.MinValue;
        }

        private const int PruneEvery = 1024;

        private readonly object gate = new object();
        private readonly Dictionary<string, Counter> accounts = new Dictionary<string, Counter>();
        private readonly Dictionary<string, Counter> ips = new Dictionary<string, Counter>();
        private int failuresSincePrune;

        public bool IsIpBlocked(string ip, DateTimeOffset now)
        {
            lock (gate)
                return ips.TryGetValue(ip, out var counter) && counter.LockedUntil > now;
        }

        public bool IsAccountLocked(string account, DateTimeOffset now)
        {
            lock (gate)
                return accounts.TryGetValue(account, out var counter) && counter.LockedUntil > now;
        }

        public void RecordFailure(string account, string ip, SignInLimits limits, DateTimeOffset now)
        {
            lock (gate)
            {
                Fail(accounts, account, limits.AccountFailures, limits.AccountWindow, limits.AccountLock, now);
                Fail(ips, ip, limits.IpFailures, limits.IpWindow, limits.IpLock, now);

                if (++failuresSincePrune >= PruneEvery)
                {
                    failuresSincePrune = 0;
                    Prune(accounts, limits.AccountWindow, now);
                    Prune(ips, limits.IpWindow, now);
                }
            }
        }

        /// <summary>
        /// A correct password clears the account's failures. The IP's failures stay: one address may be trying many accounts.
        /// </summary>
        public void RecordSuccess(string account)
        {
            lock (gate)
                accounts.Remove(account);
        }

        private static void Fail(Dictionary<string, Counter> counters, string key, int limit, TimeSpan window, TimeSpan lockTime, DateTimeOffset now)
        {
            if (!counters.TryGetValue(key, out var counter))
                counters[key] = counter = new Counter();

            while (counter.Failures.Count > 0 && counter.Failures.Peek() <= now - window)
                counter.Failures.Dequeue();

            counter.Failures.Enqueue(now);

            if (counter.Failures.Count >= limit)
            {
                counter.LockedUntil = now + lockTime;
                counter.Failures.Clear();
            }
        }

        private static void Prune(Dictionary<string, Counter> counters, TimeSpan window, DateTimeOffset now)
        {
            var stale = counters.Where(c => c.Value.LockedUntil <= now && (c.Value.Failures.Count == 0 || c.Value.Failures.Last() <= now - window)).Select(c => c.Key).ToList();

            foreach (var key in stale)
                counters.Remove(key);
        }
    }
}

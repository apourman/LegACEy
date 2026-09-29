using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Market;
using ACE.Database.Models.Shard;

namespace ACE.MarketApi
{
    /// <summary>
    /// Failures within the window lock the key (an account or an IP) for the lock time
    /// </summary>
    public sealed record LockoutRule(int Failures, TimeSpan Window, TimeSpan Lock)
    {
        public static LockoutRule Read(ShardDbContext context, MarketSetting failures, MarketSetting windowMinutes, MarketSetting lockMinutes)
        {
            // an admin's 0 or negative value means lock on the first failure, and never a negative time
            return new LockoutRule(
                (int)Math.Clamp(MarketSettings.Get(context, failures), 1, int.MaxValue),
                TimeSpan.FromMinutes(Math.Max(0, MarketSettings.Get(context, windowMinutes))),
                TimeSpan.FromMinutes(Math.Max(0, MarketSettings.Get(context, lockMinutes))));
        }
    }

    /// <summary>
    /// The web sign-in lockout settings, read from the shard's server settings on each attempt so an admin's change applies at once
    /// </summary>
    public sealed record SignInLimits(LockoutRule Account, LockoutRule Ip)
    {
        public static SignInLimits Read(ShardDbContext context)
        {
            return new SignInLimits(
                LockoutRule.Read(context, MarketSettings.SignInAccountFailures, MarketSettings.SignInAccountWindowMinutes, MarketSettings.SignInAccountLockMinutes),
                LockoutRule.Read(context, MarketSettings.SignInIpFailures, MarketSettings.SignInIpWindowMinutes, MarketSettings.SignInIpLockMinutes));
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
                Fail(accounts, account, limits.Account, now);
                Fail(ips, ip, limits.Ip, now);

                if (++failuresSincePrune >= PruneEvery)
                {
                    failuresSincePrune = 0;
                    Prune(accounts, limits.Account.Window, now);
                    Prune(ips, limits.Ip.Window, now);
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

        private static void Fail(Dictionary<string, Counter> counters, string key, LockoutRule rule, DateTimeOffset now)
        {
            if (!counters.TryGetValue(key, out var counter))
                counters[key] = counter = new Counter();

            while (counter.Failures.Count > 0 && counter.Failures.Peek() <= now - rule.Window)
                counter.Failures.Dequeue();

            counter.Failures.Enqueue(now);

            if (counter.Failures.Count >= rule.Failures)
            {
                counter.LockedUntil = now + rule.Lock;
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

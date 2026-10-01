using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Database.Entity;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;

namespace ACE.Database
{
    public class SerializedShardDatabase
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// This is the base database that SerializedShardDatabase is a wrapper for.
        /// </summary>
        public readonly ShardDatabase BaseDatabase;

        private readonly BlockingCollection<Task> _queue = new BlockingCollection<Task>();

        private Thread _workerThread;

        internal SerializedShardDatabase(ShardDatabase shardDatabase)
        {
            BaseDatabase = shardDatabase;
        }

        public void Start()
        {
            _workerThread = new Thread(DoWork);
            _workerThread.Name = "Serialized Shard Database";
            _workerThread.Start();
        }

        public void Stop()
        {
            _queue.CompleteAdding();
            _workerThread.Join();
        }

        private void DoWork()
        {
            while (!_queue.IsCompleted)
            {
                try
                {
                    Task t = _queue.Take();

                    try
                    {
                        t.Start();
                        t.Wait();
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[DATABASE] DoWork task failed with exception: {ex}");
                        // perhaps add failure callbacks?
                        // swallow for now.  can't block other db work because 1 fails.
                    }
                }
                catch (ObjectDisposedException)
                {
                    // the _queue has been disposed, we're good
                    break;
                }
                catch (InvalidOperationException)
                {
                    // _queue is empty and CompleteForAdding has been called -- we're done here
                    break;
                }
            }
        }


        public int QueueCount => _queue.Count;

        public void GetCurrentQueueWaitTime(Action<TimeSpan> callback)
        {
            var initialCallTime = DateTime.UtcNow;

            _queue.Add(new Task(() =>
            {
                callback?.Invoke(DateTime.UtcNow - initialCallTime);
            }));
        }


        /// <summary>
        /// Will return uint.MaxValue if no records were found within the range provided.
        /// </summary>
        public void GetMaxGuidFoundInRange(uint min, uint max, Action<uint> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.GetMaxGuidFoundInRange(min, max);
                callback?.Invoke(result);
            }));
        }

        /// <summary>
        /// This will return available id's, in the form of sequence gaps starting from min.<para />
        /// If a gap is just 1 value wide, then both start and end will be the same number.
        /// </summary>
        public void GetSequenceGaps(uint min, uint limitAvailableIDsReturned, Action<List<(uint start, uint end)>> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.GetSequenceGaps(min, limitAvailableIDsReturned);
                callback?.Invoke(result);
            }));
        }


        public void SaveBiota(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, Action<bool> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.SaveBiota(biota, rwLock);
                callback?.Invoke(result);
            }));
        }


        public void SaveBiotasInParallel(IEnumerable<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> biotas, Action<bool> callback, bool doNotAddToCache = false)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.SaveBiotasInParallel(biotas, doNotAddToCache);
                callback?.Invoke(result);
            }));
        }

        /// <summary>
        /// Queues the deposit job: the item change, the Vault row and a deposit event, saved once (see ShardDatabase.DepositToVault)
        /// </summary>
        public void DepositToVault(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, VaultItem vaultItem, Action<MarketJobResult> callback)
        {
            DepositToVault(biota, rwLock, vaultItem, int.MaxValue, callback);
        }

        /// <summary>
        /// Queues the deposit job, which also refuses when the account's Vault already holds maxItems (see ShardDatabase.DepositToVault)
        /// </summary>
        public void DepositToVault(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, VaultItem vaultItem, int maxItems, Action<MarketJobResult> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = RunMarketJob(nameof(DepositToVault), () => BaseDatabase.DepositToVault(biota, rwLock, vaultItem, maxItems));
                callback?.Invoke(result);
            }));
        }

        /// <summary>
        /// Queues the withdraw job: the item change, the Vault row removal and a withdraw event, saved once (see ShardDatabase.WithdrawFromVault).
        /// A game bridge ticket passed as ticket is marked done in the same save.
        /// </summary>
        public void WithdrawFromVault(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock, uint accountId, uint characterId, uint expectedRowVersion, Action<MarketJobResult> callback, TicketCompletion ticket = null)
        {
            _queue.Add(new Task(() =>
            {
                var result = RunMarketJob(nameof(WithdrawFromVault), () => BaseDatabase.WithdrawFromVault(biota, rwLock, accountId, characterId, expectedRowVersion, ticket));
                callback?.Invoke(result);
            }));
        }

        /// <summary>
        /// Queues the note deposit job: the note rows deleted and a note_deposit transfer, saved once (see ShardDatabase.DepositNotes).
        /// The callback gets the result and the balance after it.
        /// </summary>
        public void DepositNotes(uint accountId, uint characterId, IReadOnlyList<NoteStack> notes, Action<MarketJobResult, long> callback)
        {
            _queue.Add(new Task(() =>
            {
                long balanceAfter = 0;
                var result = RunMarketJob(nameof(DepositNotes), () => BaseDatabase.DepositNotes(accountId, characterId, notes, out balanceAfter));
                callback?.Invoke(result, balanceAfter);
            }));
        }

        /// <summary>
        /// Queues the note withdrawal job: a note_withdraw transfer and the new note rows, saved once (see ShardDatabase.WithdrawNotes).
        /// The callback gets the result and the balance after it (the current balance on a refusal). A game bridge ticket passed as ticket is marked done in the same save.
        /// </summary>
        public void WithdrawNotes(uint accountId, uint characterId, IReadOnlyList<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> notes, long amount, Action<MarketJobResult, long> callback, TicketCompletion ticket = null)
        {
            _queue.Add(new Task(() =>
            {
                long balanceAfter = 0;
                var result = RunMarketJob(nameof(WithdrawNotes), () => BaseDatabase.WithdrawNotes(accountId, characterId, notes, amount, out balanceAfter, ticket));
                callback?.Invoke(result, balanceAfter);
            }));
        }

        /// <summary>
        /// The market jobs catch their own failures. This is the backstop that keeps the caller's callback running if one ever throws:
        /// DoWork would swallow the exception and the callback would never run, leaving the player's Vault busy and the item out of the world.
        /// Whether such a job saved can't be known, so it answers Unknown and the caller puts nothing back into the world.
        /// </summary>
        private static MarketJobResult RunMarketJob(string job, Func<MarketJobResult> run)
        {
            try
            {
                return run();
            }
            catch (Exception ex)
            {
                log.Error($"[DATABASE][VAULT] {job} threw: {ex}");
                return MarketJobResult.Unknown;
            }
        }

        /// <summary>
        /// Queues a game bridge poll: claims up to limit waiting tickets (see ShardDatabase.ClaimTickets)
        /// </summary>
        public void ClaimTickets(int limit, Action<List<Ticket>> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.ClaimTickets(limit);
                callback?.Invoke(result);
            }));
        }

        /// <summary>
        /// Queues failing claimed tickets this server isn't working on any more (see ShardDatabase.FailAbandonedTickets)
        /// </summary>
        public void FailAbandonedTickets(IReadOnlyCollection<long> running, TimeSpan claimedFor, string message, Action<int> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.FailAbandonedTickets(running, claimedFor, message);
                callback?.Invoke(result);
            }));
        }

        /// <summary>
        /// Queues marking a claimed ticket FAILED (see ShardDatabase.FailTicket)
        /// </summary>
        public void FailTicket(long ticketId, string resultCode, string message, Action<bool> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.FailTicket(ticketId, resultCode, message);
                callback?.Invoke(result);
            }));
        }

        /// <summary>
        /// Queues deleting long-finished tickets (see ShardDatabase.DeleteFinishedTickets)
        /// </summary>
        public void DeleteFinishedTickets(Action<int> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.DeleteFinishedTickets();
                callback?.Invoke(result);
            }));
        }

        /// <summary>
        /// Queues deleting market rows nothing reads any more (see ShardDatabase.DeleteExpiredMarketRows)
        /// </summary>
        public void DeleteExpiredMarketRows(Action<MarketCleanupReport> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.DeleteExpiredMarketRows();
                callback?.Invoke(result);
            }));
        }

        public void RemoveBiota(uint id, Action<bool> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.RemoveBiota(id);
                callback?.Invoke(result);
            }));
        }

        public void RemoveBiota(uint id, Action<bool> callback, Action<TimeSpan, TimeSpan> performanceResults)
        {
            var initialCallTime = DateTime.UtcNow;

            _queue.Add(new Task(() =>
            {
                var taskStartTime = DateTime.UtcNow;
                var result = BaseDatabase.RemoveBiota(id);
                var taskCompletedTime = DateTime.UtcNow;
                callback?.Invoke(result);
                performanceResults?.Invoke(taskStartTime - initialCallTime, taskCompletedTime - taskStartTime);
            }));
        }

        public void RemoveBiotasInParallel(IEnumerable<uint> ids, Action<bool> callback, Action<TimeSpan, TimeSpan> performanceResults)
        {
            var initialCallTime = DateTime.UtcNow;

            _queue.Add(new Task(() =>
            {
                var taskStartTime = DateTime.UtcNow;
                var result = BaseDatabase.RemoveBiotasInParallel(ids);
                var taskCompletedTime = DateTime.UtcNow;
                callback?.Invoke(result);
                performanceResults?.Invoke(taskStartTime - initialCallTime, taskCompletedTime - taskStartTime);
            }));
        }


        public void GetPossessedBiotasInParallel(uint id, Action<PossessedBiotas> callback)
        {
            _queue.Add(new Task(() =>
            {
                var c = BaseDatabase.GetPossessedBiotasInParallel(id);
                callback?.Invoke(c);
            }));
        }

        public void GetInventoryInParallel(uint parentId, bool includedNestedItems, Action<List<Biota>> callback)
        {
            _queue.Add(new Task(() =>
            {
                var c = BaseDatabase.GetInventoryInParallel(parentId, includedNestedItems);
                callback?.Invoke(c);
            }));

        }


        public void IsCharacterNameAvailable(string name, Action<bool> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.IsCharacterNameAvailable(name);
                callback?.Invoke(result);
            }));
        }

        public void GetCharacters(uint accountId, bool includeDeleted, Action<List<Character>> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.GetCharacters(accountId, includeDeleted);
                callback?.Invoke(result);
            }));
        }

        public void GetCharacter(uint characterId, Action<Character> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.GetCharacter(characterId);
                callback?.Invoke(result);
            }));
        }

        public void SaveCharacter(Character character, ReaderWriterLockSlim rwLock, Action<bool> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.SaveCharacter(character, rwLock);
                callback?.Invoke(result);
            }));
        }

        public void RenameCharacter(Character character, string newName, ReaderWriterLockSlim rwLock, Action<bool> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.RenameCharacter(character, newName, rwLock);
                callback?.Invoke(result);
            }));
        }

        public void SetCharacterAccessLevelByName(string name, AccessLevel accessLevel, Action<uint> callback)
        {
            // TODO
            throw new NotImplementedException();
        }


        public void AddCharacterInParallel(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim biotaLock, IEnumerable<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)> possessions, Character character, ReaderWriterLockSlim characterLock, Action<bool> callback)
        {
            _queue.Add(new Task(() =>
            {
                var result = BaseDatabase.AddCharacterInParallel(biota, biotaLock, possessions, character, characterLock);
                callback?.Invoke(result);
            }));
        }
    }
}

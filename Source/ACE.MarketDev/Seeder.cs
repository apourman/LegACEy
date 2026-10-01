using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

using ACE.Common;
using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Market;
using ACE.Server.WorldObjects;

namespace ACE.MarketDev
{
    /// <summary>
    /// Fills the local market databases so the website can be built without a game client: two accounts with two characters each,
    /// real items made from world weenies and escrowed the way a deposit leaves them (Vault row with copied search columns, deposit event),
    /// balances through admin_adjust transfers (so the ledger audit passes), and some active listings.
    /// Runs only behind the development guard. Safe beside a running game server: its GUIDs come from reserved ranges far above what the game hands out.
    /// </summary>
    internal static class Seeder
    {
        /// <summary>
        /// Player GUIDs for seed characters (the game's player range is 0x50000001 and up, handed out from the highest in use)
        /// </summary>
        private const uint PlayerGuidBase = 0x5FF00000;

        /// <summary>
        /// Dynamic GUIDs for seed items (the game hands out dynamic GUIDs from 0x80000000 up, reusing gaps)
        /// </summary>
        private const uint ItemGuidBase = 0xE0000000;

        private const long StartingBalance = 1000;

        private sealed record SeedItem(uint Wcid, int StackSize = 1, long? Price = null, bool ListAsSecondCharacter = false);

        private sealed record SeedAccount(string Name, string[] Characters, SeedItem[] Items);

        private static readonly SeedAccount[] Accounts =
        {
            new SeedAccount("seedalpha", new[] { "Seed Alpha", "Seed Alpha Second" }, new[]
            {
                new SeedItem(27227, Price: 500),                            // Nariyid Breastplate: armor with a clothing table, so a dyed icon
                new SeedItem(35, Price: 40),                                // Chainmail Basinet
                new SeedItem(311, Price: 120, ListAsSecondCharacter: true), // Heavy Crossbow, listed by the second character
                new SeedItem(300, StackSize: 100, Price: 25),               // Arrows, a stack
                new SeedItem(91),                                           // Kite Shield, held
            }),
            new SeedAccount("seedbravo", new[] { "Seed Bravo", "Seed Bravo Second" }, new[]
            {
                new SeedItem(6599, Price: 900),                             // Amuli Shadow Coat
                new SeedItem(12463, Price: 60),                             // Atlatl
                new SeedItem(91),                                           // Kite Shield, held
            }),
        };

        // outside Holtburg, where a new character would start
        private static readonly Position StartLocation = new Position(0xA9B4001F, 84.0f, 7.1f, 94.0f, 0.0f, 0.0f, 0.0f, 1.0f);

        public static int Seed(string password)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // as Program.Main does, for the DAT strings

            DatManager.Initialize(ConfigManager.Config.Server.DatFilesDirectory, true);
            DatabaseManager.Initialize();

            if (DatabaseManager.InitializationFailure)
            {
                Console.Error.WriteLine("The game databases failed to initialize; see the log above.");
                return 1;
            }

            PropertyManager.Initialize();

            using (var shard = new ShardDbContext())
            {
                var schema = MarketSchema.Check(shard);

                if (schema.Status != MarketSchemaStatus.Ok)
                {
                    Console.Error.WriteLine($"Market schema: {schema.Report}. Start the game server once (scripts/market/game.sh) so it applies the market update script, then seed again.");
                    return 1;
                }
            }

            var existing = Accounts.Where(a => DatabaseManager.Authentication.GetAccountByName(a.Name) != null).Select(a => a.Name).ToList();
            if (existing.Count > 0)
            {
                // the last item of the last account is written last (its listing, then the audit only reads): if it's there, a run finished
                if (existing.Count == Accounts.Length && IsComplete())
                {
                    Console.WriteLine($"Already seeded ({string.Join(", ", existing)} exist). Nothing written.");
                    return 0;
                }

                Console.Error.WriteLine($"Partly seeded ({string.Join(", ", existing)} exist, but an earlier run stopped before the end). Nothing written. " +
                    "Start over: drop the market databases and run scripts/market/bootstrap.sh (scripts/market/README.md).");
                return 1;
            }

            // check what can be checked before the first write, so a bad weenie doesn't leave a half-seeded database
            var missing = Accounts.SelectMany(a => a.Items).Select(i => i.Wcid).Distinct().Where(wcid => DatabaseManager.World.GetCachedWeenie(wcid) == null).ToList();
            if (DatabaseManager.World.GetCachedWeenie("human") == null)
                missing.Add(1);
            if (missing.Count > 0)
            {
                Console.Error.WriteLine($"The world database lacks weenies {string.Join(", ", missing)}. Nothing written.");
                return 1;
            }

            var now = DateTime.UtcNow;
            var nextPlayerGuid = NextFree(PlayerGuidBase, ObjectGuid.PlayerMax);
            var nextItemGuid = NextFree(ItemGuidBase, ObjectGuid.DynamicMax);

            foreach (var seed in Accounts)
            {
                var account = DatabaseManager.Authentication.CreateAccount(seed.Name, password, AccessLevel.Player, IPAddress.Loopback);
                var characters = seed.Characters.Select(name => CreateCharacter(account.AccountId, name, nextPlayerGuid++)).ToList();

                Console.WriteLine($"Account {seed.Name} (id {account.AccountId}): characters {string.Join(", ", characters.Select(c => $"{c.Name} (0x{c.Guid.Full:X8})"))}");

                var deposited = new List<(SeedItem Seed, uint Guid)>();

                foreach (var seedItem in seed.Items)
                {
                    var guid = nextItemGuid++;
                    Deposit(seedItem, guid, account.AccountId, characters[0].Guid.Full);
                    deposited.Add((seedItem, guid));
                }

                var balance = LedgerCorrections.Adjust(() => new ShardDbContext(), account.AccountId, StartingBalance, "seed: development balance", account.AccountId, null, now);
                if (balance.Outcome != CorrectionOutcome.Done)
                    throw new InvalidOperationException($"admin_adjust for {seed.Name}: {balance.Outcome}");

                Console.WriteLine($"  balance {balance.Balance} MMD (admin_adjust transfer {balance.TransferId})");

                using var shard = new ShardDbContext();

                foreach (var (seedItem, guid) in deposited)
                {
                    if (seedItem.Price is not long price)
                    {
                        Console.WriteLine($"  0x{guid:X8} wcid {seedItem.Wcid}: held");
                        continue;
                    }

                    var seller = seedItem.ListAsSecondCharacter ? characters[1] : characters[0];
                    var listed = ListingStore.List(shard, account.AccountId, guid, price, seller.Guid.Full, now, () => false);

                    if (listed.Outcome != ListingOutcome.Ok)
                        throw new InvalidOperationException($"listing 0x{guid:X8}: {listed.Outcome}");

                    Console.WriteLine($"  0x{guid:X8} wcid {seedItem.Wcid}: listed {listed.Listing.Id} at {price} MMD by {seller.Name}");
                }
            }

            using (var shard = new ShardDbContext())
            {
                var audit = LedgerAudit.Run(shard);

                Console.WriteLine(audit.Summary);

                foreach (var failure in audit.Failures)
                    Console.WriteLine($"  {failure.Check}: {failure.Detail}");

                if (!audit.Passed)
                    return 1;
            }

            Console.WriteLine($"Seeded. Sign in as {string.Join(" or ", Accounts.Select(a => a.Name))} with the seed password.");
            return 0;
        }

        /// <summary>
        /// True when every seed account has its full Vault and every listing a run makes: the run that made them finished
        /// </summary>
        private static bool IsComplete()
        {
            using var shard = new ShardDbContext();

            foreach (var seed in Accounts)
            {
                var accountId = DatabaseManager.Authentication.GetAccountByName(seed.Name).AccountId;

                if (shard.MarketVaultItems.Count(v => v.AccountId == accountId) < seed.Items.Length)
                    return false;

                if (shard.MarketListings.Count(l => l.SellerAccountId == accountId) < seed.Items.Count(i => i.Price != null))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The first GUID above every biota already in the range, so seeding twice into a reset auth database never reuses an item's GUID
        /// </summary>
        private static uint NextFree(uint start, uint end)
        {
            var max = DatabaseManager.Shard.BaseDatabase.GetMaxGuidFoundInRange(start, end);

            return max == uint.MaxValue || max < start ? start : max + 1;
        }

        /// <summary>
        /// A character the game can list and the API can act as: the human weenie with a name and a start location, saved as character creation saves it
        /// </summary>
        private static Player CreateCharacter(uint accountId, string name, uint guid)
        {
            var player = new Player(DatabaseManager.World.GetCachedWeenie("human"), new ObjectGuid(guid), accountId);

            player.Name = name;
            player.Character.Name = name;
            player.Location = new Position(StartLocation);

            if (!DatabaseManager.Shard.BaseDatabase.AddCharacterInParallel(player.Biota, player.BiotaDatabaseLock, Array.Empty<(ACE.Entity.Models.Biota, ReaderWriterLockSlim)>(), player.Character, player.CharacterDatabaseLock))
                throw new InvalidOperationException($"saving character {name} failed");

            return player;
        }

        /// <summary>
        /// A new item from the weenie, saved as a new item is, then escrowed through the deposit job, as /vault deposit does:
        /// the item row (no container, owner or location), its Vault row and the deposit event in one save
        /// </summary>
        private static void Deposit(SeedItem seedItem, uint guid, uint accountId, uint characterId)
        {
            var item = WorldObjectFactory.CreateWorldObject(DatabaseManager.World.GetCachedWeenie(seedItem.Wcid), new ObjectGuid(guid));

            if (seedItem.StackSize > 1)
                item.SetStackSize(seedItem.StackSize);

            if (!DatabaseManager.Shard.BaseDatabase.SaveBiota(item.Biota, item.BiotaDatabaseLock))
                throw new InvalidOperationException($"saving {item.Name} (wcid {seedItem.Wcid}) failed");

            var result = DatabaseManager.Shard.BaseDatabase.DepositToVault(item.Biota, item.BiotaDatabaseLock, Vault.NewVaultItem(item, accountId, characterId));

            if (result != MarketJobResult.Saved)
                throw new InvalidOperationException($"depositing {item.Name} (wcid {seedItem.Wcid}): {result}");
        }
    }
}

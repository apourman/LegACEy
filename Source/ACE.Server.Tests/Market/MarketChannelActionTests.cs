using System;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClientChannel;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Market;
using ACE.Server.Tests.ClientChannel;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ClientChannel.ChannelTestClient;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// The market window's account action, and what ends the market station's session: a landed attack or damage from someone else.
    /// Sent through the channel test client, in the Vault's test world.
    /// </summary>
    public partial class VaultTests
    {
        private const string MarketStation = MarketChannelActions.Station;

        [TestMethod]
        public void MarketAccount_NeedsTheMarketStation()
        {
            var player = ConnectedPlayer();

            var refused = Request(player, MarketChannelActions.Account, Array.Empty<byte>());
            Assert.AreEqual(ChannelStatus.NoStation, refused.Status);
            Assert.AreEqual("Use the market station to do that.", Text(refused.Body));

            Use(player, NewStation(player, VaultStation));
            WaitForPushes(player, 1);
            Assert.AreEqual(ChannelStatus.NoStation, Request(player, MarketChannelActions.Account, Array.Empty<byte>()).Status, "the Vault's session isn't the market's");
        }

        [TestMethod]
        public void MarketAccount_AtTheStation_RepliesWithTheAccountSummary()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            // a balance of 25 MMD, one item in the Vault, and one of them listed
            GiveNotes(player, 25);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            var guid = DepositedNamed(player, 1, n => "Listed sword")[0];
            using (var shard = new ShardDbContext())
                Assert.AreEqual(ListingOutcome.Ok, ListingStore.List(shard, player.Character.AccountId, guid, 25, null, DateTime.UtcNow, () => false).Outcome);

            var account = ReadAccount(player);

            Assert.AreEqual(25L, account.Balance, "balance");
            Assert.IsFalse(account.Frozen, "not frozen");
            Assert.IsFalse(account.Paused, "not paused");
            Assert.AreEqual(1, account.VaultCount, "Vault count");
            Assert.AreEqual((int)MarketSettings.Get(MarketSettings.VaultSize), account.VaultCapacity, "Vault capacity");
            Assert.AreEqual(1, account.ListingCount, "active listings");
            Assert.AreEqual((int)MarketSettings.Get(MarketSettings.ActiveListings), account.ListingCap, "listing cap");
            Assert.AreEqual(player.Name, account.Character, "the character the window acts for");
        }

        [TestMethod]
        public void MarketAccount_WithAPausedMarket_SaysSo()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            using (var shard = new ShardDbContext())
                MarketPause.Pause(shard, "a test", DateTime.UtcNow);
            try
            {
                Assert.IsTrue(ReadAccount(player).Paused, "paused");
            }
            finally
            {
                using (var shard = new ShardDbContext())
                    MarketPause.Resume(shard, "a test", DateTime.UtcNow);
            }
        }

        [TestMethod]
        public void MarketAccount_WhileMarketEnabledIsOff_IsRefusedWithThePlainMessage()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            PropertyManager.ModifyBool(Vault.MarketEnabledKey, false);
            ChannelEvent refused;
            try
            {
                refused = Request(player, MarketChannelActions.Account, Array.Empty<byte>());
            }
            finally
            {
                PropertyManager.ModifyBool(Vault.MarketEnabledKey, true);
            }

            Assert.AreEqual(ChannelStatus.Unavailable, refused.Status);
            Assert.AreEqual(VaultMessages.For(VaultOutcome.MarketClosed, null), Text(refused.Body));
        }

        [TestMethod]
        [DataRow("player")]
        [DataRow("monster")]
        public void LandedAttack_EndsTheMarketSession_AndPushesStationClose(string attacker)
        {
            var player = ConnectedPlayer();
            var station = NewStation(player, MarketStation);
            Use(player, station);
            WaitForPushes(player, 1);

            var source = attacker == "player" ? NewPk() : (WorldObject)WorldObjectFactory.CreateNewWorldObject(DrudgeSkulkerWcid);
            source.Location = new ACE.Entity.Position(player.Location);
            VaultTestWorld.OnWorldThread(() => player.TakeDamage(source, DamageType.Slash, 1, BodyPart.Chest));

            AssertMarketClosed(player, station);
        }

        [TestMethod]
        public void LandedSpellProjectile_EndsTheMarketSession_AndPushesStationClose()
        {
            var player = ConnectedPlayer();
            var station = NewStation(player, MarketStation);
            Use(player, station);
            WaitForPushes(player, 1);

            var spellId = DamagingBoltSpell();
            var spell = new Spell(spellId);
            var projectile = (SpellProjectile)WorldObjectFactory.CreateNewWorldObject(spell.Wcid);
            projectile.Setup(spell, SpellProjectile.GetProjectileSpellType(spellId));
            var source = (Creature)WorldObjectFactory.CreateNewWorldObject(DrudgeSkulkerWcid);
            source.Location = new ACE.Entity.Position(player.Location);
            projectile.ProjectileSource = source;
            VaultTestWorld.OnWorldThread(() => projectile.DamageTarget(player, 5, false, false, false));

            AssertMarketClosed(player, station);
        }

        [TestMethod]
        public void DamageOverTimeTick_EndsTheMarketSession_AndPushesStationClose()
        {
            var player = ConnectedPlayer();
            var station = NewStation(player, MarketStation);
            Use(player, station);
            WaitForPushes(player, 1);

            VaultTestWorld.OnWorldThread(() => player.TakeDamageOverTime(3, DamageType.Fire));

            AssertMarketClosed(player, station);
        }

        [TestMethod]
        public void Fall_EndsTheMarketSession_AndPushesStationClose()
        {
            var player = ConnectedPlayer();
            var station = NewStation(player, MarketStation);
            Use(player, station);
            WaitForPushes(player, 1);

            VaultTestWorld.OnWorldThread(() => player.TakeDamage_Falling(3));

            AssertMarketClosed(player, station);
        }

        [TestMethod]
        public void LandedAttack_LeavesAVaultSessionOpen()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, VaultStation));
            WaitForPushes(player, 1);

            VaultTestWorld.OnWorldThread(() => player.TakeDamage(NewPk(), DamageType.Slash, 1, BodyPart.Chest));

            Assert.IsTrue(player.HasStation(VaultStation), "only the market session ends on damage");
        }

        /// <summary>
        /// The market station's close is pushed, and the session is over. A hit also pushes combat text, so the close is waited for by its topic.
        /// </summary>
        private static void AssertMarketClosed(Player player, WorldObject station)
        {
            AssertStationPush(WaitForEvent(player, e => e.Kind == ChannelEventKind.Push && e.Topic == StationChannelActions.Close),
                StationChannelActions.Close, MarketStation, station);
            Assert.IsFalse(player.HasStation(MarketStation));
        }

        /// <summary>
        /// The account summary's reply, read in the order the action writes it
        /// </summary>
        private static (long Balance, bool Frozen, bool Paused, int VaultCount, int VaultCapacity, int ListingCount, int ListingCap, string Character) ReadAccount(Player player)
        {
            var reply = Request(player, MarketChannelActions.Account, Array.Empty<byte>());
            Assert.AreEqual(ChannelStatus.Ok, reply.Status, Text(reply.Body));

            var body = new BinaryReader(new MemoryStream(reply.Body), Encoding.UTF8);
            return (body.ReadInt64(), body.ReadByte() != 0, body.ReadByte() != 0, body.ReadInt32(), body.ReadInt32(), body.ReadInt32(), body.ReadInt32(),
                ChannelWire.ReadString(body));
        }

        /// <summary>
        /// A damaging war magic bolt: a spell with a projectile weenie, one projectile
        /// </summary>
        private static uint DamagingBoltSpell() => DatManager.PortalDat.SpellTable.Spells.Keys
            .First(id => new Spell(id) is { Wcid: not 0 } spell && spell.NumProjectiles == 1 && spell.School == MagicSchool.WarMagic);
    }
}

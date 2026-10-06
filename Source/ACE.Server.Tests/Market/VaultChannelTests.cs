using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Market;
using ACE.Server.Network;
using ACE.Server.Network.GameAction.Actions;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Seam 2: the channel that every /vault item deposit and withdrawal goes through, driven with test players in the started world
    /// </summary>
    public partial class VaultTests
    {
        private const uint DrudgeSkulkerWcid = 7;
        private const uint HarmOtherI = 7;      // Life Magic, damages health
        private const uint WeaknessOtherI = 3;  // Creature Enchantment, a debuff that does no damage
        private const uint PyrealWcid = 273;

        // ---- the channel completes

        [TestMethod]
        public void Channel_Deposit_FreezesWithStartMessage_ThenItemIsInVault()
        {
            var (player, item) = NewItem(null);
            var guid = item.Guid.Full;

            using (ChannelSeconds(1))
            {
                VaultTestWorld.TakeSent(player);
                var run = StartDeposit(player, guid);

                Assert.IsNull(run.Result, "the deposit waits for the channel");
                Assert.IsTrue(player.IsVaultChannelling);
                Assert.IsTrue(player.IsFrozen ?? false, "the player is frozen");
                Assert.IsFalse(player.PKLogout, "the channel is not the PK logout state");
                Assert.IsFalse(player.IsLoggingOut);
                Assert.IsTrue(VaultTestWorld.Chats(VaultTestWorld.TakeSent(player)).Any(c => c.Contains(item.Name) && c.Contains("1 second")), "the player is told the channel started, and for how long");
                Assert.IsNotNull(player.GetInventoryItem(guid), "the item stays in the pack while channelling");

                var result = run.Wait();

                Assert.AreEqual(VaultOutcome.Deposited, result.Outcome, result.Message);
            }

            Assert.IsFalse(player.IsVaultChannelling);
            Assert.IsFalse(player.IsFrozen ?? false, "the player is unfrozen");
            Assert.IsNull(player.GetInventoryItem(guid));
            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(guid).State);
        }

        [TestMethod]
        public void Channel_Withdraw_MarksRowWithdrawing_ThenItemIsInPack()
        {
            var (player, guid) = DepositedItem();
            var versionBefore = VaultStore.Get(guid).RowVersion;

            using (ChannelSeconds(1))
            {
                var run = StartWithdraw(player, guid);

                var marked = VaultStore.Get(guid);
                Assert.AreEqual(VaultItemState.Withdrawing, marked.State, "the row is marked when the channel starts");
                Assert.AreEqual(versionBefore + 1, marked.RowVersion);
                Assert.IsTrue(player.IsVaultChannelling);
                Assert.IsNull(player.GetInventoryItem(guid));

                var result = run.Wait();

                Assert.AreEqual(VaultOutcome.Withdrawn, result.Outcome, result.Message);
            }

            Assert.IsNotNull(player.GetInventoryItem(guid));
            Assert.IsNull(VaultStore.Get(guid), "the Vault row is gone");
            Assert.IsFalse(player.IsFrozen ?? false);
        }

        [TestMethod]
        public void Channel_WithdrawRefusedWhenTimerFires_RowIsBackToHeld()
        {
            var (player, guid) = DepositedItem();

            using (ChannelSeconds(1))
            {
                var run = StartWithdraw(player, guid);
                Assert.AreEqual(VaultItemState.Withdrawing, VaultStore.Get(guid).State);

                // the pack fills up while the player channels
                VaultTestWorld.OnWorldThread(() =>
                {
                    while (player.TryAddToInventory(VaultTestWorld.NewItem(VaultTestWorld.SwordWcid), out _)) { }
                });

                var result = run.Wait();

                Assert.AreEqual(VaultOutcome.NoPackSpace, result.Outcome, result.Message);
            }

            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(guid).State, "a withdrawal that didn't happen leaves the item held");
            Assert.IsFalse(player.IsVaultChannelling);
        }

        [TestMethod]
        public void Channel_DepositRefusal_IsReportedAtOnceWithoutChannelling()
        {
            var (player, item) = NewItem(i => i.Attuned = AttunedStatus.Attuned);

            var result = StartDeposit(player, item.Guid.Full).Result;

            Assert.IsNotNull(result, "the rules are checked before the channel starts");
            Assert.AreEqual(VaultOutcome.Attuned, result.Outcome, result.Message);
            Assert.IsFalse(player.IsVaultChannelling);
            Assert.IsFalse(player.IsFrozen ?? false);
        }

        // ---- when the channel can't start

        [TestMethod]
        public void Channel_WithinTwoMinutesOfPlayerFight_IsRefused()
        {
            var (player, item) = NewItem(null);
            var attacker = NewPk();
            player.PlayerKillerStatus = PlayerKillerStatus.PK;

            Player.UpdatePKTimers(attacker, player);

            var result = StartDeposit(player, item.Guid.Full).Result;

            Assert.IsNotNull(result);
            Assert.AreEqual(VaultOutcome.RecentPlayerFight, result.Outcome, result.Message);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Message));
            Assert.IsFalse(player.IsVaultChannelling);
            Assert.IsFalse(player.IsFrozen ?? false);
            Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full));
        }

        [TestMethod]
        public void Channel_WhileTradingBusyOrAlreadyChannelling_IsRefused()
        {
            var (player, item) = NewItem(null);
            var second = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));

            SetTrading(player, true);
            Assert.AreEqual(VaultOutcome.Trading, StartDeposit(player, item.Guid.Full).Result?.Outcome);
            SetTrading(player, false);

            player.IsBusy = true;
            Assert.AreEqual(VaultOutcome.Busy, StartDeposit(player, item.Guid.Full).Result?.Outcome);
            player.IsBusy = false;
            Assert.IsFalse(player.IsVaultChannelling);

            using (ChannelSeconds(30))
            {
                var first = StartDeposit(player, item.Guid.Full);
                Assert.IsTrue(player.IsVaultChannelling);

                Assert.AreEqual(VaultOutcome.Channelling, StartDeposit(player, second.Guid.Full).Result?.Outcome);
                Assert.AreEqual(VaultOutcome.Channelling, StartWithdraw(player, 0x80000001).Result?.Outcome);

                VaultTestWorld.OnWorldThread(() => VaultChannel.Cancel(player));
                Assert.AreEqual(VaultOutcome.Interrupted, first.Wait().Outcome);
            }
        }

        // ---- what cancels it

        [TestMethod]
        public void Channel_LandedPlayerMeleeHit_Cancels()
        {
            var (player, item, run) = ChannellingPk();
            var attacker = NewPk();

            VaultTestWorld.OnWorldThread(() => player.TakeDamage(attacker, DamageType.Slash, 1, BodyPart.Chest));

            AssertCancelledAndNothingMoved(player, item, run);
        }

        [TestMethod]
        public void Channel_LandedHarmfulSpell_Cancels()
        {
            var (player, item, run) = ChannellingPk();
            var caster = NewCaster(Skill.LifeMagic);
            var healthBefore = player.Health.Current;

            VaultTestWorld.OnWorldThread(() => CastOn(caster, player, HarmOtherI));

            Assert.IsTrue(player.Health.Current < healthBefore, "the spell landed");
            AssertCancelledAndNothingMoved(player, item, run);
        }

        [TestMethod]
        public void Channel_LandedDebuffWithoutDamage_Cancels()
        {
            var (player, item, run) = ChannellingPk();
            var caster = NewCaster(Skill.CreatureEnchantment);
            var healthBefore = player.Health.Current;

            VaultTestWorld.OnWorldThread(() => CastOn(caster, player, WeaknessOtherI));

            Assert.IsTrue(player.EnchantmentManager.HasSpell(WeaknessOtherI), "the debuff landed");
            Assert.AreEqual(healthBefore, player.Health.Current, "the debuff did no damage");
            AssertCancelledAndNothingMoved(player, item, run);
        }

        [TestMethod]
        public void Channel_MonsterDamage_DoesNotCancel()
        {
            var (player, item) = NewItem(null);
            player.PlayerKillerStatus = PlayerKillerStatus.PK;
            var monster = (Creature)WorldObjectFactory.CreateNewWorldObject(DrudgeSkulkerWcid);
            monster.Location = new ACE.Entity.Position(player.Location);
            var healthBefore = player.Health.Current;

            using (ChannelSeconds(2))
            {
                var run = StartDeposit(player, item.Guid.Full);

                VaultTestWorld.OnWorldThread(() => player.TakeDamage(monster, DamageType.Slash, 1, BodyPart.Chest));

                Assert.IsTrue(player.Health.Current < healthBefore, "the monster hit landed");
                Assert.IsTrue(player.IsVaultChannelling, "monster damage doesn't cancel the channel");

                var result = run.Wait();
                Assert.AreEqual(VaultOutcome.Deposited, result.Outcome, result.Message);
            }
        }

        [TestMethod]
        public void Channel_Death_CancelsAndItemDropsOnCorpseUnderNormalRules()
        {
            var (player, item, run) = ChannellingPk(wcid: VaultTestWorld.HelmWcid);
            var killer = NewPk();
            player.Level = 50;

            VaultTestWorld.OnWorldThread(() =>
            {
                var die = typeof(Player).GetMethod("Die", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(DamageHistoryInfo), typeof(DamageHistoryInfo) }, null);
                die.Invoke(player, new object[] { new DamageHistoryInfo(killer), new DamageHistoryInfo(killer) });
            });

            Assert.IsTrue(player.IsInDeathProcess);
            AssertCancelledAndNothingMoved(player, item, run);

            // the corpse step of the death: the item was never taken out, so the normal death item rules apply to it
            var corpse = (Corpse)WorldObjectFactory.CreateNewWorldObject("corpse");
            List<WorldObject> dropped = null;
            VaultTestWorld.OnWorldThread(() => dropped = player.CalculateDeathItems(corpse));

            Assert.IsTrue(dropped.Any(d => d.Guid == item.Guid), "the item drops on the corpse");
            Assert.IsNull(player.GetInventoryItem(item.Guid.Full));
            Assert.IsNull(VaultStore.Get(item.Guid.Full));
        }

        [TestMethod]
        public void Channel_Logout_CancelsAndNothingMoves()
        {
            var (player, item) = NewItem(null);

            SetChannelSeconds(2);
            var run = StartDeposit(player, item.Guid.Full);
            Assert.IsTrue(player.IsVaultChannelling);

            VaultTestWorld.OnWorldThread(() => player.LogOut());

            Assert.IsTrue(player.IsLoggingOut);
            AssertCancelledAndNothingMoved(player, item, run);
        }

        [TestMethod]
        public void Channel_WithdrawCancelled_RowIsBackToHeldAndItemStaysInVault()
        {
            var (player, guid) = DepositedItem();
            player.PlayerKillerStatus = PlayerKillerStatus.PK;
            var version = VaultStore.Get(guid).RowVersion;

            using (ChannelSeconds(2))
            {
                var run = StartWithdraw(player, guid);
                Assert.AreEqual(VaultItemState.Withdrawing, VaultStore.Get(guid).State);

                VaultTestWorld.OnWorldThread(() => player.TakeDamage(NewPk(), DamageType.Slash, 1, BodyPart.Chest));
                VaultTestWorld.OnWorldThread(() => { }); // a cancel reports on the world thread's next turn

                Assert.AreEqual(VaultOutcome.Interrupted, run.Result?.Outcome);
                var row = VaultStore.Get(guid);
                Assert.AreEqual(VaultItemState.Held, row.State);
                Assert.AreEqual(version + 2, row.RowVersion, "marking and releasing each changed the row version");

                Thread.Sleep(2500);
                VaultTestWorld.OnWorldThread(() => { });
            }

            Assert.IsNull(player.GetInventoryItem(guid), "the timer did nothing once the channel was cancelled");
            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(guid).State);
        }

        // ---- what it blocks

        [TestMethod]
        public void Channel_BlocksAttackCastUseMoveTradeGiveDropRecall()
        {
            var (player, item) = NewItem(null);
            var other = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var session = player.Session;
            var coins = VaultTestWorld.NewItem(PyrealWcid);
            coins.SetStackSize(10);
            VaultTestWorld.Give(player, coins);

            using (ChannelSeconds(30))
            {
                var run = StartDeposit(player, item.Guid.Full);
                Assert.IsTrue(player.IsVaultChannelling);

                void AssertBlocked(string what, Action action)
                {
                    VaultTestWorld.TakeSent(player);
                    VaultTestWorld.OnWorldThread(action);
                    Assert.IsTrue(VaultTestWorld.HasError(VaultTestWorld.TakeSent(player), WeenieError.YoureTooBusy), $"{what} is refused while channelling");
                }

                SetLastCombatMode(player, CombatMode.Melee);
                AssertBlocked("melee", () => player.HandleActionTargetedMeleeAttack(other.Guid.Full, (uint)AttackHeight.Medium, 0.5f));
                Assert.IsNull(player.AttackHeight, "no melee attack was queued");

                SetLastCombatMode(player, CombatMode.Missile);
                AssertBlocked("missile", () => player.HandleActionTargetedMissileAttack(other.Guid.Full, (uint)AttackHeight.Medium, 0.5f));

                SetLastCombatMode(player, CombatMode.Magic);
                AssertBlocked("targeted casting", () => player.HandleActionCastTargetedSpell(other.Guid.Full, WeaknessOtherI));
                AssertBlocked("untargeted casting", () => player.HandleActionMagicCastUnTargetedSpell(WeaknessOtherI));
                SetLastCombatMode(player, CombatMode.NonCombat);

                AssertBlocked("using an item", () => player.HandleActionUseItem(item.Guid.Full));
                AssertBlocked("using an item on a target", () => player.HandleActionUseWithTarget(item.Guid.Full, other.Guid.Full));

                AssertBlocked("opening a trade", () => player.HandleActionOpenTradeNegotiations(other.Guid.Full, true));
                Assert.IsFalse(player.IsTrading);
                AssertBlocked("adding to a trade", () => player.HandleActionAddToTrade(item.Guid.Full, 0));
                Assert.AreEqual(0, player.ItemsInTradeWindow.Count);

                AssertBlocked("giving", () => player.HandleActionGiveObjectRequest(other.Guid.Full, item.Guid.Full, 1));
                AssertBlocked("dropping", () => player.HandleActionDropItem(item.Guid.Full));
                AssertBlocked("splitting a stack onto the ground", () => player.HandleActionStackableSplitTo3D(coins.Guid.Full, 1));
                Assert.AreEqual(10, coins.StackSize, "no coins were dropped");
                Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full), "the item wasn't given or dropped");

                AssertBlocked("the lifestone recall", () => player.HandleActionTeleToLifestone());
                AssertBlocked("the marketplace recall", () => player.HandleActionTeleToMarketPlace());
                AssertBlocked("the house recall", () => player.HandleActionTeleToHouse());
                Assert.IsFalse(player.IsBusy, "no recall started");

                // movement messages are dropped before they are read, so a message holding only its opcode is enough
                var moveToStateBefore = player.CurrentMoveToState;
                VaultTestWorld.OnWorldThread(() => GameActionMoveToState.Handle(new ClientMessage(BitConverter.GetBytes(0xF61Cu)), session));
                VaultTestWorld.OnWorldThread(() => GameActionJump.Handle(new ClientMessage(BitConverter.GetBytes(0xF61Bu)), session));
                Assert.AreSame(moveToStateBefore, player.CurrentMoveToState, "the movement was ignored");

                Assert.IsTrue(player.IsVaultChannelling, "none of it cancelled the channel");

                VaultTestWorld.OnWorldThread(() => VaultChannel.Cancel(player));
                Assert.AreEqual(VaultOutcome.Interrupted, run.Wait().Outcome);
            }
        }

        [TestMethod]
        public void Channel_AttacksOnChannellingPlayer_AreNotAutomaticallyCritical()
        {
            var (player, item, run) = ChannellingPk(30);
            var attacker = NewPk();
            var sword = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            Assert.IsTrue(attacker.TryEquipObject(sword, EquipMask.MeleeWeapon));
            SetLastCombatMode(attacker, CombatMode.Melee);

            var hit = LandedHit(attacker, player);
            Assert.IsTrue(hit.CriticalChance < 1.0f, $"critical chance is {hit.CriticalChance}");

            // the PK logout state would make it certain: the reason the channel is its own state
            player.PKLogout = true;
            Assert.AreEqual(1.0f, LandedHit(attacker, player).CriticalChance);
            player.PKLogout = false;

            VaultTestWorld.OnWorldThread(() => VaultChannel.Cancel(player));
            Assert.AreEqual(VaultOutcome.Interrupted, run.Wait().Outcome);
            Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full));
            ClearChannelSeconds();
        }

        [TestMethod]
        public void Channel_End_LeavesAFreezeItDidNotSet()
        {
            var (player, item) = NewItem(null);
            player.IsFrozen = true; // frozen by something else before the channel

            using (ChannelSeconds(1))
            {
                var run = StartDeposit(player, item.Guid.Full);
                Assert.AreEqual(VaultOutcome.Deposited, run.Wait().Outcome);
            }

            Assert.IsTrue(player.IsFrozen ?? false, "the channel only lifts its own freeze");
        }

        [TestMethod]
        public void Channel_FreezeSavedByACrash_IsGoneAtNextLogin()
        {
            var (player, item) = NewItem(null);

            using (ChannelSeconds(30))
            {
                var run = StartDeposit(player, item.Guid.Full);
                Assert.IsTrue(player.GetProperty(PropertyBool.IsFrozen) ?? false, "the freeze is a saved property");

                // the server stops mid-channel after a save: the next login loads the saved biota
                var loaded = new Player(player.Biota, new List<ACE.Database.Models.Shard.Biota>(), new List<ACE.Database.Models.Shard.Biota>(), player.Character, null);

                Assert.IsFalse(loaded.IsFrozen ?? false, "no channel survives a restart, so neither does its freeze");

                VaultTestWorld.OnWorldThread(() => VaultChannel.Cancel(player));
                Assert.AreEqual(VaultOutcome.Interrupted, run.Wait().Outcome);
            }
        }

        [TestMethod]
        public void Vault_Initialize_ReleasesWithdrawalsLeftByARestart()
        {
            var (player, guid) = DepositedItem();
            MarketTestDatabase.Execute(Db, $"UPDATE market_vault_item SET state = '{VaultItemState.Withdrawing}', row_Version = row_Version + 1 WHERE item_Guid = {guid};");

            Vault.Initialize();

            Assert.IsTrue(Vault.Available);
            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(guid).State, "no channel survives a restart, so the item can be withdrawn again");
            Assert.AreEqual(VaultOutcome.Withdrawn, VaultTestWorld.Withdraw(player, guid).Outcome);
        }

        [TestMethod]
        public void Vault_Initialize_MissingTicketProgressColumns_DisablesTheMarket()
        {
            MarketTestDatabase.Execute(VaultTestWorld.Db,
                "ALTER TABLE market_ticket DROP COLUMN progress, DROP COLUMN progress_Time, DROP COLUMN progress_Until, DROP COLUMN result;");
            try
            {
                Vault.Initialize();
                Assert.IsFalse(Vault.Available, "the game refuses to expose market actions on the old ticket schema");
            }
            finally
            {
                MarketTestDatabase.Execute(VaultTestWorld.Db,
                    "ALTER TABLE market_ticket ADD COLUMN progress varchar(32) DEFAULT NULL, ADD COLUMN progress_Time datetime(6) DEFAULT NULL, ADD COLUMN progress_Until datetime(6) DEFAULT NULL, ADD COLUMN result json DEFAULT NULL;");
                Vault.Initialize();
            }
        }

        // ---- helpers

        private sealed class ChannelRun
        {
            private readonly ManualResetEventSlim done = new ManualResetEventSlim();

            public VaultResult Result { get; private set; }

            public void Set(VaultResult result)
            {
                Assert.IsNull(Result, "the channel reports its result once");
                Result = result;
                done.Set();
            }

            public VaultResult Wait()
            {
                Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(30)), "the channel reported a result");
                VaultTestWorld.OnWorldThread(() => { }); // let the callback's follow-up settle
                return Result;
            }
        }

        private static ChannelRun StartDeposit(Player player, uint itemGuid)
        {
            var run = new ChannelRun();
            VaultTestWorld.OnWorldThread(() => VaultChannel.StartDeposit(player, itemGuid, run.Set));
            return run;
        }

        private static ChannelRun StartWithdraw(Player player, uint itemGuid)
        {
            var run = new ChannelRun();
            VaultTestWorld.OnWorldThread(() => VaultChannel.StartWithdraw(player, itemGuid, run.Set));
            return run;
        }

        /// <summary>
        /// A PK player channelling a deposit of a sword. AssertCancelledAndNothingMoved clears the channel setting.
        /// </summary>
        private static (Player player, WorldObject item, ChannelRun run) ChannellingPk(int seconds = 2, uint wcid = VaultTestWorld.SwordWcid)
        {
            var (player, item) = NewItem(null, wcid);
            player.PlayerKillerStatus = PlayerKillerStatus.PK;

            SetChannelSeconds(seconds);
            var run = StartDeposit(player, item.Guid.Full);

            Assert.IsTrue(player.IsVaultChannelling);
            return (player, item, run);
        }

        private static void AssertCancelledAndNothingMoved(Player player, WorldObject item, ChannelRun run)
        {
            var guid = item.Guid.Full;

            VaultTestWorld.OnWorldThread(() => { }); // a cancel reports on the world thread's next turn
            Assert.IsNotNull(run.Result, "the channel was cancelled at once");
            Assert.AreEqual(VaultOutcome.Interrupted, run.Result.Outcome, run.Result.Message);
            Assert.IsFalse(player.IsVaultChannelling);
            Assert.IsFalse(player.IsFrozen ?? false, "the player is unfrozen");

            // past the channel time: the timer must not act on a cancelled channel
            Thread.Sleep(2500);
            VaultTestWorld.OnWorldThread(() => { });
            ClearChannelSeconds();

            Assert.IsNotNull(player.GetInventoryItem(guid), "the item stays in the pack");
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"), "the database still has it in the pack");
            Assert.IsNull(VaultStore.Get(guid), "nothing went into the Vault");
        }

        private static Player NewPk()
        {
            var pk = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            pk.PlayerKillerStatus = PlayerKillerStatus.PK;
            return pk;
        }

        private static Player NewCaster(Skill school)
        {
            var caster = NewPk();
            var skill = caster.GetCreatureSkill(school);
            skill.AdvancementClass = SkillAdvancementClass.Specialized;
            skill.InitLevel = 5000; // never resisted
            return caster;
        }

        /// <summary>
        /// The step of a player's cast where the spell lands on the target
        /// </summary>
        private static void CastOn(Player caster, Player target, uint spellId)
        {
            var createPlayerSpell = typeof(Player).GetMethod("CreatePlayerSpell", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(WorldObject), typeof(Spell), typeof(bool) }, null);
            createPlayerSpell.Invoke(caster, new object[] { target, new Spell(spellId), false });
        }

        private static DamageEvent LandedHit(Player attacker, Player defender)
        {
            for (var i = 0; i < 1000; i++)
            {
                var damageEvent = DamageEvent.CalculateDamage(attacker, defender, attacker);

                if (!damageEvent.Evaded && !damageEvent.LifestoneProtection && !damageEvent.GeneralFailure)
                    return damageEvent;
            }

            Assert.Fail("no attack landed in 1000 tries");
            return null;
        }

        private static void SetLastCombatMode(Player player, CombatMode mode)
        {
            // the handlers switch to the combat mode the client last asked for
            typeof(Creature).GetProperty(nameof(Creature.CombatMode)).SetValue(player, CombatMode.NonCombat);
            player.LastCombatMode = mode;
        }

        private static void SetTrading(Player player, bool trading)
        {
            typeof(Player).GetProperty(nameof(Player.IsTrading)).SetValue(player, trading);
        }

        private static IDisposable ChannelSeconds(int seconds)
        {
            SetChannelSeconds(seconds);
            return new Cleanup(ClearChannelSeconds);
        }

        private static void SetChannelSeconds(int seconds)
        {
            MarketTestDatabase.Execute(Db, $"REPLACE INTO config_properties_long (`key`, `value`, description) VALUES ('{MarketSettings.ChannelSeconds.Key}', {seconds}, 'test');");
        }

        private static void ClearChannelSeconds()
        {
            MarketTestDatabase.Execute(Db, $"DELETE FROM config_properties_long WHERE `key` = '{MarketSettings.ChannelSeconds.Key}';");
        }
    }
}

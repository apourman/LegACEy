using System;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Command.Handlers;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// /god on a character the Vault test world makes: a real player, session and shard database
    /// </summary>
    public partial class VaultTests
    {
        [TestMethod]
        public void God_OnACharacterMissingSomeSkills_EntersGodMode_AndUngodReturnsIt()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            Assert.IsTrue(SkillHelper.ValidSkills.Any(skill => !player.Biota.PropertiesSkill.ContainsKey(skill)), "the test character lacks some skills, as a seeded character does");

            AdminCommands.HandleGod(player.Session);

            // the command saves the character first, and carries on when the save is done
            VaultTestWorld.WaitUntil(() => player.GodState?.StartsWith("1") == true, "god mode");

            // god mode is set on the database thread, which sets GodState before it raises the stats: a job queued now runs once it has finished,
            // so ungod below doesn't race it over the player's vitals
            var godDone = new ManualResetEventSlim();
            DatabaseManager.Shard.GetCurrentQueueWaitTime(_ => godDone.Set());
            Assert.IsTrue(godDone.Wait(TimeSpan.FromSeconds(30)), "the database thread finished god mode");

            Assert.AreEqual(999, player.Level);

            AdminCommands.HandleUngod(player.Session);
            Assert.IsNull(player.GodState);
            Assert.AreNotEqual(999, player.Level);
        }
    }
}

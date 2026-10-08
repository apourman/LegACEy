using System;
using System.Linq;
using System.Text.Json;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using MySqlConnector;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// Seam 2: the game bridge. Each ticket is written with the store the Market API uses, and the world's own poller claims it,
    /// runs it through the Vault entry point the Vault chest uses, and writes the result back.
    /// </summary>
    public partial class VaultTests
    {
        // ---- MMD

        [TestMethod]
        public void Bridge_MmdWithdraw_ForAnOnlineCharacter_NotesArriveInThePackAndTheTicketIsDone()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 50);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 20));

                Assert.AreEqual($"{TicketStatus.Done}|{TicketStore.Ok}", WaitForTicket(ticket));
                StringAssert.Contains(TicketMessage(ticket), "20 trade notes");

                Assert.AreEqual(20, NotesInPacks(player), "the notes are in the character's pack");
                CollectionAssert.AreEqual(new[] { 20L }, DatabaseNoteStacks(player.Guid.Full), "the database has them in the pack");
                Assert.AreEqual(30, Ledger.GetBalance(account));

                var transfer = SingleTransfer(account, TransferKind.NoteWithdraw);
                Assert.AreEqual(ticket, Count($"SELECT ticket_Id FROM market_transfer WHERE id = {transfer};"), "the transfer names the ticket it was saved with");
            }
        }

        [TestMethod]
        public void Bridge_MmdWithdraw_WhileTheMarketIsPaused_FailsAsPausedAndNothingMoves()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            using (var shard = MarketTestDatabase.CreateContext(Db))
                MarketPause.Pause(shard, "test", DateTime.UtcNow);

            try
            {
                using (VaultTestWorld.Online(player))
                {
                    var ticket = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 5));

                    Assert.AreEqual($"{TicketStatus.Failed}|paused", WaitForTicket(ticket));
                    Assert.AreEqual(0, NotesInPacks(player));
                    Assert.AreEqual(10, Ledger.GetBalance(account));
                }
            }
            finally
            {
                ResumeMarket();
            }
        }

        [TestMethod]
        public void Bridge_MmdWithdraw_SaveFails_TicketFailsAndNothingMoves()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            var balanceRow = BalanceRow(account);

            using (VaultTestWorld.Online(player))
            using (FailInsertsInto("market_ledger_entry"))
            {
                var ticket = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 4));

                Assert.AreEqual($"{TicketStatus.Failed}|save_failed", WaitForTicket(ticket));
            }

            Assert.AreEqual(0, NotesInPacks(player));
            Assert.AreEqual(0, DatabaseNoteStacks(player.Guid.Full).Count);
            Assert.AreEqual(balanceRow, BalanceRow(account), "the balance row is exactly as before");
        }

        [TestMethod]
        public void Bridge_MmdWithdraw_FullPack_IsRefusedNoPackSpace_TellsThePlayerToFreeSpace_AndNothingMoves()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            var balanceRow = BalanceRow(account);
            VaultTestWorld.OnWorldThread(() =>
            {
                while (player.TryAddToInventory(VaultTestWorld.NewItem(VaultTestWorld.SwordWcid), out _)) { }
            });

            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 4));

                Assert.AreEqual($"{TicketStatus.Failed}|no_pack_space", WaitForTicket(ticket));
                StringAssert.Contains(TicketMessage(ticket), "Free some pack space", "the website shows this message, which says what to do");
            }

            Assert.AreEqual(0, NotesInPacks(player));
            Assert.AreEqual(0, DatabaseNoteStacks(player.Guid.Full).Count);
            Assert.AreEqual(balanceRow, BalanceRow(account), "the balance row is exactly as before");
        }

        [TestMethod]
        public void Bridge_MmdWithdraw_PackFillsDuringTheSave_TicketIsDone_AndTheNotesArriveAtNextLogin()
        {
            var account = VaultTestWorld.NewAccountId();
            var player = VaultTestWorld.NewPlayer(account);
            GiveNotes(player, 50);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);

            using (VaultTestWorld.Online(player))
            {
                long ticket;

                // the withdrawal's save waits on this lock, after the up-front checks have passed
                using (var balanceLock = new MySqlConnection(MarketTestDatabase.ConnectionString(Db)))
                {
                    balanceLock.Open();
                    using var transaction = balanceLock.BeginTransaction();
                    using (var command = new MySqlCommand($"SELECT balance FROM market_balance WHERE account_Id = {account} FOR UPDATE;", balanceLock, transaction))
                        command.ExecuteScalar();

                    ticket = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 20));

                    // the bridge runs the ticket only once its checks passed, and the save's answer is what releases it: fill the pack in between
                    var filled = false;
                    VaultTestWorld.WaitUntil(() =>
                    {
                        VaultTestWorld.OnWorldThread(() =>
                        {
                            if (!GameBridge.IsTicketRunning(ticket))
                                return;

                            while (player.TryAddToInventory(VaultTestWorld.NewItem(VaultTestWorld.SwordWcid), out _)) { }
                            filled = true;
                        });
                        return filled;
                    }, "the withdrawal to pass its checks");

                    Assert.AreEqual(TicketStatus.Claimed, ReadTicket(ticket).Status, "the save has not run yet");
                    VaultTestWorld.TakeSent(player);
                    transaction.Rollback();
                }

                Assert.AreEqual($"{TicketStatus.Done}|{TicketStore.Ok}", WaitForTicket(ticket));
                VaultTestWorld.WaitUntil(() => !BridgeIsRunning(ticket), "the bridge to hear the withdrawal's answer");
                Assert.AreEqual(30, Ledger.GetBalance(account), "the balance was debited");
                Assert.IsTrue(VaultTestWorld.Chats(VaultTestWorld.TakeSent(player)).Any(c => c.Contains("when you next log in")), "the player is told the notes arrive at the next login");
                Assert.AreEqual(0, NotesInPacks(player), "the full pack took none of them now");
            }

            CollectionAssert.AreEqual(new[] { 20L }, DatabaseNoteStacks(player.Guid.Full), "the database has the notes in the character's pack");

            // the next login loads the character's possessions from the database
            var possessed = DatabaseManager.Shard.BaseDatabase.GetPossessedBiotasInParallel(player.Guid.Full);
            var loaded = new Player(player.Biota, possessed.Inventory, possessed.WieldedItems, player.Character, null);
            var notes = loaded.GetTradeNotes().Where(n => n.WeenieClassId == TradeNoteWcid).ToList();

            Assert.AreEqual(20, notes.Sum(n => n.StackSize ?? 1), "the notes are in the pack after reconnecting");
            Assert.IsTrue(notes.All(n => n.OwnerId == player.Guid.Full && n.ContainerId == player.Guid.Full), "the character owns them");
        }

        // ---- items

        [TestMethod]
        public void Bridge_ItemWithdraw_RunsTheChannelInGame_ThenTheItemIsInThePack()
        {
            var (player, guid) = DepositedItem();

            using (ChannelSeconds(3))
            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid));

                VaultTestWorld.WaitUntil(() => player.IsVaultChannelling, "the channel to start");
                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == "channelling", "the channel progress to reach the ticket");
                Assert.IsTrue(player.IsFrozen ?? false, "the channel freezes the player");
                Assert.AreEqual(TicketStatus.Claimed, TicketStatusOf(ticket), "the ticket is in progress while the player channels");
                var progress = ReadTicket(ticket);
                Assert.AreEqual("channelling", progress.Progress);
                Assert.IsNotNull(progress.ProgressTime);
                Assert.IsNotNull(progress.ProgressUntil);
                Assert.AreEqual(3, (progress.ProgressUntil.Value - progress.ProgressTime.Value).TotalSeconds, 0.01);
                Assert.AreEqual(VaultItemState.Withdrawing, VaultStore.Get(guid).State);

                Assert.AreEqual($"{TicketStatus.Done}|{TicketStore.Ok}", WaitForTicket(ticket));
            }

            VaultTestWorld.WaitUntil(() => player.GetInventoryItem(guid) != null, "the item to reach the pack");
            Assert.IsNull(VaultStore.Get(guid), "the Vault row is gone");
            Assert.IsFalse(player.IsFrozen ?? false);
            Assert.AreEqual(player.Guid.Full, (uint)Count($"SELECT value FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"));
        }

        [TestMethod]
        public void Bridge_ItemWithdraw_CharacterOffline_FailsWithAReasonAndNothingMoves()
        {
            var (player, guid) = DepositedItem();

            var ticket = NewTicket(player, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid));

            Assert.AreEqual($"{TicketStatus.Failed}|offline", WaitForTicket(ticket));
            Assert.IsFalse(string.IsNullOrWhiteSpace(TicketMessage(ticket)));
            AssertStillHeld(player, guid);
        }

        [TestMethod]
        public void Bridge_ItemWithdraw_InAPlayerFight_FailsWithAReasonAndNothingMoves()
        {
            var (player, guid) = DepositedItem();
            player.PlayerKillerStatus = PlayerKillerStatus.PK;
            Player.UpdatePKTimers(NewPk(), player);

            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid));

                Assert.AreEqual($"{TicketStatus.Failed}|recent_player_fight", WaitForTicket(ticket));
            }

            Assert.IsFalse(player.IsVaultChannelling);
            AssertStillHeld(player, guid);
        }

        [TestMethod]
        public void Bridge_ItemWithdraw_NoPackSpace_FailsWithAReasonAndNothingMoves()
        {
            var (player, guid) = DepositedItem();
            VaultTestWorld.OnWorldThread(() =>
            {
                while (player.TryAddToInventory(VaultTestWorld.NewItem(VaultTestWorld.SwordWcid), out _)) { }
            });

            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid));

                Assert.AreEqual($"{TicketStatus.Failed}|no_pack_space", WaitForTicket(ticket));
            }

            AssertStillHeld(player, guid);
        }

        [TestMethod]
        public void Bridge_TicketForAnotherAccountsCharacter_OrOfAnUnsupportedKind_Fails()
        {
            var (player, guid) = DepositedItem();
            var stranger = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());

            using (VaultTestWorld.Online(player))
            using (VaultTestWorld.Online(stranger))
            {
                var wrongAccount = NewTicket(stranger, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid), accountId: player.Character.AccountId);
                var deposit = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: guid));
                var unsupported = NewTicket(player, "future_market_request", new TicketPayload(ItemGuid: guid));

                Assert.AreEqual($"{TicketStatus.Failed}|invalid_character", WaitForTicket(wrongAccount));
                Assert.AreEqual($"{TicketStatus.Failed}|not_in_pack", WaitForTicket(deposit));
                Assert.AreEqual($"{TicketStatus.Failed}|unsupported_kind", WaitForTicket(unsupported));
            }

            AssertStillHeld(player, guid);
        }

        [TestMethod]
        public void Bridge_InventorySnapshot_ListsPackItemsWithSharedRefusals_AndMovesNothing()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var allowed = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var attuned = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            attuned.Attuned = AttunedStatus.Attuned;
            VaultTestWorld.Give(player, attuned);
            var worn = VaultTestWorld.NewItem(VaultTestWorld.SwordWcid);
            Assert.IsTrue(player.TryEquipObject(worn, EquipMask.MeleeWeapon));

            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.InventorySnapshot, new TicketPayload());

                Assert.AreEqual($"{TicketStatus.Done}|{TicketStore.Ok}", WaitForTicket(ticket));

                using var result = JsonDocument.Parse(ReadTicket(ticket).Result);
                var items = result.RootElement.GetProperty("items").EnumerateArray().ToArray();
                var entries = items.ToDictionary(item => item.GetProperty("itemGuid").GetUInt32());

                Assert.AreEqual(2, entries.Count, "pack contents are included and worn items are omitted");
                Assert.IsTrue(entries.ContainsKey(allowed.Guid.Full));
                Assert.IsTrue(entries.ContainsKey(attuned.Guid.Full));
                Assert.IsFalse(entries.ContainsKey(worn.Guid.Full));
                Assert.AreEqual(JsonValueKind.Null, entries[allowed.Guid.Full].GetProperty("refusalCode").ValueKind);
                Assert.AreEqual("attuned", entries[attuned.Guid.Full].GetProperty("refusalCode").GetString());
                Assert.AreEqual(allowed.Name, entries[allowed.Guid.Full].GetProperty("name").GetString());
                Assert.AreEqual(allowed.StackSize ?? 1, entries[allowed.Guid.Full].GetProperty("stackSize").GetInt32());
                Assert.IsNotNull(entries[allowed.Guid.Full].GetProperty("icon"));
                Assert.IsNotNull(player.GetInventoryItem(allowed.Guid.Full), "the snapshot did not remove the item");
                Assert.IsNotNull(player.GetInventoryItem(attuned.Guid.Full), "a refused item stays in the pack");
                Assert.AreEqual(0, VaultStore.Count(player.Character.AccountId));
            }
        }

        [TestMethod]
        public void Bridge_MarketClosed_LeavesTicketsWaitingUntilItOpens()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());

            PropertyManager.ModifyBool(Vault.MarketEnabledKey, false);
            long ticket;
            try
            {
                ticket = NewTicket(player, TicketKind.InventorySnapshot, new TicketPayload());
                System.Threading.Thread.Sleep(GameBridge.PollInterval * 3);

                Assert.AreEqual(TicketStatus.Waiting, TicketStatusOf(ticket), "a closed market claims nothing");
            }
            finally
            {
                PropertyManager.ModifyBool(Vault.MarketEnabledKey, true);
            }

            Assert.AreEqual($"{TicketStatus.Failed}|{GameBridge.Offline}", WaitForTicket(ticket));
        }

        [TestMethod]
        public void Bridge_InventorySnapshot_OfflineCharacterFails()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var ticket = NewTicket(player, TicketKind.InventorySnapshot, new TicketPayload());

            Assert.AreEqual($"{TicketStatus.Failed}|{GameBridge.Offline}", WaitForTicket(ticket));
            Assert.IsFalse(string.IsNullOrWhiteSpace(TicketMessage(ticket)));
        }

        [TestMethod]
        public void Bridge_VaultDeposit_ConfirmationStartsChannelAndCompletesInItsOwnSave()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            long ticket;

            using (ChannelSeconds(3))
            using (VaultTestWorld.Online(player))
            {
                ticket = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: item.Guid.Full));
                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.AwaitingConfirmation, "the in-game confirmation request");

                Assert.IsNotNull(player.ConfirmationManager);
                var context = PendingConfirmationContext(player);
                VaultTestWorld.OnWorldThread(() => player.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, context, true));

                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.Channelling, "the deposit channel to start after Yes");
                Assert.IsTrue(player.IsFrozen ?? false, "the normal channel freezes the player after Yes");
                var progress = ReadTicket(ticket);
                Assert.AreEqual(3, (progress.ProgressUntil.Value - progress.ProgressTime.Value).TotalSeconds, 0.01);
                Assert.AreEqual($"{TicketStatus.Done}|{TicketStore.Ok}", WaitForTicket(ticket));
            }

            VaultTestWorld.WaitUntil(() => VaultStore.Get(item.Guid.Full) != null, "the deposited Vault row");
            Assert.IsNull(player.GetInventoryItem(item.Guid.Full));
            Assert.AreEqual(0L, Count($"SELECT COUNT(*) FROM market_ticket WHERE id = {ticket} AND status <> '{TicketStatus.Done}';"));
            Assert.AreEqual(0L, Count($"SELECT COUNT(*) FROM market_vault_item WHERE item_Guid = {item.Guid.Full} AND account_Id <> {player.Character.AccountId};"));
        }

        [TestMethod]
        public void Bridge_VaultDeposit_NoFailsAndLeavesItemInThePack()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));

            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: item.Guid.Full));
                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.AwaitingConfirmation, "the in-game confirmation request");
                var context = PendingConfirmationContext(player);
                VaultTestWorld.OnWorldThread(() => player.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, context, false));

                Assert.AreEqual($"{TicketStatus.Failed}|{GameBridge.DeclinedResultCode}", WaitForTicket(ticket));
                Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full));
                Assert.IsNull(VaultStore.Get(item.Guid.Full));
            }
        }

        [TestMethod]
        public void Bridge_VaultDeposit_ExistingYesNoPopupFailsBusyAndKeepsThatPopup()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));

            using (VaultTestWorld.Online(player))
            {
                VaultTestWorld.OnWorldThread(() => Assert.IsTrue(player.ConfirmationManager.EnqueueSend(new Confirmation_Custom(player.Guid, () => { }), "Existing question")));
                var oldContext = PendingConfirmationContext(player);
                var ticket = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: item.Guid.Full));

                Assert.AreEqual($"{TicketStatus.Failed}|{GameBridge.ConfirmationBusyResultCode}", WaitForTicket(ticket));
                Assert.AreEqual(oldContext, PendingConfirmationContext(player), "the existing popup remains open");
                Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full));
                var failed = ReadTicket(ticket);
                Assert.IsNull(failed.Progress, "a terminal ticket has no stale progress stage");
                Assert.IsNull(failed.ProgressTime);
                Assert.IsNull(failed.ProgressUntil);

                VaultTestWorld.OnWorldThread(() => player.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, oldContext, false));
            }
        }

        [TestMethod]
        public void Bridge_VaultDeposit_TimeoutRemovesOnlyItsPopup_AndLateYesCannotAnswerTheNextOne()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));

            using (VaultTestWorld.Online(player))
            {
                var first = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: item.Guid.Full));
                VaultTestWorld.WaitUntil(() => ReadTicket(first).Progress == TicketProgress.AwaitingConfirmation, "the first confirmation request");
                var oldContext = PendingConfirmationContext(player);

                Assert.AreEqual($"{TicketStatus.Failed}|{GameBridge.ConfirmationTimeoutResultCode}", WaitForTicket(first, TimeSpan.FromSeconds(45)));
                Assert.IsNull(PendingConfirmationContextOrNull(player), "timeout removed the expired confirmation");
                Assert.IsFalse(BridgeIsRunning(first), "timeout released the bridge's running entry");
                var timedOut = TicketOutcome(first);
                VaultTestWorld.OnWorldThread(() => player.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, oldContext, true));
                Assert.IsFalse(player.IsVaultChannelling, "a late Yes with no pending popup starts no channel");
                AfterQueuedWork(); // anything the late Yes queued has run, writes included
                Assert.AreEqual(timedOut, TicketOutcome(first), "a late Yes leaves the timed-out ticket's status and result as they were");

                var second = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: item.Guid.Full));
                VaultTestWorld.WaitUntil(() => ReadTicket(second).Progress == TicketProgress.AwaitingConfirmation, "the next confirmation request");
                var newContext = PendingConfirmationContext(player);

                Assert.AreNotEqual(oldContext, newContext);
                VaultTestWorld.OnWorldThread(() => player.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, oldContext, true));
                Assert.AreEqual(newContext, PendingConfirmationContext(player), "a late answer leaves the newer popup pending");
                Assert.AreEqual(TicketStatus.Claimed, ReadTicket(second).Status);

                VaultTestWorld.OnWorldThread(() => player.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, newContext, false));
                Assert.AreEqual($"{TicketStatus.Failed}|{GameBridge.DeclinedResultCode}", WaitForTicket(second));
                Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full));
            }
        }

        [TestMethod]
        public void Bridge_VaultDeposit_RechecksPackAndRecentFightAfterYes()
        {
            var movedPlayer = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var movedItem = VaultTestWorld.Give(movedPlayer, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));

            using (VaultTestWorld.Online(movedPlayer))
            {
                var ticket = NewTicket(movedPlayer, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: movedItem.Guid.Full));
                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.AwaitingConfirmation, "the confirmation before pack re-check");
                var context = PendingConfirmationContext(movedPlayer);
                VaultTestWorld.OnWorldThread(() =>
                {
                    Assert.IsTrue(movedPlayer.TryRemoveFromInventoryForVault(movedItem.Guid, out _));
                    movedPlayer.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, context, true);
                });

                Assert.AreEqual($"{TicketStatus.Failed}|not_in_pack", WaitForTicket(ticket));
                Assert.IsNull(VaultStore.Get(movedItem.Guid.Full));
            }

            var fightingPlayer = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var fightingItem = VaultTestWorld.Give(fightingPlayer, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            fightingPlayer.PlayerKillerStatus = PlayerKillerStatus.PK;

            using (VaultTestWorld.Online(fightingPlayer))
            {
                var ticket = NewTicket(fightingPlayer, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: fightingItem.Guid.Full));
                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.AwaitingConfirmation, "the confirmation before fight re-check");
                var context = PendingConfirmationContext(fightingPlayer);
                VaultTestWorld.OnWorldThread(() =>
                {
                    Player.UpdatePKTimers(NewPk(), fightingPlayer);
                    fightingPlayer.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, context, true);
                });

                Assert.AreEqual($"{TicketStatus.Failed}|recent_player_fight", WaitForTicket(ticket));
                Assert.IsNotNull(fightingPlayer.GetInventoryItem(fightingItem.Guid.Full));
                Assert.IsFalse(fightingPlayer.IsVaultChannelling);
            }
        }

        [TestMethod]
        public void Bridge_VaultDeposit_DeathWhilePopupOpenFailsInterrupted()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));

            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: item.Guid.Full));
                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.AwaitingConfirmation, "the confirmation before death");
                var context = PendingConfirmationContext(player);

                VaultTestWorld.OnWorldThread(() =>
                {
                    player.IsInDeathProcess = true;
                    player.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, context, true);
                });

                Assert.AreEqual($"{TicketStatus.Failed}|interrupted", WaitForTicket(ticket));
                Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full));
                Assert.IsFalse(player.IsVaultChannelling);
                Assert.IsNull(VaultStore.Get(item.Guid.Full));
            }
        }

        [TestMethod]
        public void Bridge_VaultDeposit_ProgressStartsAtYesAfterTwentyFiveSecondsAwaitingConfirmation()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));

            using (ChannelSeconds(3))
            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: item.Guid.Full));
                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.AwaitingConfirmation, "the confirmation request");
                MarketTestDatabase.Execute(Db, $"UPDATE market_ticket SET claimed_Time = UTC_TIMESTAMP(6) - INTERVAL 25 SECOND WHERE id = {ticket};");
                var context = PendingConfirmationContext(player);
                VaultTestWorld.OnWorldThread(() => player.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, context, true));

                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.Channelling, "the channel progress after Yes");
                var progress = ReadTicket(ticket);
                Assert.IsTrue((progress.ProgressTime.Value - progress.ClaimedTime.Value).TotalSeconds >= 24, "the channel starts from the answer, not the claim");
                Assert.AreEqual(3, (progress.ProgressUntil.Value - progress.ProgressTime.Value).TotalSeconds, 0.01);
                Assert.AreEqual($"{TicketStatus.Done}|{TicketStore.Ok}", WaitForTicket(ticket));
            }
        }

        [TestMethod]
        public void Bridge_VaultDeposit_DeadlineAfterLogoutFailsOffline()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var online = VaultTestWorld.Online(player);
            var ticket = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: item.Guid.Full));
            VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.AwaitingConfirmation, "the confirmation request");
            var context = PendingConfirmationContext(player);

            online.Dispose();
            VaultTestWorld.OnWorldThread(() => player.ConfirmationManager.Timeout(ConfirmationType.Yes_No, context));

            Assert.AreEqual($"{TicketStatus.Failed}|{GameBridge.Offline}", WaitForTicket(ticket));
            Assert.IsNull(PendingConfirmationContextOrNull(player));
            Assert.IsFalse(BridgeIsRunning(ticket));
            Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full));
            Assert.IsNull(VaultStore.Get(item.Guid.Full));
        }

        [TestMethod]
        public void Bridge_VaultDeposit_DeadlineAfterRelogFailsOfflineForTheOriginalCharacterSession()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            var originalLogin = VaultTestWorld.Online(player);
            var ticket = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: item.Guid.Full));
            VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.AwaitingConfirmation, "the confirmation request");
            var context = PendingConfirmationContext(player);

            originalLogin.Dispose();
            var reloggedPlayer = VaultTestWorld.Reconnect(player);
            using (VaultTestWorld.Online(reloggedPlayer))
            {
                VaultTestWorld.OnWorldThread(() => player.ConfirmationManager.Timeout(ConfirmationType.Yes_No, context));

                Assert.AreEqual($"{TicketStatus.Failed}|{GameBridge.Offline}", WaitForTicket(ticket));
                Assert.IsFalse(reloggedPlayer.IsVaultChannelling);
                Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full));
                Assert.IsNull(VaultStore.Get(item.Guid.Full));
            }
        }

        [TestMethod]
        public void ConfirmationManager_FellowshipStaleContextKeepsCurrentConfirmation()
        {
            var inviter = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var invited = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());

            using (VaultTestWorld.Online(inviter))
            using (VaultTestWorld.Online(invited))
            {
                var confirmation = new Confirmation_Fellowship(inviter.Guid, invited.Guid);
                VaultTestWorld.OnWorldThread(() => Assert.IsTrue(invited.ConfirmationManager.EnqueueSend(confirmation, inviter.Name)));
                var currentContext = PendingConfirmationContext(invited, ConfirmationType.Fellowship);
                var staleContext = currentContext == 1 ? currentContext + 1 : currentContext - 1;

                VaultTestWorld.OnWorldThread(() => invited.ConfirmationManager.HandleResponse(ConfirmationType.Fellowship, staleContext, false));

                Assert.AreEqual(currentContext, PendingConfirmationContext(invited, ConfirmationType.Fellowship));
                CollectionAssert.Contains(VaultTestWorld.Chats(VaultTestWorld.TakeSent(invited)), "That offer of fellowship has expired.");
                VaultTestWorld.OnWorldThread(() => invited.ConfirmationManager.HandleResponse(ConfirmationType.Fellowship, currentContext, false));
            }
        }

        [TestMethod]
        public void Bridge_VaultDeposit_PlayerHitDuringChannelFailsInterruptedAndKeepsItemInPack()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var item = VaultTestWorld.Give(player, VaultTestWorld.NewItem(VaultTestWorld.SwordWcid));
            player.PlayerKillerStatus = PlayerKillerStatus.PK;

            using (ChannelSeconds(30))
            using (VaultTestWorld.Online(player))
            {
                var ticket = NewTicket(player, TicketKind.VaultDeposit, new TicketPayload(ItemGuid: item.Guid.Full));
                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.AwaitingConfirmation, "the confirmation request");
                var context = PendingConfirmationContext(player);
                VaultTestWorld.OnWorldThread(() => player.ConfirmationManager.HandleResponse(ConfirmationType.Yes_No, context, true));
                VaultTestWorld.WaitUntil(() => ReadTicket(ticket).Progress == TicketProgress.Channelling, "the deposit channel");

                VaultTestWorld.OnWorldThread(() => player.TakeDamage(NewPk(), DamageType.Slash, 1, BodyPart.Chest));
                Assert.AreEqual($"{TicketStatus.Failed}|interrupted", WaitForTicket(ticket));
                Assert.IsNotNull(player.GetInventoryItem(item.Guid.Full));
                Assert.IsNull(VaultStore.Get(item.Guid.Full));
            }
        }

        private static uint PendingConfirmationContext(Player player, ConfirmationType confirmationType = ConfirmationType.Yes_No)
        {
            var context = PendingConfirmationContextOrNull(player, confirmationType);
            Assert.IsTrue(context.HasValue, "a yes/no confirmation is pending");
            return context.Value;
        }

        private static uint? PendingConfirmationContextOrNull(Player player, ConfirmationType confirmationType = ConfirmationType.Yes_No)
        {
            var context = (uint?)null;
            VaultTestWorld.OnWorldThread(() => context = player.ConfirmationManager.PendingContext(confirmationType));
            return context;
        }

        private static bool BridgeIsRunning(long ticketId)
        {
            var running = false;
            VaultTestWorld.OnWorldThread(() => running = GameBridge.IsTicketRunning(ticketId));
            return running;
        }

        // ---- restart

        [TestMethod]
        public void Bridge_Restart_ClaimedTicketsAreFailedWithAReason_AndNothingWasHalfDone()
        {
            var (player, guid) = DepositedItem();
            var account = player.Character.AccountId;
            GiveNotes(player, 10);
            Assert.AreEqual(VaultOutcome.NotesDeposited, VaultTestWorld.DepositNotes(player).Outcome);
            var balanceRow = BalanceRow(account);

            // the server stopped while it worked on these: an item withdrawal whose channel had marked the row, and a note withdrawal
            var itemTicket = NewClaimedTicket(player, TicketKind.VaultWithdraw, new TicketPayload(ItemGuid: guid));
            var notesTicket = NewClaimedTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 5));
            Assert.IsNotNull(VaultStore.TryMarkWithdrawing(guid, account, VaultStore.Get(guid).RowVersion));

            Vault.Initialize();

            Assert.AreEqual($"{TicketStatus.Failed}|{TicketStore.ServerRestart}", TicketResult(itemTicket));
            Assert.AreEqual($"{TicketStatus.Failed}|{TicketStore.ServerRestart}", TicketResult(notesTicket));
            Assert.IsFalse(string.IsNullOrWhiteSpace(TicketMessage(itemTicket)));

            AssertStillHeld(player, guid);
            Assert.AreEqual(balanceRow, BalanceRow(account), "no MMD left the balance");
            Assert.AreEqual(0, NotesInPacks(player));
        }

        [TestMethod]
        public void Bridge_FinishedTicketsOlderThanThirtyDays_AreRemoved()
        {
            var player = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var old = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 1));
            var recent = NewTicket(player, TicketKind.MmdWithdraw, new TicketPayload(Amount: 1));

            // finished by the poller (the character is offline), then aged
            Assert.AreEqual($"{TicketStatus.Failed}|offline", WaitForTicket(old));
            Assert.AreEqual($"{TicketStatus.Failed}|offline", WaitForTicket(recent));
            MarketTestDatabase.Execute(Db, $"UPDATE market_ticket SET finished_Time = UTC_TIMESTAMP(6) - INTERVAL 31 DAY WHERE id = {old};");
            MarketTestDatabase.Execute(Db, $"UPDATE market_ticket SET finished_Time = UTC_TIMESTAMP(6) - INTERVAL 29 DAY WHERE id = {recent};");

            Vault.Initialize();

            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM market_ticket WHERE id = {old};"), "the old ticket is deleted at startup");
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM market_ticket WHERE id = {recent};"));
        }

        [TestMethod]
        public void Bridge_EveryVaultRefusal_HasItsOwnResultCode()
        {
            var refusals = Enum.GetValues<VaultOutcome>().Where(o => !new VaultResult(o, "", 0).Success).ToList();
            var codes = refusals.Select(GameBridge.ResultCode).ToList();

            CollectionAssert.DoesNotContain(codes, "failed", "every refusal is named");
            Assert.AreEqual(codes.Count, codes.Distinct().Count(), "no two refusals share a code");
            CollectionAssert.AllItemsAreNotNull(codes);
            Assert.IsTrue(codes.All(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^[a-z]+(_[a-z]+)*$")), "codes are snake case");
            Assert.AreEqual("no_pack_space", GameBridge.ResultCode(VaultOutcome.NoPackSpace));
        }

        // ---- helpers

        /// <summary>
        /// A ticket written as the Market API writes one, for the player's character (the account is the player's unless given)
        /// </summary>
        private static long NewTicket(Player player, string kind, TicketPayload payload, uint? accountId = null)
        {
            using var shard = MarketTestDatabase.CreateContext(Db);

            var result = TicketStore.Create(shard, accountId ?? player.Character.AccountId, player.Guid.Full, kind, payload, Guid.NewGuid().ToString("N"), DateTime.UtcNow);
            Assert.AreEqual(TicketCreateOutcome.Created, result.Outcome);

            return result.Ticket.Id;
        }

        /// <summary>
        /// A ticket as a game server leaves it while working on it. Written already claimed, so the world's poller never sees it waiting.
        /// </summary>
        private static long NewClaimedTicket(Player player, string kind, TicketPayload payload)
        {
            MarketTestDatabase.Execute(Db, "INSERT INTO market_ticket (kind, account_Id, character_Id, payload, status, idempotency_Key, created_Time, claimed_Time) " +
                $"VALUES ('{kind}', {player.Character.AccountId}, {player.Guid.Full}, '{payload.ToJson()}', '{TicketStatus.Claimed}', '{Guid.NewGuid():N}', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));");

            return Count($"SELECT MAX(id) FROM market_ticket WHERE account_Id = {player.Character.AccountId} AND kind = '{kind}';");
        }

        /// <summary>
        /// Waits for the game server to finish the ticket, and returns its status and result code
        /// </summary>
        private static string WaitForTicket(long ticketId)
        {
            VaultTestWorld.WaitUntil(() => TicketStatusOf(ticketId) is TicketStatus.Done or TicketStatus.Failed, $"ticket {ticketId} to finish");

            return TicketResult(ticketId);
        }

        private static string WaitForTicket(long ticketId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (TicketStatusOf(ticketId) is not (TicketStatus.Done or TicketStatus.Failed))
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, $"timed out waiting for ticket {ticketId} to finish");
                System.Threading.Thread.Sleep(20);
            }

            return TicketResult(ticketId);
        }

        private static string TicketStatusOf(long ticketId) => MarketTestDatabase.Rows(Db, $"SELECT status FROM market_ticket WHERE id = {ticketId};").Single();

        private static string TicketResult(long ticketId) => MarketTestDatabase.Rows(Db, $"SELECT status, result_Code FROM market_ticket WHERE id = {ticketId};").Single();

        private static string TicketMessage(long ticketId) => MarketTestDatabase.Rows(Db, $"SELECT result_Message FROM market_ticket WHERE id = {ticketId};").Single();

        /// <summary>
        /// Waits until work already queued on the world thread, and the saves that work queued, have run
        /// </summary>
        private static void AfterQueuedWork()
        {
            VaultTestWorld.OnWorldThread(() => { });

            var saved = new ManualResetEventSlim();
            DatabaseManager.Shard.GetCurrentQueueWaitTime(_ => saved.Set());
            Assert.IsTrue(saved.Wait(TimeSpan.FromSeconds(30)), "the save queue ran the queued work");
        }

        /// <summary>
        /// Everything a finished ticket says about how it ended
        /// </summary>
        private static string TicketOutcome(long ticketId) => MarketTestDatabase.Rows(Db, $@"SELECT status, IFNULL(result_Code, ''), IFNULL(result_Message, ''), IFNULL(result, ''),
            IFNULL(progress, ''), IFNULL(progress_Time, ''), IFNULL(progress_Until, ''), IFNULL(finished_Time, '') FROM market_ticket WHERE id = {ticketId};").Single();

        private static Ticket ReadTicket(long ticketId)
        {
            using var shard = MarketTestDatabase.CreateContext(Db);
            return shard.MarketTickets.Single(t => t.Id == ticketId);
        }

        private static void AssertStillHeld(Player player, uint guid)
        {
            Assert.AreEqual(VaultItemState.Held, VaultStore.Get(guid)?.State, "the item is still held in the Vault");
            Assert.IsNull(player.GetInventoryItem(guid), "the item is not in the pack");
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM biota_properties_i_i_d WHERE object_Id = {guid} AND type = {(int)PropertyInstanceId.Container};"), "the database has no container for it");
        }
    }
}

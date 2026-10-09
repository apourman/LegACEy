using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Database.Models.Shard.Market;

namespace ACE.Server.Market
{
    /// <summary>
    /// What a player is told for each Vault outcome. Every refusal has its own wording so the player knows what to fix.
    /// </summary>
    public static class VaultMessages
    {
        public static string For(VaultOutcome outcome, string itemName)
        {
            var item = string.IsNullOrEmpty(itemName) ? "That item" : itemName;

            return outcome switch
            {
                VaultOutcome.Deposited => $"{item} is now in your Vault.",
                VaultOutcome.Withdrawn => $"{item} is back in your pack.",

                VaultOutcome.WithdrawnAtLogin => $"{item} is yours, but your pack has no room for it right now. It will be in your pack when you next log in.",
                VaultOutcome.Busy => "Wait for your last Vault action to finish.",
                VaultOutcome.InTrade => $"Take {item} out of the trade window before you put it in the Vault.",
                VaultOutcome.NotAvailable => "The Vault is not available right now.",
                VaultOutcome.NotInPack => "That item is not in your pack. Appraise an item in your pack first.",
                VaultOutcome.Worn => $"Take {item} off before you put it in the Vault.",
                VaultOutcome.Attuned => $"{item} is attuned and cannot go in the Vault.",
                VaultOutcome.ContainsAttuned => $"{item} holds an attuned item and cannot go in the Vault.",
                VaultOutcome.PetOut => $"Unsummon your pet before you put {item} in the Vault.",
                VaultOutcome.ContainerNotEmpty => $"Empty {item} before you put it in the Vault.",
                VaultOutcome.BlockedWcid => $"{item} cannot go in the Vault.",
                VaultOutcome.VaultFull => "Your Vault is full. List or withdraw something first.",
                VaultOutcome.NotInVault => "That item is not in your Vault.",
                VaultOutcome.Listed => $"{item} is listed for sale. Delist it before you withdraw it.",
                VaultOutcome.Withdrawing => $"{item} is already being withdrawn.",
                VaultOutcome.NoPackSpace => $"You do not have room in your pack for {item}.",
                VaultOutcome.UniqueLimit => $"You cannot carry any more of {item}.",
                VaultOutcome.SaveFailed => $"The Vault could not save {item}. Nothing was changed.",
                VaultOutcome.Unconfirmed => $"The Vault could not confirm whether {item} moved. Log out and back in: it will be in your pack or in your Vault.",
                VaultOutcome.Banned => "Your account is banned, so its Vault is frozen.",
                VaultOutcome.MarketClosed => "The marketplace is not open yet.",

                VaultOutcome.RecentPlayerFight => "You have been in a player fight too recently to use the Vault. Try again in a couple of minutes.",
                VaultOutcome.Trading => "Close the trade window before you use the Vault.",
                VaultOutcome.Channelling => "You are already moving an item to or from your Vault.",
                VaultOutcome.Interrupted => $"Your Vault channel was interrupted. {item} did not move.",

                _ => outcome.ToString(),
            };
        }

        /// <summary>
        /// What a player is told for a batch withdrawal. A refusal of one item names it; a refusal of the set names the set, as nothing in it moved.
        /// </summary>
        public static string ForBatch(VaultOutcome outcome, string firstName, int count)
        {
            if (count == 1)
                return For(outcome, firstName);

            var items = $"{count:N0} items";

            return outcome switch
            {
                VaultOutcome.Withdrawn => $"{items} are back in your pack.",
                VaultOutcome.WithdrawnAtLogin => $"{items} are yours, but your pack has no room for all of them right now. The rest will be in your pack when you next log in.",
                VaultOutcome.NoPackSpace => $"You do not have room in your pack for all {items}. Nothing was withdrawn.",
                VaultOutcome.UniqueLimit => $"You cannot carry all {items}. Nothing was withdrawn.",
                VaultOutcome.SaveFailed or VaultOutcome.Unconfirmed or VaultOutcome.Banned => For(outcome, items),
                _ => For(outcome, firstName),
            };
        }

        /// <summary>
        /// What a player is told for a trade note (MMD) outcome
        /// </summary>
        public static string ForNotes(VaultOutcome outcome, long amount, long balance)
        {
            var notes = TradeNotes(amount);

            return outcome switch
            {
                VaultOutcome.NotesDeposited => $"You deposit {notes}. Your balance is {balance:N0} MMD.",
                VaultOutcome.NotesWithdrawn => $"You withdraw {notes}. Your balance is {balance:N0} MMD.",
                VaultOutcome.NotesWithdrawnAtLogin => $"You withdraw {notes}, but your pack has no room for all of them right now. The rest will be in your pack when you next log in. Your balance is {balance:N0} MMD.",
                VaultOutcome.NoNotes => "You have no trade notes in your packs to deposit.",
                VaultOutcome.InvalidAmount => "Withdraw at least 1 MMD.",
                VaultOutcome.InsufficientFunds => $"You cannot withdraw {notes}. Your balance is {balance:N0} MMD.",
                VaultOutcome.NoPackSpace => $"You do not have room in your pack for {notes}. Nothing was withdrawn. Free some pack space and try again.",
                VaultOutcome.SaveFailed => "The Vault could not save your trade notes. Nothing was changed.",
                VaultOutcome.Unconfirmed => "The Vault could not confirm whether your trade notes moved. Log out and back in, then check /vault balance.",
                VaultOutcome.Banned => "Your account is banned, so its balance is frozen.",
                VaultOutcome.Paused => $"The market is paused, so MMD withdrawals are stopped for now. Your balance is {balance:N0} MMD.",
                _ => For(outcome, "Your trade notes"),
            };
        }

        public static string Balance(long balance) => $"Your balance is {balance:N0} MMD.";

        /// <summary>
        /// A game bridge withdrawal's result. It is saved with the withdrawal, before anyone knows whether the pack still has room,
        /// so it holds either way: the item or notes are in the pack now, or at the character's next login.
        /// </summary>
        public static string WithdrawnByTicket(string what, string characterName) => $"{what} withdrawn to {characterName}.";

        public static string DepositedByTicket(string what, string characterName) => $"{what} deposited from {characterName}.";

        public static string TradeNotes(long amount) => $"{amount:N0} trade note{(amount == 1 ? "" : "s")}";

        public static string LinkCode(string code, long minutes) =>
            $"Your plugin link code is {code}. Enter it in the UtilityBelt plugin within {minutes:N0} minute{(minutes == 1 ? "" : "s")}. It works once, and never share it.";

        /// <summary>
        /// /vault tokens: a header, one line per token, and how to revoke one
        /// </summary>
        public static IEnumerable<string> Tokens(IReadOnlyCollection<PluginToken> tokens)
        {
            if (tokens.Count == 0)
            {
                yield return "Your account has no plugin tokens. /vault link signs in the UtilityBelt plugin.";
                yield break;
            }

            yield return $"Your account's plugin tokens ({tokens.Count:N0}):";

            foreach (var token in tokens)
                yield return $"#{token.Id}  {token.Label ?? "(no label)"}  last used {(token.LastUsedTime is DateTime used ? Day(used) : "never")}, expires {Day(token.ExpiresTime)}";

            yield return "/vault tokens revoke <id> stops one.";
        }

        public static string TokenRevoked(long tokenId, bool revoked) =>
            revoked ? $"Revoked plugin token #{tokenId}. It stops working at once." : $"Your account has no plugin token #{tokenId}.";

        private static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>
        /// What a player is told when a deposit or withdrawal channel starts
        /// </summary>
        public static string ChannelStarted(bool deposit, string itemName, int seconds)
        {
            var item = string.IsNullOrEmpty(itemName) ? "the item" : itemName;
            var time = $"{seconds} second{(seconds == 1 ? "" : "s")}";

            return deposit
                ? $"You begin moving {item} into your Vault. Hold still for {time}; a player attack, death or logging out stops it."
                : $"You begin taking {item} out of your Vault. Hold still for {time}; a player attack, death or logging out stops it.";
        }
    }
}

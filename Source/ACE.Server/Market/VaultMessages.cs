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

                VaultOutcome.NotAvailable => "The Vault is not available right now.",
                VaultOutcome.NotInPack => "That item is not in your pack. Appraise an item in your pack first.",
                VaultOutcome.Worn => $"Take {item} off before you put it in the Vault.",
                VaultOutcome.Attuned => $"{item} is attuned and cannot go in the Vault.",
                VaultOutcome.ContainsAttuned => $"{item} holds an attuned item and cannot go in the Vault.",
                VaultOutcome.PetOut => $"Unsummon your pet before you put {item} in the Vault.",
                VaultOutcome.ContainerNotEmpty => $"Empty {item} before you put it in the Vault.",
                VaultOutcome.BlockedWcid => $"{item} cannot go in the Vault.",
                VaultOutcome.VaultFull => "Your Vault is full. List or withdraw something first.",
                VaultOutcome.NotInVault => "That item is not in your Vault. Use /vault list to see its id.",
                VaultOutcome.Listed => $"{item} is listed for sale. Delist it before you withdraw it.",
                VaultOutcome.Withdrawing => $"{item} is already being withdrawn.",
                VaultOutcome.NoPackSpace => $"You do not have room in your pack for {item}.",
                VaultOutcome.UniqueLimit => $"You cannot carry any more of {item}.",
                VaultOutcome.SaveFailed => $"The Vault could not save {item}. Nothing was changed.",

                _ => outcome.ToString(),
            };
        }
    }
}

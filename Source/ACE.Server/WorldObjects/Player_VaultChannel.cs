using System.Threading;

using ACE.Server.Market;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        private VaultChannel vaultChannel;

        /// <summary>
        /// The /vault deposit or withdrawal this player is channelling, or null
        /// </summary>
        public VaultChannel ActiveVaultChannel => Volatile.Read(ref vaultChannel);

        /// <summary>
        /// True while the player channels a Vault deposit or withdrawal. Checked everywhere PKLogout is, and it also blocks trading, giving, dropping and recalls.
        /// </summary>
        public bool IsVaultChannelling => ActiveVaultChannel != null;

        /// <summary>
        /// Cancels a Vault channel, if any. Called by the PK timer update (a landed player attack), death and logout.
        /// </summary>
        public void CancelVaultChannel()
        {
            if (IsVaultChannelling)
                VaultChannel.Cancel(this);
        }

        /// <summary>
        /// Swaps in a channel (or null) and returns the one there was. Atomic, so two cancels can't both end the same channel.
        /// </summary>
        internal VaultChannel TakeVaultChannel(VaultChannel channel)
        {
            return Interlocked.Exchange(ref vaultChannel, channel);
        }

        /// <summary>
        /// Ends the channel if it is still this one
        /// </summary>
        internal bool TryEndVaultChannel(VaultChannel channel)
        {
            return Interlocked.CompareExchange(ref vaultChannel, null, channel) == channel;
        }
    }
}

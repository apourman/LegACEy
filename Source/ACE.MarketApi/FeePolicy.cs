namespace ACE.MarketApi
{
    /// <summary>
    /// The sale a fee is quoted for
    /// </summary>
    /// <param name="Price">whole MMD for the whole stack</param>
    public sealed record Sale(uint SellerAccountId, uint BuyerAccountId, long ListingId, uint Wcid, int ItemType, int StackSize, long Price);

    /// <summary>
    /// The seller's fee for a sale. The purchase refuses a fee that isn't a whole MMD between 0 and the price.
    /// </summary>
    /// <param name="Reason">recorded on the fee's ledger entry</param>
    public sealed record SellerFee(decimal Amount, string Reason = null);

    /// <summary>
    /// Decides the seller's fee for each sale. The fee is taken from the seller and removed from the economy (the FEES system account).
    /// </summary>
    public interface IFeePolicy
    {
        SellerFee Quote(Sale sale);
    }

    /// <summary>
    /// No fees. The fee pair is still written on every sale, at 0.
    /// </summary>
    public sealed class ZeroFeePolicy : IFeePolicy
    {
        public SellerFee Quote(Sale sale) => new SellerFee(0);
    }
}

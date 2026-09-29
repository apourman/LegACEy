namespace ACE.Database.Market
{
    public enum MarketJobResult
    {
        Saved,
        Refused,
        Failed,
    }

    public readonly record struct NoteStack(uint Guid, int StackSize);
}

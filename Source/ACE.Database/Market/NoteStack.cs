namespace ACE.Database.Market
{
    /// <summary>
    /// A stack of trade notes taken out of a pack: its GUID and how many notes (MMD) it holds
    /// </summary>
    public readonly record struct NoteStack(uint Guid, int StackSize);
}

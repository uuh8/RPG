namespace Game.Combat
{
    public enum ElementReactionPhase : byte
    {
        Started = 0,
        Paused = 1,
        Resumed = 2,
        Resolved = 3,
        Cancelled = 4,
    }
}

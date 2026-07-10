namespace Game.Skills
{
    public enum CastTraceStepKind : byte
    {
        CastStarted = 0,
        NullSpellSkipped = 1,
        ModifyApplied = 2,
        MulticastApplied = 3,
        DrawBudgetBlocked = 4,
        EmitProduced = 5,
        PayloadCaptured = 6,
        ManaFizzle = 7,
        CastCompleted = 8
    }
}

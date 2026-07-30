namespace Game.Character
{
    /// <summary>
    /// Safe Zone 阶段的完全惰性状态：不移动、不转向、不查询 Utility，也不施法。
    /// </summary>
    internal sealed class BossInactiveState : BossStateBase
    {
        public override BossRuntimeStateKind Kind =>
            BossRuntimeStateKind.Inactive;

        public BossInactiveState(WizardBossController boss) : base(boss)
        {
        }
    }
}

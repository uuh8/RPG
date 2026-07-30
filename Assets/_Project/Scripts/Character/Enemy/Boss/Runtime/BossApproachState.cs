namespace Game.Character
{
    /// <summary>
    /// 只做 MoveTo：距离过远时逼近，进入 StopDistance 后停下。
    /// 没有 MoveAway 分支，因此永久索敌不等于普通 Ranged Enemy 的 Kite/Retreat。
    /// </summary>
    internal sealed class BossApproachState : BossStateBase
    {
        public override BossRuntimeStateKind Kind =>
            BossRuntimeStateKind.Approach;

        public BossApproachState(WizardBossController boss) : base(boss)
        {
        }

        public override void Tick(float deltaTime)
        {
            if (!Boss.HasTarget)
            {
                Boss.EnterInactive();
                return;
            }

            if (Boss.IsWithinStopDistance)
            {
                Boss.EnterDecision();
                return;
            }

            Boss.MoveTowardTarget(deltaTime);
        }
    }
}

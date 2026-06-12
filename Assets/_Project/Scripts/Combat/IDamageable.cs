namespace Game.Combat
{
    /// <summary>
    /// "可受伤害"统一契约，由 HealthComponent 实现。
    /// 命中判定方通过 ReceiveHit 提交攻击意图，实现方用自身防御档案结算。
    /// </summary>
    public interface IDamageable
    {
        /// <summary>所属阵营。命中判定据此跳过同阵营（含攻击者自身）。</summary>
        byte TeamId { get; }

        /// <summary>是否存活。已死目标不再受理命中。</summary>
        bool IsAlive { get; }

        /// <summary>受理一次命中。实现方负责结算、扣血并同帧派发事件。</summary>
        void ReceiveHit(in DamageRequest req);
    }
}

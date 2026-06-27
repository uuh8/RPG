using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 一次命中的攻击意图。攻击者数据按值快照，结算期间不回查攻击者对象，
    /// 规避"攻击者已销毁但伤害仍在结算"的空引用失败模式。
    /// </summary>
    public readonly struct DamageRequest
    {
        public readonly int AttackerId;
        public readonly byte AttackerTeam;
        public readonly float BaseAmount;   // 基础伤害
        public readonly DamageType Type;
        public readonly Vector3 HitPoint;
        public readonly Vector3 HitDirection;
        // 是否触发"受击反应"（受击动画 + 敌人硬直）。直击=true；DoT/环境跳伤(燃烧/火场)=false：
        // 仍正常扣血与发事件（可闪红/飘字），但不打断动作、不站桩。解决"持续伤害时角色一直播受击、无法移动"。
        public readonly bool TriggerHitReaction;

        public DamageRequest(int attackerId, byte attackerTeam, float baseAmount,
                             DamageType type, Vector3 hitPoint, Vector3 hitDirection,
                             bool triggerHitReaction = true)
        {
            AttackerId         = attackerId;
            AttackerTeam       = attackerTeam;
            BaseAmount         = baseAmount;
            Type               = type;
            HitPoint           = hitPoint;
            HitDirection       = hitDirection;
            TriggerHitReaction = triggerHitReaction;
        }
    }
}

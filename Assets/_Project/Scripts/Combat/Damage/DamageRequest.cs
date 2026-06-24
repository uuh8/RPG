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

        public DamageRequest(int attackerId, byte attackerTeam, float baseAmount,
                             DamageType type, Vector3 hitPoint, Vector3 hitDirection)
        {
            AttackerId   = attackerId;
            AttackerTeam = attackerTeam;
            BaseAmount   = baseAmount;
            Type         = type;
            HitPoint     = hitPoint;
            HitDirection = hitDirection;
        }
    }
}

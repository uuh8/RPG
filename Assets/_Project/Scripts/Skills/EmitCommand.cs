using UnityEngine;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// 求值器输出的一条"该产出什么"的纯数据。运行时 SpellCaster（阶段 B）据此 Instantiate 投射物并调 ProjectileBase.Init。
    /// 数值已是"基础值×修正快照"的最终结果，SpellCaster 不再二次计算。
    /// </summary>
    public readonly struct EmitCommand
    {
        public readonly GameObject ProjectilePrefab; // 要生成的投射物预制体（来自 emit 法术）
        public readonly float Damage;                // 最终伤害
        public readonly float Speed;                 // 最终速度
        public readonly DamageType DamageType;       // 伤害类型
        public readonly float SpreadDegrees;         // 散射角度（SpellCaster 据此把多发打散成扇形）

        public EmitCommand(GameObject projectilePrefab, float damage, float speed, DamageType damageType, float spreadDegrees)
        {
            ProjectilePrefab = projectilePrefab;
            Damage = damage;
            Speed = speed;
            DamageType = damageType;
            SpreadDegrees = spreadDegrees;
        }
    }
}

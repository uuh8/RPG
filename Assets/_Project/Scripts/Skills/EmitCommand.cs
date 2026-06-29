using System.Collections.Generic;
using UnityEngine;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// 求值器输出的一条"该产出什么"的纯数据。运行时 SpellCaster 据此 Instantiate 投射物并调 ProjectileBase.Init。
    /// 数值已是"基础值×修正快照"的最终结果。触发投射物额外带"载荷"：命中时再施放的法术后缀 + 其起始修正快照。
    /// </summary>
    public readonly struct EmitCommand
    {
        public readonly GameObject ProjectilePrefab; // 要生成的投射物预制体
        public readonly float Damage;                // 最终伤害
        public readonly float Speed;                 // 最终速度
        public readonly DamageType DamageType;       // 伤害类型
        public readonly float SpreadDegrees;         // 散射角度
        public readonly AudioClip CastSfx;           // 施放音效（一次施法去重）
        public readonly IReadOnlyList<SpellDefinition> Payload; // 触发载荷：命中时施放的法术后缀；非触发为 null
        public readonly CastModifierState PayloadMods;          // 载荷的起始修正快照（继承触发当时的加成）

        public bool HasPayload => Payload != null && Payload.Count > 0;

        public EmitCommand(GameObject projectilePrefab, float damage, float speed, DamageType damageType,
                           float spreadDegrees, AudioClip castSfx,
                           IReadOnlyList<SpellDefinition> payload, CastModifierState payloadMods)
        {
            ProjectilePrefab = projectilePrefab;
            Damage = damage;
            Speed = speed;
            DamageType = damageType;
            SpreadDegrees = spreadDegrees;
            CastSfx = castSfx;
            Payload = payload;
            PayloadMods = payloadMods;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// 求值器输出的一条“应该产出什么”的纯数据命令。运行时 SpellCaster 据此实例化投射物并调用 ProjectileBase.Init。
    /// 数值已经是基础值 + 修正快照的最终结果。携带 payload 的投射物额外记录 payload 的触发条件与起始修正快照。
    /// </summary>
    public readonly struct EmitCommand
    {
        public readonly GameObject ProjectilePrefab;
        public readonly float Damage;
        public readonly float Speed;
        public readonly DamageType DamageType;
        public readonly float SpreadDegrees;
        public readonly AudioClip CastSfx;
        public readonly IReadOnlyList<SpellDefinition> Payload;
        public readonly CastModifierState PayloadMods;
        public readonly PayloadTriggerMode PayloadTrigger;
        public readonly float PayloadDelaySeconds;

        public bool HasPayload => Payload != null && Payload.Count > 0;

        public EmitCommand(GameObject projectilePrefab, float damage, float speed, DamageType damageType,
                           float spreadDegrees, AudioClip castSfx,
                           IReadOnlyList<SpellDefinition> payload, CastModifierState payloadMods,
                           PayloadTriggerMode payloadTrigger, float payloadDelaySeconds)
        {
            ProjectilePrefab = projectilePrefab;
            Damage = damage;
            Speed = speed;
            DamageType = damageType;
            SpreadDegrees = spreadDegrees;
            CastSfx = castSfx;
            Payload = payload;
            PayloadMods = payloadMods;
            PayloadTrigger = payloadTrigger;
            PayloadDelaySeconds = payloadDelaySeconds;
        }
    }
}

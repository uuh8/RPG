using System.Collections.Generic;
using UnityEngine;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// CastEvaluator 输出的一条只读攻击命令，是 Game.Skills 与 Unity Runtime 之间的数据边界。
    /// 它不含执行方法：SpellCaster 只消费这些已经烘焙好的最终值，不再重读或修改原始法术资产。
    /// readonly struct 保证字段在构造后不可改；其中 Prefab、AudioClip、Payload 仍是只读引用，不代表深拷贝资源。
    /// </summary>
    public readonly struct EmitCommand
    {
        // ── 生成数据：Runtime 应创建哪个 Prefab、采用哪种空间生成策略 ──
        public readonly GameObject ProjectilePrefab;
        public readonly SpellSpawnMode SpawnMode;
        public readonly GameObject LandingSitePrefab;
        public readonly float SkyfallHeight;
        public readonly float SkyfallBackOffset;
        public readonly float LandingSiteDuration;
        public readonly int ShieldReflectCount;

        // ── 战斗结果快照：由基础数据和 CastModifierState 在 BakeEmit 时合并 ──
        public readonly float Damage;
        public readonly float ExplosionDamage;
        public readonly float FireFieldDamagePerTick;
        public readonly float FireFieldTickInterval;
        public readonly float FireFieldDuration;
        public readonly float Speed;
        public readonly DamageType DamageType;

        // ── 飞行与运动快照：SpellCaster 注入 ProjectileBase/Rigidbody ──
        public readonly float SpreadDegrees;
        public readonly int BounceCount;
        public readonly bool UseGravity;
        public readonly float HomingRadius;
        public readonly float HomingDuration;
        public readonly float HomingTurnRateDegrees;
        public readonly float OrbitRadius;
        public readonly float OrbitAngularSpeedDegrees;
        public readonly float OrbitPhaseOffsetDegrees;
        public readonly float OrbitPlaneTiltDegrees;
        public readonly ProjectileMotionMode MotionMode;

        // ── 表现与后续触发：Payload 是触发时才重新求值的有序子序列 ──
        public readonly AudioClip CastSfx;
        public readonly IReadOnlyList<SpellDefinition> Payload;
        public readonly CastModifierState PayloadMods;
        public readonly PayloadTriggerMode PayloadTrigger;
        public readonly float PayloadDelaySeconds;

        /// <summary>同时检查引用与元素数量，空 Payload 会退化为普通 Emit，不建立无意义的事件订阅。</summary>
        public bool HasPayload => Payload != null && Payload.Count > 0;

        /// <summary>由 CastEvaluator.BakeEmit 唯一构造；把一次求值结果完整封装后交给 Runtime。</summary>
        public EmitCommand(GameObject projectilePrefab, SpellSpawnMode spawnMode, GameObject landingSitePrefab,
                           float skyfallHeight, float skyfallBackOffset, float landingSiteDuration,
                           int shieldReflectCount,
                           float damage, float explosionDamage,
                           float fireFieldDamagePerTick, float fireFieldTickInterval, float fireFieldDuration,
                           float speed, DamageType damageType,
                           float spreadDegrees, int bounceCount, bool useGravity,
                           float homingRadius, float homingDuration, float homingTurnRateDegrees,
                           float orbitRadius, float orbitAngularSpeedDegrees, float orbitPhaseOffsetDegrees,
                           float orbitPlaneTiltDegrees,
                           ProjectileMotionMode motionMode,
                           AudioClip castSfx,
                           IReadOnlyList<SpellDefinition> payload, CastModifierState payloadMods,
                           PayloadTriggerMode payloadTrigger, float payloadDelaySeconds)
        {
            ProjectilePrefab = projectilePrefab;
            SpawnMode = spawnMode;
            LandingSitePrefab = landingSitePrefab;
            SkyfallHeight = skyfallHeight;
            SkyfallBackOffset = skyfallBackOffset;
            LandingSiteDuration = landingSiteDuration;
            ShieldReflectCount = shieldReflectCount;
            Damage = damage;
            ExplosionDamage = explosionDamage;
            FireFieldDamagePerTick = fireFieldDamagePerTick;
            FireFieldTickInterval = fireFieldTickInterval;
            FireFieldDuration = fireFieldDuration;
            Speed = speed;
            DamageType = damageType;
            SpreadDegrees = spreadDegrees;
            BounceCount = bounceCount;
            UseGravity = useGravity;
            HomingRadius = homingRadius;
            HomingDuration = homingDuration;
            HomingTurnRateDegrees = homingTurnRateDegrees;
            OrbitRadius = orbitRadius;
            OrbitAngularSpeedDegrees = orbitAngularSpeedDegrees;
            OrbitPhaseOffsetDegrees = orbitPhaseOffsetDegrees;
            OrbitPlaneTiltDegrees = orbitPlaneTiltDegrees;
            MotionMode = motionMode;
            CastSfx = castSfx;
            Payload = payload;
            PayloadMods = payloadMods;
            PayloadTrigger = payloadTrigger;
            PayloadDelaySeconds = payloadDelaySeconds;
        }
    }
}

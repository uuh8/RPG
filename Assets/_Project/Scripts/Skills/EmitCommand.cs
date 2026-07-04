using System.Collections.Generic;
using UnityEngine;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// CastEvaluator 输出的一条纯数据发射命令。SpellCaster 根据它实例化真实投射物。
    /// </summary>
    public readonly struct EmitCommand
    {
        public readonly GameObject ProjectilePrefab;
        public readonly SpellSpawnMode SpawnMode;
        public readonly GameObject LandingSitePrefab;
        public readonly float SkyfallHeight;
        public readonly float SkyfallBackOffset;
        public readonly float LandingSiteDuration;
        public readonly float Damage;
        public readonly float Speed;
        public readonly DamageType DamageType;
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
        public readonly AudioClip CastSfx;
        public readonly IReadOnlyList<SpellDefinition> Payload;
        public readonly CastModifierState PayloadMods;
        public readonly PayloadTriggerMode PayloadTrigger;
        public readonly float PayloadDelaySeconds;

        public bool HasPayload => Payload != null && Payload.Count > 0;

        public EmitCommand(GameObject projectilePrefab, SpellSpawnMode spawnMode, GameObject landingSitePrefab,
                           float skyfallHeight, float skyfallBackOffset, float landingSiteDuration,
                           float damage, float speed, DamageType damageType,
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
            Damage = damage;
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

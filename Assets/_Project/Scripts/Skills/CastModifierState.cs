using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 互斥的投射物主运动控制器。后出现的冲突类 Modify 会覆盖前一个模式。
    /// </summary>
    public enum ProjectileMotionMode : byte
    {
        None = 0,
        Homing = 1,
        Orbit = 2
    }

    /// <summary>
    /// 求值过程中当前累积的修正状态。解释器从左到右读取 Modify 法术时更新它，
    /// 产出投射物时把它快照进 EmitCommand。readonly struct 保持值传递和低开销。
    /// </summary>
    public readonly struct CastModifierState
    {
        public readonly float DamageAddFlat;
        public readonly float DamageMul;
        public readonly float SpeedMul;
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

        public CastModifierState(float damageAddFlat, float damageMul, float speedMul, float spreadDegrees,
                                 int bounceCount, bool useGravity,
                                 float homingRadius, float homingDuration, float homingTurnRateDegrees,
                                 float orbitRadius, float orbitAngularSpeedDegrees, float orbitPhaseOffsetDegrees,
                                 float orbitPlaneTiltDegrees, ProjectileMotionMode motionMode)
        {
            DamageAddFlat = damageAddFlat;
            DamageMul = damageMul;
            SpeedMul = speedMul;
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
        }

        public static CastModifierState Default => new CastModifierState(0f, 1f, 1f, 0f, 0, false, 0f, 0f, 0f, 0f, 0f, 0f, 0f, ProjectileMotionMode.None);

        public CastModifierState Apply(SpellDefinition modify)
        {
            ProjectileMotionMode motionMode = ResolveMotionMode(modify);
            return new CastModifierState(
                DamageAddFlat + modify.ModDamageAddFlat,
                DamageMul * modify.ModDamageMul,
                SpeedMul * modify.ModSpeedMul,
                SpreadDegrees + modify.ModSpreadAddDegrees,
                BounceCount + modify.ModBounceAdd,
                UseGravity || modify.ModUseGravity,
                Mathf.Max(HomingRadius, modify.ModHomingRadius),
                HomingDuration + modify.ModHomingDuration,
                HomingTurnRateDegrees + modify.ModHomingTurnRateDegrees,
                Mathf.Max(OrbitRadius, modify.ModOrbitRadius),
                OrbitAngularSpeedDegrees + modify.ModOrbitAngularSpeedDegrees,
                OrbitPhaseOffsetDegrees + modify.ModOrbitPhaseOffsetDegrees,
                Mathf.Abs(modify.ModOrbitPlaneTiltDegrees) > Mathf.Abs(OrbitPlaneTiltDegrees)
                    ? modify.ModOrbitPlaneTiltDegrees
                    : OrbitPlaneTiltDegrees,
                motionMode);
        }

        private ProjectileMotionMode ResolveMotionMode(SpellDefinition modify)
        {
            ProjectileMotionMode mode = MotionMode;

            bool enablesHoming = modify.ModHomingRadius > 0f
                && modify.ModHomingDuration > 0f
                && modify.ModHomingTurnRateDegrees > 0f;
            if (enablesHoming)
                mode = ProjectileMotionMode.Homing;

            bool enablesOrbit = modify.ModOrbitRadius > 0f
                && !Mathf.Approximately(modify.ModOrbitAngularSpeedDegrees, 0f);
            if (enablesOrbit)
                mode = ProjectileMotionMode.Orbit;

            return mode;
        }
    }
}

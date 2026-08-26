using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 互斥的投射物主运动模式。追踪与轨道都需要接管主要方向，因此后出现的有效 Modify 覆盖前一个模式。
    /// </summary>
    public enum ProjectileMotionMode : byte
    {
        None = 0,
        Homing = 1,
        Orbit = 2
    }

    /// <summary>
    /// 一次 CastEvaluator 调用中的修正快照。解释器持有一个局部值，读到 Modify 时用 Apply 返回新值，
    /// 读到 Emit 时把当前值复制进 EmitCommand；它从不回写共享的 SpellDefinition 资产。
    /// readonly struct 的字段构造后不可变，值复制也避免多个施法层共享同一个可变 Modifier 对象。
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

        // 恒等状态：加法项为 0、乘法项为 1、能力开关关闭，应用后不会改变基础投射物。
        public static CastModifierState Default => new CastModifierState(
            0f,
            1f,
            1f,
            0f,
            0,
            false,
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            0f,
            ProjectileMotionMode.None);

        /// <summary>
        /// 把一条 Modify 指令合并到当前快照并返回新快照。加法、乘法和 bool OR 分别表达不同叠加语义；
        /// 调用方必须接住返回值，因为 readonly struct 不会原地修改自身。
        /// </summary>
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

            // Approximately 用带容差的浮点比较代替 == 0，避免 Inspector 浮点误差把“近似零”当成有效角速度。
            bool enablesOrbit = modify.ModOrbitRadius > 0f
                && !Mathf.Approximately(modify.ModOrbitAngularSpeedDegrees, 0f);
            if (enablesOrbit)
                mode = ProjectileMotionMode.Orbit;

            return mode;
        }
    }
}

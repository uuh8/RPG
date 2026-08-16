using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 初始化时从 LiquidSimulationProfile 复制出的不可变 Runtime Snapshot。
    /// PBF Tick/Substep 只读取这个值类型，避免在热路径反复访问可由 Inspector 修改的 ScriptableObject，
    /// 也保证同一轮模拟不会在中途混入两套参数。
    /// </summary>
    public readonly struct LiquidSimulationSettings
    {
        public readonly int ParticleCapacity;
        public readonly int HashTableCapacity;
        public readonly int MaxSpawnRequests;
        public readonly int MaxFluidColliders;
        public readonly uint AmountUnitsPerParticle;
        public readonly float ParticleRadius;
        public readonly float ParticleMass;
        public readonly float SmoothingRadius;
        public readonly float RestDensity;
        public readonly float FixedTickRate;
        public readonly int Substeps;
        public readonly int SolverIterations;
        public readonly float MaximumPositionCorrection;
        public readonly float LambdaEpsilon;
        public readonly float ArtificialPressure;
        public readonly float Viscosity;
        public readonly float Vorticity;
        public readonly Vector3 Gravity;
        public readonly float MaxSpeed;
        public readonly float CollisionFriction;
        public readonly float CollisionRestitution;
        public readonly float SleepThreshold;
        public readonly float SleepDensityErrorThreshold;
        public readonly int SleepAfterStableTicks;
        public readonly int MaxCatchUpTicks;

        public LiquidSimulationSettings(
            int particleCapacity,
            int maxSpawnRequests,
            int maxFluidColliders,
            int amountUnitsPerParticle,
            float particleRadius,
            float particleMass,
            float smoothingRadius,
            float restDensity,
            float fixedTickRate,
            int substeps,
            int solverIterations,
            float maximumPositionCorrection,
            float lambdaEpsilon,
            float artificialPressure,
            float viscosity,
            float vorticity,
            Vector3 gravity,
            float maxSpeed,
            float collisionFriction,
            float collisionRestitution,
            float sleepThreshold,
            int maxCatchUpTicks,
            float sleepDensityErrorThreshold = 0.02f,
            int sleepAfterStableTicks = 30)
        {
            ParticleCapacity = FluidCapacityPlanner.NormalizeParticleCapacity(particleCapacity);
            HashTableCapacity = FluidCapacityPlanner.CalculateHashTableCapacity(ParticleCapacity);
            MaxSpawnRequests = RequirePositive(maxSpawnRequests, nameof(maxSpawnRequests));
            MaxFluidColliders = RequirePositive(maxFluidColliders, nameof(maxFluidColliders));
            AmountUnitsPerParticle = (uint)RequirePositive(
                amountUnitsPerParticle,
                nameof(amountUnitsPerParticle));
            ParticleRadius = RequirePositiveFinite(particleRadius, nameof(particleRadius));
            ParticleMass = RequirePositiveFinite(particleMass, nameof(particleMass));
            SmoothingRadius = RequirePositiveFinite(smoothingRadius, nameof(smoothingRadius));
            RestDensity = RequirePositiveFinite(restDensity, nameof(restDensity));
            FixedTickRate = RequirePositiveFinite(fixedTickRate, nameof(fixedTickRate));
            Substeps = RequirePositive(substeps, nameof(substeps));
            SolverIterations = RequirePositive(solverIterations, nameof(solverIterations));
            MaximumPositionCorrection = RequirePositiveFinite(
                maximumPositionCorrection,
                nameof(maximumPositionCorrection));
            if (MaximumPositionCorrection >= SmoothingRadius)
                throw new ArgumentOutOfRangeException(nameof(maximumPositionCorrection));
            LambdaEpsilon = RequirePositiveFinite(lambdaEpsilon, nameof(lambdaEpsilon));
            ArtificialPressure = RequireNonNegativeFinite(
                artificialPressure,
                nameof(artificialPressure));
            Viscosity = RequireUnitInterval(viscosity, nameof(viscosity));
            Vorticity = RequireNonNegativeFinite(vorticity, nameof(vorticity));
            Gravity = RequireFinite(gravity, nameof(gravity));
            MaxSpeed = RequirePositiveFinite(maxSpeed, nameof(maxSpeed));
            float maximumSubstepDisplacement = MaxSpeed / (FixedTickRate * Substeps)
                + SolverIterations * MaximumPositionCorrection;
            float boundedSweepCoverage = ParticleRadius * FluidCollisionSweep.MaxSamples;
            if (maximumSubstepDisplacement > boundedSweepCoverage)
                throw new ArgumentOutOfRangeException(nameof(maxSpeed));
            CollisionFriction = RequireUnitInterval(collisionFriction, nameof(collisionFriction));
            CollisionRestitution = RequireUnitInterval(
                collisionRestitution,
                nameof(collisionRestitution));
            SleepThreshold = RequireNonNegativeFinite(sleepThreshold, nameof(sleepThreshold));
            SleepDensityErrorThreshold = RequireNonNegativeFinite(
                sleepDensityErrorThreshold,
                nameof(sleepDensityErrorThreshold));
            if (SleepDensityErrorThreshold >= 1f)
                throw new ArgumentOutOfRangeException(nameof(sleepDensityErrorThreshold));
            SleepAfterStableTicks = RequirePositive(
                sleepAfterStableTicks,
                nameof(sleepAfterStableTicks));
            MaxCatchUpTicks = FluidSimulationClock.ValidateMaxCatchUpTicks(maxCatchUpTicks);
        }

        private static int RequirePositive(int value, string parameterName)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(parameterName);

            return value;
        }

        private static float RequirePositiveFinite(float value, string parameterName)
        {
            if (value <= 0f || float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);

            return value;
        }

        private static float RequireNonNegativeFinite(float value, string parameterName)
        {
            if (value < 0f || float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);

            return value;
        }

        private static float RequireUnitInterval(float value, string parameterName)
        {
            if (value < 0f || value > 1f || float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);

            return value;
        }

        private static Vector3 RequireFinite(Vector3 value, string parameterName)
        {
            if (float.IsNaN(value.x) || float.IsInfinity(value.x)
                || float.IsNaN(value.y) || float.IsInfinity(value.y)
                || float.IsNaN(value.z) || float.IsInfinity(value.z))
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }

            return value;
        }
    }
}

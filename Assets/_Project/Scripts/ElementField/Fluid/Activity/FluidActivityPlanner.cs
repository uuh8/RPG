using System;

namespace Game.ElementField
{
    /// <summary>
    /// Metadata.y 的 Activity Bit Contract。Alive 管数据寿命，InterestActive 管可见/邻域参与，
    /// RequiresSimulation 管 Solver target，Sleeping 表示仍可见但暂不作为 target 推进。
    /// </summary>
    public static class FluidActivityFlags
    {
        public const uint Alive = 1u << 0;
        public const uint InterestActive = 1u << 1;
        public const uint RequiresSimulation = 1u << 2;
        public const uint Sleeping = 1u << 3;

        public static bool IsAlive(uint flags) => (flags & Alive) != 0u;
        public static bool RequiresSolver(uint flags) =>
            (flags & (Alive | InterestActive | RequiresSimulation))
            == (Alive | InterestActive | RequiresSimulation)
            && (flags & Sleeping) == 0u;
        public static bool ContributesToSurface(uint flags) =>
            (flags & (Alive | InterestActive)) == (Alive | InterestActive);
        public static bool IsSleeping(uint flags) =>
            ContributesToSurface(flags) && (flags & Sleeping) != 0u;
    }

    public readonly struct FluidActivityTransition
    {
        public FluidActivityTransition(uint flags, uint stableTicks)
        {
            Flags = flags;
            StableTicks = stableTicks;
        }

        public uint Flags { get; }
        public uint StableTicks { get; }
    }

    /// <summary>与 GPU Kernel 对齐的 Pure 状态迁移与 Indirect Dispatch 规划。</summary>
    public static class FluidActivityPlanner
    {
        public static FluidActivityTransition Evaluate(
            uint currentFlags,
            uint currentStableTicks,
            bool insideInterest,
            bool isStable,
            bool wakeRequested,
            uint requiredStableTicks)
        {
            if (!FluidActivityFlags.IsAlive(currentFlags))
                return default;
            if (!insideInterest)
                return new FluidActivityTransition(FluidActivityFlags.Alive, 0u);

            bool wasInside = (currentFlags & FluidActivityFlags.InterestActive) != 0u;
            if (!wasInside || wakeRequested)
            {
                return new FluidActivityTransition(
                    FluidActivityFlags.Alive
                    | FluidActivityFlags.InterestActive
                    | FluidActivityFlags.RequiresSimulation,
                    0u);
            }

            if ((currentFlags & FluidActivityFlags.Sleeping) != 0u)
            {
                return new FluidActivityTransition(
                    FluidActivityFlags.Alive
                    | FluidActivityFlags.InterestActive
                    | FluidActivityFlags.Sleeping,
                    currentStableTicks);
            }

            uint stableTicks = isStable
                ? currentStableTicks == uint.MaxValue ? uint.MaxValue : currentStableTicks + 1u
                : 0u;
            if (isStable && stableTicks >= Math.Max(1u, requiredStableTicks))
            {
                return new FluidActivityTransition(
                    FluidActivityFlags.Alive
                    | FluidActivityFlags.InterestActive
                    | FluidActivityFlags.Sleeping,
                    stableTicks);
            }

            return new FluidActivityTransition(
                FluidActivityFlags.Alive
                | FluidActivityFlags.InterestActive
                | FluidActivityFlags.RequiresSimulation,
                stableTicks);
        }

        public static int CalculateSolverThreadGroups(
            uint awakeCount,
            int particleCapacity,
            int threadsPerGroup)
        {
            if (particleCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(particleCapacity));
            if (threadsPerGroup <= 0)
                throw new ArgumentOutOfRangeException(nameof(threadsPerGroup));
            // slot 并不 compact：只要有一个 awake，就必须覆盖完整 capacity，不能用 AwakeCount 算 groups。
            return awakeCount == 0u
                ? 0
                : (particleCapacity + threadsPerGroup - 1) / threadsPerGroup;
        }
    }
}

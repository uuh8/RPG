using System;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// Density Texture 的纯数据布局。Resolution 表示 Lattice Sample 数，所以真正的 Voxel Cell 数
    /// 是 Resolution-1；WorldOrigin 是 sample(0,0,0) 的世界坐标，而不是 Texture 中心。
    /// </summary>
    public readonly struct FluidSurfaceGridSettings
    {
        public readonly Vector3Int Resolution;
        public readonly Vector3 WorldOrigin;
        public readonly Vector3 VoxelSize;
        public readonly int MaximumTriangleCount;
        public readonly long SampleCount;
        public readonly long CellCount;
        public readonly long MaximumCellTriangleCount;
        public readonly long TriangleBufferSizeInBytes;

        public FluidSurfaceGridSettings(
            Vector3Int resolution,
            Vector3 worldOrigin,
            Vector3 voxelSize,
            int maximumTriangleCount)
        {
            if (resolution.x < 2 || resolution.y < 2 || resolution.z < 2)
                throw new ArgumentOutOfRangeException(nameof(resolution));
            RequireFinite(worldOrigin, nameof(worldOrigin));
            RequirePositiveFinite(voxelSize, nameof(voxelSize));
            if (maximumTriangleCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumTriangleCount));

            Resolution = resolution;
            WorldOrigin = worldOrigin;
            VoxelSize = voxelSize;
            MaximumTriangleCount = maximumTriangleCount;

            // 先转 long 再乘，避免 int 在进入 checked 前已经溢出。
            SampleCount = checked((long)resolution.x * resolution.y * resolution.z);
            CellCount = checked(
                (long)(resolution.x - 1) * (resolution.y - 1) * (resolution.z - 1));
            MaximumCellTriangleCount = checked(CellCount * 5L);
            TriangleBufferSizeInBytes = checked((long)maximumTriangleCount * 96L);
        }

        public Bounds WorldBounds
        {
            get
            {
                Vector3 size = Vector3.Scale(VoxelSize, Resolution - Vector3Int.one);
                return new Bounds(WorldOrigin + size * 0.5f, size);
            }
        }

        /// <summary>
        /// Origin 不参与 GPU allocation：相同 Sample 形状、实际 Voxel Size 与 Triangle Capacity
        /// 只需更新 World-to-Grid 参数，无需销毁 Texture/Buffer。
        /// </summary>
        public bool IsResourceCompatibleWith(in FluidSurfaceGridSettings other)
        {
            return Resolution == other.Resolution
                && VoxelSize == other.VoxelSize
                && MaximumTriangleCount == other.MaximumTriangleCount;
        }

        private static void RequireFinite(Vector3 value, string parameterName)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void RequirePositiveFinite(Vector3 value, string parameterName)
        {
            if (value.x <= 0f || value.y <= 0f || value.z <= 0f)
                throw new ArgumentOutOfRangeException(parameterName);
            RequireFinite(value, parameterName);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>
    /// 把世界 Bounds 与目标 Voxel Size 转换为有限 3D Lattice。Ceiling 保证采样间距不会比目标更粗；
    /// 达到硬 Resolution 上限后改算实际 Voxel Size，仍精确覆盖整个 padded Bounds。
    /// </summary>
    public static class FluidSurfaceGridPlanner
    {
        public static FluidSurfaceGridSettings Plan(
            Bounds interestBounds,
            in LiquidRenderSettings settings)
        {
            Vector3 center = interestBounds.center;
            Vector3 size = interestBounds.size;
            RequireFinite(center, nameof(interestBounds));
            RequirePositiveFinite(size, nameof(interestBounds));

            Vector3 paddedSize = size + Vector3.one * (settings.BoundsPadding * 2f);
            RequirePositiveFinite(paddedSize, nameof(interestBounds));
            Vector3 worldOrigin = interestBounds.min - Vector3.one * settings.BoundsPadding;
            RequireFinite(worldOrigin, nameof(interestBounds));

            var resolution = new Vector3Int(
                CalculateResolution(paddedSize.x, in settings),
                CalculateResolution(paddedSize.y, in settings),
                CalculateResolution(paddedSize.z, in settings));
            var actualVoxelSize = new Vector3(
                paddedSize.x / (resolution.x - 1),
                paddedSize.y / (resolution.y - 1),
                paddedSize.z / (resolution.z - 1));

            return new FluidSurfaceGridSettings(
                resolution,
                worldOrigin,
                actualVoxelSize,
                settings.MaximumTriangleCount);
        }

        private static int CalculateResolution(float paddedAxisSize, in LiquidRenderSettings settings)
        {
            double cellCount = Math.Ceiling((double)paddedAxisSize / settings.TargetVoxelSize);
            if (double.IsNaN(cellCount)
                || double.IsInfinity(cellCount)
                || cellCount > int.MaxValue - 1d)
            {
                throw new OverflowException("Surface Grid resolution exceeds Int32 capacity.");
            }

            int sampleCount = checked((int)cellCount + 1);
            sampleCount = Math.Max(2, sampleCount);
            return Math.Min(sampleCount, settings.MaximumResolutionPerAxis);
        }

        private static void RequireFinite(Vector3 value, string parameterName)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void RequirePositiveFinite(Vector3 value, string parameterName)
        {
            if (value.x <= 0f || value.y <= 0f || value.z <= 0f)
                throw new ArgumentOutOfRangeException(parameterName);
            RequireFinite(value, parameterName);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

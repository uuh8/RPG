using System;
using Unity.Collections;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 单个 float4 同时携带 Position.xyz 与状态 tag，保证一次 Readback 原子取得位置、active 和材质。
    /// tag=0 表示 inactive；其余值为 materialId+1，避免 Water/Empty 与 inactive 混淆。
    /// </summary>
    internal static class FluidGameplaySampleCodec
    {
        internal const int Stride = FluidGpuLayout.Float4Stride;

        internal static float EncodeTag(bool active, ElementMaterialKind materialKind)
        {
            return active ? (byte)materialKind + 1f : 0f;
        }

        internal static bool TryDecodeMaterial(float tag, out ElementMaterialKind materialKind)
        {
            int encoded = Mathf.RoundToInt(tag);
            if (encoded <= 0 || Mathf.Abs(tag - encoded) > 0.001f)
            {
                materialKind = ElementMaterialKind.Empty;
                return false;
            }

            materialKind = (ElementMaterialKind)(encoded - 1);
            return true;
        }
    }

    /// <summary>
    /// Readback 发起瞬间冻结的坐标与版本信息。完成时必须连同 data 一起发布，不能拿完成帧的新 Bounds
    /// 去解释旧 GPU 样本，否则移动 Interest Bounds 时会把水映射到错误 Global Cell。
    /// </summary>
    internal readonly struct FluidGameplayReadbackMetadata
    {
        internal readonly Bounds Bounds;
        internal readonly Vector3 Origin;
        internal readonly float CellSize;
        internal readonly int ParticleCapacity;
        internal readonly uint AmountUnitsPerParticle;
        internal readonly uint LayoutVersion;
        internal readonly uint TopologyVersion;
        internal readonly uint SnapshotVersion;

        internal FluidGameplayReadbackMetadata(
            Bounds bounds,
            Vector3 origin,
            float cellSize,
            int particleCapacity,
            uint amountUnitsPerParticle,
            uint layoutVersion,
            uint topologyVersion,
            uint snapshotVersion)
        {
            Bounds = bounds;
            Origin = origin;
            CellSize = cellSize;
            ParticleCapacity = particleCapacity;
            AmountUnitsPerParticle = amountUnitsPerParticle;
            LayoutVersion = layoutVersion;
            TopologyVersion = topologyVersion;
            SnapshotVersion = snapshotVersion;
        }
    }

    internal static class FluidGameplayMetadataValidator
    {
        internal static bool IsValid(in FluidGameplayReadbackMetadata metadata)
        {
            Vector3 center = metadata.Bounds.center;
            Vector3 size = metadata.Bounds.size;
            Vector3 minimum = metadata.Bounds.min;
            Vector3 maximum = metadata.Bounds.max;
            return metadata.ParticleCapacity > 0
                && metadata.AmountUnitsPerParticle > 0
                && IsPositiveFinite(metadata.CellSize)
                && IsFinite(metadata.Origin)
                && IsFinite(center)
                && IsFinite(size)
                && IsFinite(minimum)
                && IsFinite(maximum)
                && size.x > 0f
                && size.y > 0f
                && size.z > 0f;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsPositiveFinite(float value)
        {
            return value > 0f && IsFinite(value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>
    /// 预分配 Open Addressing Table。按 GPU slot 0..capacity-1 固定顺序 Binning，
    /// 不使用 Dictionary/LINQ，因此低频发布也不会产生 GC Alloc，且累加结果可重复。
    /// </summary>
    internal sealed class LiquidGameplaySnapshot : ILiquidOccupancyReadOnly
    {
        private readonly Vector3Int[] _cellKeys;
        private readonly byte[] _amounts;
        private readonly byte[] _occupied;
        private readonly int _slotMask;
        private Vector3Int _minimumCell;
        private Vector3Int _maximumCell;

        internal LiquidGameplaySnapshot(int maximumCellCount)
        {
            if (maximumCellCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumCellCount));

            int slotCount = 2;
            while (slotCount < maximumCellCount * 2)
                slotCount <<= 1;

            _cellKeys = new Vector3Int[slotCount];
            _amounts = new byte[slotCount];
            _occupied = new byte[slotCount];
            _slotMask = slotCount - 1;
        }

        public bool HasValidSnapshot { get; private set; }
        public Bounds SnapshotBounds { get; private set; }
        internal uint LayoutVersion { get; private set; }
        internal uint TopologyVersion { get; private set; }
        public uint SnapshotVersion { get; private set; }
        public uint AmountUnitsPerParticle { get; private set; }

        internal bool TryRebuild(
            NativeArray<Vector4> samples,
            in FluidGameplayReadbackMetadata metadata)
        {
            if (!samples.IsCreated
                || samples.Length > metadata.ParticleCapacity
                || !FluidGameplayMetadataValidator.IsValid(in metadata))
            {
                return false;
            }

            Array.Clear(_occupied, 0, _occupied.Length);
            Array.Clear(_amounts, 0, _amounts.Length);

            Vector3 boundsMin = metadata.Bounds.min;
            Vector3 boundsMax = metadata.Bounds.max;
            for (int particleSlot = 0; particleSlot < samples.Length; particleSlot++)
            {
                Vector4 sample = samples[particleSlot];
                if (!FluidGameplaySampleCodec.TryDecodeMaterial(sample.w, out ElementMaterialKind material)
                    || material != ElementMaterialKind.Water
                    || !IsFinite(sample.x)
                    || !IsFinite(sample.y)
                    || !IsFinite(sample.z))
                {
                    continue;
                }

                // Bounds 使用 half-open [min,max)：落在 max 面上的粒子属于相邻区域，不能双重计数。
                if (sample.x < boundsMin.x || sample.y < boundsMin.y || sample.z < boundsMin.z
                    || sample.x >= boundsMax.x || sample.y >= boundsMax.y || sample.z >= boundsMax.z)
                {
                    continue;
                }

                Vector3Int cell = ElementWorldCoordinates.WorldToGlobalCell(
                    new Vector3(sample.x, sample.y, sample.z),
                    metadata.Origin,
                    metadata.CellSize);
                Accumulate(cell, metadata.AmountUnitsPerParticle);
            }

            float inset = Mathf.Min(0.0001f, metadata.CellSize * 0.001f);
            _minimumCell = ElementWorldCoordinates.WorldToGlobalCell(
                boundsMin, metadata.Origin, metadata.CellSize);
            _maximumCell = ElementWorldCoordinates.WorldToGlobalCell(
                boundsMax - Vector3.one * inset, metadata.Origin, metadata.CellSize);

            // data、Bounds 与版本最后一起切为 valid；Bridge 只交换完整构建的 Snapshot 引用。
            SnapshotBounds = metadata.Bounds;
            LayoutVersion = metadata.LayoutVersion;
            TopologyVersion = metadata.TopologyVersion;
            SnapshotVersion = metadata.SnapshotVersion;
            AmountUnitsPerParticle = metadata.AmountUnitsPerParticle;
            HasValidSnapshot = true;
            return true;
        }

        public bool TryGetAmount(
            Vector3Int globalCell,
            ElementMaterialKind materialKind,
            out byte amount)
        {
            if (!HasValidSnapshot
                || materialKind != ElementMaterialKind.Water
                || globalCell.x < _minimumCell.x || globalCell.x > _maximumCell.x
                || globalCell.y < _minimumCell.y || globalCell.y > _maximumCell.y
                || globalCell.z < _minimumCell.z || globalCell.z > _maximumCell.z)
            {
                amount = 0;
                return false;
            }

            int slot = FindSlot(globalCell);
            amount = _occupied[slot] != 0 ? _amounts[slot] : (byte)0;
            return true;
        }

        private void Accumulate(Vector3Int cell, uint amountUnitsPerParticle)
        {
            int slot = FindSlot(cell);
            if (_occupied[slot] == 0)
            {
                _occupied[slot] = 1;
                _cellKeys[slot] = cell;
            }

            uint accumulated = (uint)_amounts[slot] + amountUnitsPerParticle;
            _amounts[slot] = accumulated >= byte.MaxValue
                ? byte.MaxValue
                : (byte)accumulated;
        }

        private int FindSlot(Vector3Int cell)
        {
            int slot = Hash(cell) & _slotMask;
            while (_occupied[slot] != 0 && _cellKeys[slot] != cell)
                slot = (slot + 1) & _slotMask;
            return slot;
        }

        private static int Hash(Vector3Int cell)
        {
            unchecked
            {
                int hash = cell.x * 73856093;
                hash ^= cell.y * 19349663;
                hash ^= cell.z * 83492791;
                return hash;
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

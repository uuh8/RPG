using Game.Materials;
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

        internal static float EncodeTag(bool active, MaterialId materialKind)
        {
            return active ? (byte)materialKind + 1f : 0f;
        }

        internal static bool TryDecodeMaterial(float tag, out MaterialId materialKind)
        {
            int encoded = Mathf.RoundToInt(tag);
            if (encoded <= 0 || Mathf.Abs(tag - encoded) > 0.001f)
            {
                materialKind = MaterialId.Empty;
                return false;
            }

            materialKind = (MaterialId)(encoded - 1);
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
        internal readonly LiquidMaterialAmountScaleSnapshot AmountScales;
        internal readonly uint LayoutVersion;
        internal readonly uint TopologyVersion;
        internal readonly uint SnapshotVersion;

        internal FluidGameplayReadbackMetadata(
            Bounds bounds,
            Vector3 origin,
            float cellSize,
            int particleCapacity,
            LiquidMaterialAmountScaleSnapshot amountScales,
            uint layoutVersion,
            uint topologyVersion,
            uint snapshotVersion)
        {
            Bounds = bounds;
            Origin = origin;
            CellSize = cellSize;
            ParticleCapacity = particleCapacity;
            AmountScales = amountScales;
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
                && metadata.AmountScales != null
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
        private readonly MaterialId[] _materialKeys;
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
            _materialKeys = new MaterialId[slotCount];
            _amounts = new byte[slotCount];
            _occupied = new byte[slotCount];
            _slotMask = slotCount - 1;
        }

        public bool HasValidSnapshot { get; private set; }
        public Bounds SnapshotBounds { get; private set; }
        internal uint LayoutVersion { get; private set; }
        internal uint TopologyVersion { get; private set; }
        public uint SnapshotVersion { get; private set; }
        private LiquidMaterialAmountScaleSnapshot _amountScales;

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
                if (!FluidGameplaySampleCodec.TryDecodeMaterial(sample.w, out MaterialId material)
                    || !metadata.AmountScales.TryGet(material, out uint amountUnitsPerParticle)
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
                Accumulate(cell, material, amountUnitsPerParticle);
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
            _amountScales = metadata.AmountScales;
            HasValidSnapshot = true;
            return true;
        }

        public bool TryGetAmount(
            Vector3Int globalCell,
            MaterialId materialKind,
            out byte amount)
        {
            if (!HasValidSnapshot
                || _amountScales == null
                || !_amountScales.TryGet(materialKind, out _)
                || globalCell.x < _minimumCell.x || globalCell.x > _maximumCell.x
                || globalCell.y < _minimumCell.y || globalCell.y > _maximumCell.y
                || globalCell.z < _minimumCell.z || globalCell.z > _maximumCell.z)
            {
                amount = 0;
                return false;
            }

            int slot = FindSlot(globalCell, materialKind);
            amount = _occupied[slot] != 0 ? _amounts[slot] : (byte)0;
            return true;
        }

        public bool TryGetAmountUnitsPerParticle(MaterialId material, out uint amountUnits)
        {
            amountUnits = 0u;
            return HasValidSnapshot
                && _amountScales != null
                && _amountScales.TryGet(material, out amountUnits);
        }

        public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (!HasValidSnapshot || _amountScales == null || !_amountScales.TryGet(material, out _))
                return 0;

            int count = 0;
            // 直接扫描固定 Open Addressing Table，不创建 List/Enumerator，低频 Readback 路径保持 zero-GC。
            for (int slot = 0; slot < _occupied.Length && count < destination.Length; slot++)
            {
                if (_occupied[slot] == 0 || _materialKeys[slot] != material || _amounts[slot] == 0)
                    continue;
                destination[count++] = new LiquidMaterialCellSample(_cellKeys[slot], _amounts[slot]);
            }
            return count;
        }

        private void Accumulate(
            Vector3Int cell,
            MaterialId material,
            uint amountUnitsPerParticle)
        {
            int slot = FindSlot(cell, material);
            if (_occupied[slot] == 0)
            {
                _occupied[slot] = 1;
                _cellKeys[slot] = cell;
                _materialKeys[slot] = material;
            }

            uint accumulated = (uint)_amounts[slot] + amountUnitsPerParticle;
            _amounts[slot] = accumulated >= byte.MaxValue
                ? byte.MaxValue
                : (byte)accumulated;
        }

        private int FindSlot(Vector3Int cell, MaterialId material)
        {
            int slot = Hash(cell, material) & _slotMask;
            while (_occupied[slot] != 0
                && (_cellKeys[slot] != cell || _materialKeys[slot] != material))
                slot = (slot + 1) & _slotMask;
            return slot;
        }

        private static int Hash(Vector3Int cell, MaterialId material)
        {
            unchecked
            {
                int hash = cell.x * 73856093;
                hash ^= cell.y * 19349663;
                hash ^= cell.z * 83492791;
                hash ^= (byte)material * -1640531527;
                return hash;
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

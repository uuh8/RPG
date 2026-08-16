using System;
using Game.Combat;
using UnityEngine;

namespace Game.ElementField
{
    public readonly struct FluidFireCellSample
    {
        public FluidFireCellSample(Vector3Int globalCell, byte amount)
        {
            GlobalCell = globalCell;
            Amount = amount;
        }

        public Vector3Int GlobalCell { get; }
        public byte Amount { get; }
    }

    /// <summary>
    /// 只做确定性反应规划，不直接修改 CPU/GPU World。Water reservation 防止同一份延迟 Snapshot
    /// 被多个 Fire Cell 重复消费；成功提交后才记住 SnapshotVersion。
    /// </summary>
    public sealed class FluidFireReactionSystem
    {
        private static readonly Vector3Int[] ContactOffsets =
        {
            Vector3Int.zero,
            Vector3Int.left,
            Vector3Int.right,
            Vector3Int.down,
            Vector3Int.up,
            new Vector3Int(0, 0, -1),
            new Vector3Int(0, 0, 1),
        };

        private readonly ElementCellDeltaRequest[] _fireDeltas;
        private readonly FluidConsumeCommand[] _consumeCommands;
        private readonly Vector3Int[] _reservedWaterCells;
        private readonly byte[] _reservedWaterAmounts;
        private int _reservedWaterCount;
        private uint _lastCommittedSnapshotVersion;
        private bool _hasCommittedSnapshot;

        public FluidFireReactionSystem(int maximumCommands)
        {
            if (maximumCommands <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumCommands));
            _fireDeltas = new ElementCellDeltaRequest[maximumCommands];
            _consumeCommands = new FluidConsumeCommand[maximumCommands];
            _reservedWaterCells = new Vector3Int[maximumCommands];
            _reservedWaterAmounts = new byte[maximumCommands];
        }

        public bool TryPlanAndCommit(
            ILiquidOccupancyReadOnly occupancy,
            FluidFireCellSample[] fireCells,
            int fireCellCount,
            float deltaTime,
            in ExtinguishTuning tuning,
            uint amountUnitsPerParticle,
            FluidReactionCommandQueue queue)
        {
            if (occupancy == null || fireCells == null || queue == null)
                return false;
            if (!occupancy.HasValidSnapshot || amountUnitsPerParticle == 0u || deltaTime <= 0f)
                return false;
            if (_hasCommittedSnapshot && occupancy.SnapshotVersion == _lastCommittedSnapshotVersion)
                return false;

            fireCellCount = Mathf.Clamp(fireCellCount, 0, fireCells.Length);
            _reservedWaterCount = 0;
            int fireDeltaCount = 0;
            int consumeCommandCount = 0;
            for (int fireIndex = 0; fireIndex < fireCellCount; fireIndex++)
            {
                FluidFireCellSample fire = fireCells[fireIndex];
                int remainingFireAmount = fire.Amount;
                if (remainingFireAmount <= 0)
                    continue;

                for (int contact = 0; contact < ContactOffsets.Length; contact++)
                {
                    if (remainingFireAmount <= 0 || fireDeltaCount >= _fireDeltas.Length)
                        break;

                    Vector3Int waterCell = fire.GlobalCell + ContactOffsets[contact];
                    if (!occupancy.TryGetAmount(waterCell, ElementMaterialKind.Water, out byte snapshotWater))
                        continue;
                    int availableWater = snapshotWater - GetReservedWater(waterCell);
                    if (availableWater <= 0)
                        continue;

                    float fireIntensity = remainingFireAmount * (100f / byte.MaxValue);
                    float waterIntensity = availableWater * (100f / byte.MaxValue);
                    bool formal = fireIntensity >= Mathf.Max(0f, tuning.FormalThreshold)
                        && waterIntensity >= Mathf.Max(0f, tuning.FormalThreshold);
                    float consumedIntensity = ElementReactionEvaluator.CalculateExtinguishConsumption(
                        fireIntensity,
                        waterIntensity,
                        deltaTime,
                        formal,
                        in tuning);
                    int consumedAmount = Mathf.Clamp(
                        Mathf.RoundToInt(consumedIntensity * (byte.MaxValue / 100f)),
                        0,
                        Mathf.Min(remainingFireAmount, availableWater));
                    if (consumedAmount <= 0)
                        continue;

                    _fireDeltas[fireDeltaCount++] = new ElementCellDeltaRequest(
                        fire.GlobalCell,
                        ElementMaterialKind.Fire,
                        (byte)consumedAmount);
                    ReserveWater(waterCell, consumedAmount);
                    int reservationIndex = FindReservedWater(waterCell);
                    uint totalParticles = ((uint)_reservedWaterAmounts[reservationIndex]
                        + amountUnitsPerParticle - 1u) / amountUnitsPerParticle;
                    if (reservationIndex >= consumeCommandCount)
                        consumeCommandCount = reservationIndex + 1;
                    _consumeCommands[reservationIndex] = new FluidConsumeCommand(
                        waterCell,
                        waterCell,
                        (uint)ElementMaterialKind.Water,
                        totalParticles,
                        HashSeed(occupancy.SnapshotVersion, fire.GlobalCell, waterCell));
                    remainingFireAmount -= consumedAmount;
                }
            }

            if (!queue.TryEnqueueBatch(
                    _fireDeltas,
                    fireDeltaCount,
                    _consumeCommands,
                    consumeCommandCount))
                return false;

            _lastCommittedSnapshotVersion = occupancy.SnapshotVersion;
            _hasCommittedSnapshot = true;
            return true;
        }

        private int GetReservedWater(Vector3Int cell)
        {
            for (int index = 0; index < _reservedWaterCount; index++)
            {
                if (_reservedWaterCells[index] == cell)
                    return _reservedWaterAmounts[index];
            }
            return 0;
        }

        private int FindReservedWater(Vector3Int cell)
        {
            for (int index = 0; index < _reservedWaterCount; index++)
            {
                if (_reservedWaterCells[index] == cell)
                    return index;
            }
            return -1;
        }

        private void ReserveWater(Vector3Int cell, int amount)
        {
            for (int index = 0; index < _reservedWaterCount; index++)
            {
                if (_reservedWaterCells[index] != cell)
                    continue;
                _reservedWaterAmounts[index] = (byte)Mathf.Min(
                    byte.MaxValue,
                    _reservedWaterAmounts[index] + amount);
                return;
            }
            if (_reservedWaterCount >= _reservedWaterCells.Length)
                return;
            _reservedWaterCells[_reservedWaterCount] = cell;
            _reservedWaterAmounts[_reservedWaterCount] = (byte)amount;
            _reservedWaterCount++;
        }

        private static uint HashSeed(uint version, Vector3Int fire, Vector3Int water)
        {
            unchecked
            {
                uint hash = version * 747796405u + 2891336453u;
                hash ^= (uint)fire.x * 73856093u;
                hash ^= (uint)fire.y * 19349663u;
                hash ^= (uint)fire.z * 83492791u;
                hash ^= (uint)water.GetHashCode();
                return hash;
            }
        }
    }
}

using System;
using Game.Combat;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 稀疏世界版 Water/Fire Solver。它沿用 P6-A 的 Stage 顺序和公式，但把邻接关系从
    /// “同一数组的相邻 Index”提升为“固定 Global Cell 的相邻坐标”，因此边界两侧仍是一张连续世界。
    /// </summary>
    public sealed class ElementWorldSimulator
    {
        private static readonly ProfilerMarker PrepareNeighborsMarker =
            new ProfilerMarker("ElementWorld.Simulate.PrepareNeighbors");
        private static readonly ProfilerMarker ReactionMarker =
            new ProfilerMarker("ElementWorld.Simulate.Reaction");
        private static readonly ProfilerMarker DownFlowMarker =
            new ProfilerMarker("ElementWorld.Simulate.DownFlow");
        private static readonly ProfilerMarker HorizontalFlowMarker =
            new ProfilerMarker("ElementWorld.Simulate.HorizontalFlow");
        private static readonly ProfilerMarker FireDecayMarker =
            new ProfilerMarker("ElementWorld.Simulate.FireDecay");
        private static readonly ProfilerMarker CommitMarker =
            new ProfilerMarker("ElementWorld.Simulate.Commit");

        private static readonly Vector3Int[] PositiveReactionDirections =
        {
            Vector3Int.right,
            Vector3Int.up,
            Vector3Int.forward,
        };

        private static readonly Vector3Int[] HorizontalDirections =
        {
            Vector3Int.left,
            Vector3Int.right,
            Vector3Int.forward,
            Vector3Int.back,
        };

        private readonly ElementWorldChunk[] _simulationChunks;
        private readonly ElementBoundaryNeighborPlanner _neighborPlanner;
        private readonly int _settleAfterUnchangedTicks;
        private int _simulationChunkCount;

        public ElementWorldSimulator(
            int maximumResidentChunks,
            int settleAfterUnchangedTicks = 2)
        {
            if (maximumResidentChunks <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumResidentChunks));
            if (settleAfterUnchangedTicks <= 0)
                throw new ArgumentOutOfRangeException(nameof(settleAfterUnchangedTicks));

            // Runtime 初始化时一次性分配；固定 Tick 中只覆盖引用，不创建 List/Array。
            _simulationChunks = new ElementWorldChunk[maximumResidentChunks];
            _neighborPlanner = new ElementBoundaryNeighborPlanner(maximumResidentChunks);
            _settleAfterUnchangedTicks = settleAfterUnchangedTicks;
        }

        public ElementFieldSimulationStats SimulateActiveChunks(
            ElementWorldStore store,
            ElementWorldChunk[] activeChunks,
            int activeChunkCount,
            long worldTick,
            float deltaTime,
            in ElementFieldSimulationSettings settings)
        {
            ValidateArguments(
                store,
                activeChunks,
                activeChunkCount,
                worldTick,
                deltaTime,
                in settings);

            _simulationChunkCount = CopyUniqueActiveChunks(activeChunks, activeChunkCount);
            // PBF 模式保留旧 Cell 数据但不再让它生成接收 Chunk；模式切换只在 Play 初始化生效，
            // 因此这里必须跳过 Prepare，而不是扫描后再清理残留 Water。
            if (settings.SimulateCellWater)
            {
                using (PrepareNeighborsMarker.Auto())
                {
                    _simulationChunkCount = _neighborPlanner.PrepareNeighbors(
                        store,
                        _simulationChunks,
                        _simulationChunkCount,
                        worldTick,
                        settings.MaxDownFlowPerTick,
                        settings.MaxLateralFlowPerTick);
                }
            }
            SortSimulationChunks();

            var stats = new ElementFieldSimulationStats();
            if (settings.SimulateCellWater)
            {
                using (ReactionMarker.Auto())
                    RunReactionStage(deltaTime, in settings.Extinguish, ref stats);
                using (DownFlowMarker.Auto())
                    RunDownFlowStage(settings.MaxDownFlowPerTick, ref stats);
                using (HorizontalFlowMarker.Auto())
                    RunHorizontalFlowStage(settings.MaxLateralFlowPerTick, worldTick, ref stats);
            }
            using (FireDecayMarker.Auto())
                RunFireDecayStage(settings.FireDecayPerTick, ref stats);

            using (CommitMarker.Auto())
            {
                for (int i = 0; i < _simulationChunkCount; i++)
                {
                    ElementWorldChunk chunk = _simulationChunks[i];
                    chunk.RefreshNonEmptyCellCount();
                    int changedCellCount = chunk.Grid.CommitDirtyVersions();
                    chunk.RecordSimulationResult(
                        changedCellCount,
                        _settleAfterUnchangedTicks);
                    stats.ChangedCells += changedCellCount;
                }
            }

            return stats;
        }

        private static void ValidateArguments(
            ElementWorldStore store,
            ElementWorldChunk[] activeChunks,
            int activeChunkCount,
            long worldTick,
            float deltaTime,
            in ElementFieldSimulationSettings settings)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (activeChunks == null)
                throw new ArgumentNullException(nameof(activeChunks));
            if (activeChunkCount < 0 || activeChunkCount > activeChunks.Length)
                throw new ArgumentOutOfRangeException(nameof(activeChunkCount));
            if (worldTick < 0)
                throw new ArgumentOutOfRangeException(nameof(worldTick));
            if (deltaTime < 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
                throw new ArgumentOutOfRangeException(nameof(deltaTime));
            if (settings.ChunkSize != store.ChunkSize)
            {
                throw new ArgumentException(
                    "Simulation settings ChunkSize must match the ElementWorldStore.",
                    nameof(settings));
            }
        }

        private int CopyUniqueActiveChunks(
            ElementWorldChunk[] activeChunks,
            int activeChunkCount)
        {
            int copied = 0;
            for (int i = 0; i < activeChunkCount && copied < _simulationChunks.Length; i++)
            {
                ElementWorldChunk candidate = activeChunks[i];
                if (candidate == null || ContainsUnsorted(candidate.Key, copied))
                    continue;

                candidate.ActivityState = ElementChunkActivityState.Active;
                _simulationChunks[copied++] = candidate;
            }

            return copied;
        }

        private bool ContainsUnsorted(ElementChunkKey key, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_simulationChunks[i].Key == key)
                    return true;
            }

            return false;
        }

        private void SortSimulationChunks()
        {
            // Dictionary 的枚举顺序不是 Gameplay 契约。这里用无分配 Insertion Sort 固定为 Z→Y→X，
            // 后续同样的 Local Z→Y→X 遍历便会产生稳定的离散余数和反应顺序。
            for (int i = 1; i < _simulationChunkCount; i++)
            {
                ElementWorldChunk value = _simulationChunks[i];
                int insert = i - 1;
                while (insert >= 0 && CompareKeys(_simulationChunks[insert].Key, value.Key) > 0)
                {
                    _simulationChunks[insert + 1] = _simulationChunks[insert];
                    insert--;
                }

                _simulationChunks[insert + 1] = value;
            }
        }

        private static int CompareKeys(ElementChunkKey a, ElementChunkKey b)
        {
            int z = a.Z.CompareTo(b.Z);
            if (z != 0)
                return z;
            int y = a.Y.CompareTo(b.Y);
            return y != 0 ? y : a.X.CompareTo(b.X);
        }

        private void RunReactionStage(
            float deltaTime,
            in ExtinguishTuning tuning,
            ref ElementFieldSimulationStats stats)
        {
            PrepareStage();
            int size = _simulationChunkCount > 0 ? _simulationChunks[0].ChunkSize : 0;

            for (int chunkIndex = 0; chunkIndex < _simulationChunkCount; chunkIndex++)
            {
                ElementWorldChunk sourceChunk = _simulationChunks[chunkIndex];
                for (int z = 0; z < size; z++)
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var sourceLocal = new Vector3Int(x, y, z);
                    int sourceIndex = ElementFieldCoordinates.ToIndex(
                        sourceLocal,
                        sourceChunk.Grid.Dimensions);
                    ElementCell sourceCell = sourceChunk.CurrentCells[sourceIndex];
                    if (sourceCell.MaterialKind != ElementMaterialKind.Water
                        && sourceCell.MaterialKind != ElementMaterialKind.Fire)
                    {
                        continue;
                    }

                    for (int directionIndex = 0;
                         directionIndex < PositiveReactionDirections.Length;
                         directionIndex++)
                    {
                        if (TryResolveNeighbor(
                                sourceChunk,
                                sourceLocal,
                                PositiveReactionDirections[directionIndex],
                                out ElementWorldChunk targetChunk,
                                out Vector3Int targetLocal))
                        {
                            int targetIndex = ElementFieldCoordinates.ToIndex(
                                targetLocal,
                                targetChunk.Grid.Dimensions);
                            ReactPair(
                                sourceChunk,
                                sourceIndex,
                                targetChunk,
                                targetIndex,
                                deltaTime,
                                in tuning,
                                ref stats);
                        }
                    }
                }
            }

            ApplyReactionDeltas();
            CommitPreparedStage();
        }

        private static void ReactPair(
            ElementWorldChunk firstChunk,
            int firstIndex,
            ElementWorldChunk secondChunk,
            int secondIndex,
            float deltaTime,
            in ExtinguishTuning tuning,
            ref ElementFieldSimulationStats stats)
        {
            ElementCell first = firstChunk.CurrentCells[firstIndex];
            ElementCell second = secondChunk.CurrentCells[secondIndex];
            bool opposed = first.MaterialKind == ElementMaterialKind.Water
                && second.MaterialKind == ElementMaterialKind.Fire
                || first.MaterialKind == ElementMaterialKind.Fire
                && second.MaterialKind == ElementMaterialKind.Water;
            if (!opposed)
                return;

            int firstAvailable = first.Amount + firstChunk.AmountDelta[firstIndex];
            int secondAvailable = second.Amount + secondChunk.AmountDelta[secondIndex];
            if (firstAvailable <= 0 || secondAvailable <= 0)
                return;

            int fireAmount = first.MaterialKind == ElementMaterialKind.Fire
                ? firstAvailable
                : secondAvailable;
            int waterAmount = first.MaterialKind == ElementMaterialKind.Water
                ? firstAvailable
                : secondAvailable;
            float fireIntensity = fireAmount * 100f / byte.MaxValue;
            float waterIntensity = waterAmount * 100f / byte.MaxValue;
            float threshold = Mathf.Max(0f, tuning.FormalThreshold);
            bool formal = fireIntensity >= threshold && waterIntensity >= threshold;

            float consumedIntensity = ElementReactionEvaluator.CalculateExtinguishConsumption(
                fireIntensity,
                waterIntensity,
                deltaTime,
                formal,
                in tuning);
            int consumedAmount = Mathf.RoundToInt(consumedIntensity * byte.MaxValue / 100f);
            consumedAmount = Mathf.Min(consumedAmount, Mathf.Min(firstAvailable, secondAvailable));
            if (consumedAmount <= 0)
                return;

            firstChunk.AmountDelta[firstIndex] -= consumedAmount;
            secondChunk.AmountDelta[secondIndex] -= consumedAmount;
            stats.ReactionPairs++;
        }

        private void ApplyReactionDeltas()
        {
            for (int chunkIndex = 0; chunkIndex < _simulationChunkCount; chunkIndex++)
            {
                ElementWorldChunk chunk = _simulationChunks[chunkIndex];
                for (int index = 0; index < chunk.CellCount; index++)
                {
                    int delta = chunk.AmountDelta[index];
                    if (delta == 0)
                        continue;

                    ElementCell current = chunk.CurrentCells[index];
                    int amount = Mathf.Clamp(current.Amount + delta, 0, byte.MaxValue);
                    chunk.NextCells[index] = amount > 0
                        ? new ElementCell(current.MaterialKind, (byte)amount)
                        : default;
                }
            }
        }

        private void RunDownFlowStage(
            byte maxTransfer,
            ref ElementFieldSimulationStats stats)
        {
            PrepareStage();
            if (maxTransfer == 0)
            {
                CommitPreparedStage();
                return;
            }

            int size = _simulationChunkCount > 0 ? _simulationChunks[0].ChunkSize : 0;
            for (int chunkIndex = 0; chunkIndex < _simulationChunkCount; chunkIndex++)
            {
                ElementWorldChunk sourceChunk = _simulationChunks[chunkIndex];
                for (int z = 0; z < size; z++)
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var sourceLocal = new Vector3Int(x, y, z);
                    int sourceIndex = ElementFieldCoordinates.ToIndex(
                        sourceLocal,
                        sourceChunk.Grid.Dimensions);
                    if (sourceChunk.CurrentCells[sourceIndex].MaterialKind != ElementMaterialKind.Water
                        || !TryResolveNeighbor(
                            sourceChunk,
                            sourceLocal,
                            Vector3Int.down,
                            out ElementWorldChunk targetChunk,
                            out Vector3Int targetLocal))
                    {
                        continue;
                    }

                    int targetIndex = ElementFieldCoordinates.ToIndex(
                        targetLocal,
                        targetChunk.Grid.Dimensions);
                    if (targetChunk.SolidMask[targetIndex]
                        || !CanReceiveWater(targetChunk.CurrentCells[targetIndex]))
                    {
                        continue;
                    }

                    TransferWater(
                        sourceChunk,
                        sourceIndex,
                        targetChunk,
                        targetIndex,
                        maxTransfer,
                        balanceByDifference: false,
                        ref stats);
                }
            }

            ApplyWaterDeltas();
            CommitPreparedStage();
        }

        private void RunHorizontalFlowStage(
            byte maxTransfer,
            long worldTick,
            ref ElementFieldSimulationStats stats)
        {
            PrepareStage();
            if (maxTransfer == 0)
            {
                CommitPreparedStage();
                return;
            }

            int size = _simulationChunkCount > 0 ? _simulationChunks[0].ChunkSize : 0;
            bool reverse = (worldTick & 1L) != 0L;
            for (int chunkIndex = 0; chunkIndex < _simulationChunkCount; chunkIndex++)
            {
                ElementWorldChunk sourceChunk = _simulationChunks[chunkIndex];
                for (int z = 0; z < size; z++)
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var sourceLocal = new Vector3Int(x, y, z);
                    int sourceIndex = ElementFieldCoordinates.ToIndex(
                        sourceLocal,
                        sourceChunk.Grid.Dimensions);
                    if (sourceChunk.CurrentCells[sourceIndex].MaterialKind != ElementMaterialKind.Water)
                        continue;

                    for (int order = 0; order < HorizontalDirections.Length; order++)
                    {
                        int directionIndex = reverse
                            ? HorizontalDirections.Length - 1 - order
                            : order;
                        if (!TryResolveNeighbor(
                                sourceChunk,
                                sourceLocal,
                                HorizontalDirections[directionIndex],
                                out ElementWorldChunk targetChunk,
                                out Vector3Int targetLocal))
                        {
                            continue;
                        }

                        int targetIndex = ElementFieldCoordinates.ToIndex(
                            targetLocal,
                            targetChunk.Grid.Dimensions);
                        if (targetChunk.SolidMask[targetIndex]
                            || !CanReceiveWater(targetChunk.CurrentCells[targetIndex]))
                        {
                            continue;
                        }

                        TransferWater(
                            sourceChunk,
                            sourceIndex,
                            targetChunk,
                            targetIndex,
                            maxTransfer,
                            balanceByDifference: true,
                            ref stats);
                    }
                }
            }

            ApplyWaterDeltas();
            CommitPreparedStage();
        }

        private static void TransferWater(
            ElementWorldChunk sourceChunk,
            int sourceIndex,
            ElementWorldChunk targetChunk,
            int targetIndex,
            int maxTransfer,
            bool balanceByDifference,
            ref ElementFieldSimulationStats stats)
        {
            ElementCell source = sourceChunk.CurrentCells[sourceIndex];
            ElementCell target = targetChunk.CurrentCells[targetIndex];
            int sourceAvailable = source.Amount + sourceChunk.AmountDelta[sourceIndex];
            int targetOriginal = target.MaterialKind == ElementMaterialKind.Water ? target.Amount : 0;
            int targetProjected = targetOriginal + targetChunk.AmountDelta[targetIndex];
            int targetCapacity = byte.MaxValue - targetProjected;
            if (sourceAvailable <= 0 || targetCapacity <= 0)
                return;

            int desired = balanceByDifference
                ? ElementFieldSimulator.CalculateStableLateralTransfer(
                    source.Amount,
                    targetOriginal,
                    maxTransfer)
                : maxTransfer;
            int transfer = Mathf.Min(desired, Mathf.Min(sourceAvailable, targetCapacity));
            transfer = Mathf.Min(transfer, maxTransfer);
            if (transfer <= 0)
                return;

            sourceChunk.AmountDelta[sourceIndex] -= transfer;
            targetChunk.AmountDelta[targetIndex] += transfer;
            stats.WaterTransfers++;
        }

        private void ApplyWaterDeltas()
        {
            for (int chunkIndex = 0; chunkIndex < _simulationChunkCount; chunkIndex++)
            {
                ElementWorldChunk chunk = _simulationChunks[chunkIndex];
                for (int index = 0; index < chunk.CellCount; index++)
                {
                    int delta = chunk.AmountDelta[index];
                    if (delta == 0)
                        continue;

                    ElementCell current = chunk.CurrentCells[index];
                    int original = current.MaterialKind == ElementMaterialKind.Water
                        ? current.Amount
                        : 0;
                    int amount = Mathf.Clamp(original + delta, 0, byte.MaxValue);
                    chunk.NextCells[index] = amount > 0
                        ? new ElementCell(ElementMaterialKind.Water, (byte)amount)
                        : default;
                }
            }
        }

        private void RunFireDecayStage(
            byte decay,
            ref ElementFieldSimulationStats stats)
        {
            PrepareStage();
            if (decay > 0)
            {
                for (int chunkIndex = 0; chunkIndex < _simulationChunkCount; chunkIndex++)
                {
                    ElementWorldChunk chunk = _simulationChunks[chunkIndex];
                    for (int index = 0; index < chunk.CellCount; index++)
                    {
                        ElementCell current = chunk.CurrentCells[index];
                        if (current.MaterialKind != ElementMaterialKind.Fire || current.Amount == 0)
                            continue;

                        int amount = Mathf.Max(0, current.Amount - decay);
                        chunk.NextCells[index] = amount > 0
                            ? new ElementCell(ElementMaterialKind.Fire, (byte)amount)
                            : default;
                        stats.FireCellsDecayed++;
                    }
                }
            }

            CommitPreparedStage();
        }

        private void PrepareStage()
        {
            for (int i = 0; i < _simulationChunkCount; i++)
            {
                ElementGrid grid = _simulationChunks[i].Grid;
                grid.PrepareNextFromCurrent();
                grid.ClearAmountDelta();
            }
        }

        private void CommitPreparedStage()
        {
            // 这一步就是 Stage Barrier：所有 Chunk 都完成只读 Current/写 Next 后才统一交换。
            // 若处理完一个 Chunk 就立即交换，后处理 Chunk 会读到“未来值”，结果将依赖枚举顺序。
            for (int i = 0; i < _simulationChunkCount; i++)
                _simulationChunks[i].Grid.CommitPreparedStage();
        }

        private bool TryResolveNeighbor(
            ElementWorldChunk sourceChunk,
            Vector3Int sourceLocal,
            Vector3Int direction,
            out ElementWorldChunk targetChunk,
            out Vector3Int targetLocal)
        {
            int size = sourceChunk.ChunkSize;
            int x = sourceLocal.x + direction.x;
            int y = sourceLocal.y + direction.y;
            int z = sourceLocal.z + direction.z;
            int chunkX = sourceChunk.Key.X;
            int chunkY = sourceChunk.Key.Y;
            int chunkZ = sourceChunk.Key.Z;

            // 绝大多数邻接关系都位于同一 Chunk。原实现即使没有跨边界，也会对已排序 Chunk 数组
            // 做 O(log n) Binary Search；Reaction 每 Cell 有三次查询，因此在 69 个 Active Chunk 时
            // 会把大量 CPU 浪费在查找“自己”。先走本地 Fast Path，只在真正越界时查询邻居 Chunk。
            if ((uint)x < (uint)size
                && (uint)y < (uint)size
                && (uint)z < (uint)size)
            {
                targetChunk = sourceChunk;
                targetLocal = new Vector3Int(x, y, z);
                return true;
            }

            WrapAxis(ref x, ref chunkX, size);
            WrapAxis(ref y, ref chunkY, size);
            WrapAxis(ref z, ref chunkZ, size);

            targetLocal = new Vector3Int(x, y, z);
            return TryFindSimulationChunk(
                new ElementChunkKey(chunkX, chunkY, chunkZ),
                out targetChunk);
        }

        private static void WrapAxis(ref int local, ref int chunk, int chunkSize)
        {
            if (local < 0)
            {
                local += chunkSize;
                chunk--;
            }
            else if (local >= chunkSize)
            {
                local -= chunkSize;
                chunk++;
            }
        }

        private bool TryFindSimulationChunk(
            ElementChunkKey key,
            out ElementWorldChunk chunk)
        {
            // `_simulationChunks` 已排序，因此跨边界查询使用 O(log n) Binary Search，
            // 不需要在每个 Cell 邻接查询时扫描最多 512 个 Chunk。
            int low = 0;
            int high = _simulationChunkCount - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                ElementWorldChunk candidate = _simulationChunks[middle];
                int comparison = CompareKeys(candidate.Key, key);
                if (comparison == 0)
                {
                    chunk = candidate;
                    return true;
                }

                if (comparison < 0)
                    low = middle + 1;
                else
                    high = middle - 1;
            }

            chunk = null;
            return false;
        }

        private static bool CanReceiveWater(ElementCell cell)
        {
            return cell.IsEmpty || cell.MaterialKind == ElementMaterialKind.Water;
        }
    }
}

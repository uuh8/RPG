using System;
using System.Collections.Generic;
using Game.Core;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField
{
    /// <summary>
    /// P6-B 连续关卡的 Unity Adapter：固定 World Origin，管理 Interest Point、稀疏 Chunk 生命周期、
    /// 写入队列与固定 Tick Simulation。Projectile 只能通过 IElementWriteSink 入队，不能取得 Store 写权限。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ElementFieldSolidBaker))]
    public sealed class ElementWorldRuntime : MonoBehaviour, IElementWriteSink, IElementWorldReadOnly
    {
        private const float TransformTolerance = 0.0001f;

        private static readonly ProfilerMarker ApplyWritesMarker =
            new ProfilerMarker("ElementWorld.ApplyWrites");
        private static readonly ProfilerMarker SimulateMarker =
            new ProfilerMarker("ElementWorld.Simulate");
        private static readonly ProfilerMarker RebuildActivityMarker =
            new ProfilerMarker("ElementWorld.RebuildActivity");
        private static readonly ProfilerMarker BakeChunkSolidMarker =
            new ProfilerMarker("ElementWorld.BakeChunkSolid");

        [Header("Gameplay Data")]
        [SerializeField] private ElementWorldProfile _profile;

        [Header("GPU PBF Water (Optional)")]
        [Tooltip("仅当 Profile 选择 GpuPbf 时使用；缺失或未初始化会拒绝 Water，不会回写 Legacy Cell。")]
        [SerializeField] private GpuPbfFluidRuntime _gpuPbfFluidRuntime;
        [Tooltip("Task 10 的低频 Water Occupancy；只用于 Gameplay 反应，不参与即时 GPU Solver。")]
        [SerializeField] private FluidGameplayOccupancyBridge _fluidGameplayOccupancy;

        [Header("Streaming Interest Point")]
        [Tooltip("只决定附近哪些 Chunk 工作，不会成为世界 Origin，也不会让 ElementWorldRoot 跟随玩家移动。")]
        [SerializeField] private Transform _interestPoint;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private Vector3Int _interestGlobalCell;
        [SerializeField] private Vector3Int _interestChunkKey;
        [SerializeField] private long _worldTick;
        [SerializeField] private int _residentChunkCount;
        [SerializeField] private int _activeChunkCount;
        [SerializeField] private int _activeElementCellCount;
        [SerializeField] private int _sleepingChunkCount;
        [SerializeField] private int _reclaimedChunkCountLastTick;
        [SerializeField] private int _bakedChunkCount;
        [SerializeField] private int _lastBakedSolidCellCount;
        [SerializeField] private ElementFieldSimulationStats _lastStats;

        private ElementWorldStore _store;
        private ElementWriteQueue _writeQueue;
        private ElementWorldSimulator _simulator;
        private ElementFieldSimulationSettings _settings;
        private ElementFieldTickAccumulator _tickAccumulator;
        private ElementFieldSolidBaker _solidBaker;
        private ElementWorldChunk[] _activeChunks;
        private ElementChunkKey[] _reclaimKeys;
        private Vector3 _origin;
        private float _tickInterval;
        private ElementChunkKey _interestChunk;
        private bool _hasInterestChunk;
        private WaterSimulationMode _effectiveWaterSimulationMode;
        private IFluidDepositSink _fluidDepositSink;
        private ILiquidOccupancyReadOnly _liquidOccupancy;
        private FluidReactionCommandQueue _fluidReactionQueue;
        private FluidFireReactionSystem _fluidFireReactionSystem;
        private FluidFireCellSample[] _fireReactionSamples;
        private bool _hasReportedUnsupportedPbfFallback;

        public bool IsInitialized { get; private set; }
        public Vector3 Origin => _origin;
        public float CellSize => IsInitialized ? _settings.CellSize : 0f;
        public int ChunkSize => IsInitialized ? _profile.ChunkSize : 0;
        public int MaximumResidentChunkCount => IsInitialized ? _profile.MaximumResidentChunks : 0;
        public long WorldTick => _worldTick;
        public ElementChunkKey InterestChunk => _interestChunk;
        public int ResidentChunkCount => _residentChunkCount;
        public int ActiveChunkCount => _activeChunkCount;
        public int SleepingChunkCount => _sleepingChunkCount;
        public int PendingWriteCount => _writeQueue != null ? _writeQueue.Count : 0;
        public ElementFieldSimulationStats LastStats => _lastStats;
        public WaterSimulationMode EffectiveWaterSimulationMode => _effectiveWaterSimulationMode;

        private void Awake()
        {
            if (!TryInitialize())
                enabled = false;
        }

        private void OnEnable()
        {
            if (!IsInitialized && !TryInitialize())
            {
                enabled = false;
                return;
            }

            if (!ElementRuntimeRegistry.TryRegister(this))
            {
                GameLog.Error(
                    "Another Element Runtime already owns the scene write sink.",
                    "ElementField");
                enabled = false;
            }
        }

        private void OnDisable()
        {
            ElementRuntimeRegistry.Unregister(this);
        }

        private void Update()
        {
            if (!IsInitialized || Time.timeScale <= 0f)
                return;

            RefreshInterestChunk();

            int ticks = _tickAccumulator.Consume(Time.deltaTime);
            for (int i = 0; i < ticks; i++)
                SimulateOneTick();
        }

        /// <summary>
        /// 测试与 Debug 使用显式 deltaTime；正常 Gameplay 仍由 Update 驱动。
        /// </summary>
        public int TickForTests(float deltaTime)
        {
            if (!IsInitialized)
                return 0;

            RefreshInterestChunk();
            int ticks = _tickAccumulator.Consume(deltaTime);
            for (int i = 0; i < ticks; i++)
                SimulateOneTick();

            return ticks;
        }

        public bool TryEnqueueWrite(in ElementWriteRequest request)
        {
            return IsInitialized && ElementWorldWriteRouter.TryRoute(
                _effectiveWaterSimulationMode,
                in request,
                _writeQueue,
                _fluidDepositSink);
        }

        /// <summary>
        /// 返回 Interest Box 内已经 Resident 的 Chunk，而不是只返回 Solver Active Chunk。
        /// 稳定水会进入 Solver-Sleeping；若 Rendering 误用 Active Snapshot，水一稳定就会消失。
        /// </summary>
        public int CopyVisibleChunkKeys(ElementChunkKey[] destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (!IsInitialized || !_hasInterestChunk || _store == null || destination.Length == 0)
                return 0;

            int count = 0;
            Dictionary<ElementChunkKey, ElementWorldChunk>.Enumerator enumerator =
                _store.Chunks.GetEnumerator();
            while (enumerator.MoveNext() && count < destination.Length)
            {
                KeyValuePair<ElementChunkKey, ElementWorldChunk> pair = enumerator.Current;
                ElementChunkKey key = pair.Key;
                // Presentation 没必要为只有 SolidMask、却没有 Water/Fire 的 Resident Chunk 分配 View。
                // 稳定水虽然 Solver-Sleeping，但 HasAnyElement 仍为 true，因此不会被这条规则误隐藏。
                if (!pair.Value.HasAnyElement)
                    continue;
                if (!ElementChunkActivityPlanner.IsInsideActiveRegion(
                        key,
                        _interestChunk,
                        _profile.ActiveRadiusXZChunks,
                        _profile.ActiveRadiusYChunks))
                {
                    continue;
                }

                destination[count++] = key;
            }
            enumerator.Dispose();

            // Dictionary 不保证枚举顺序。小规模 Buffer 使用原地 Insertion Sort，既不产生 GC，
            // 又让 View 分配、重建预算和测试结果在每次运行中保持确定性。
            for (int i = 1; i < count; i++)
            {
                ElementChunkKey value = destination[i];
                int previous = i - 1;
                while (previous >= 0 && CompareVisibleChunkKeys(destination[previous], value) > 0)
                {
                    destination[previous + 1] = destination[previous];
                    previous--;
                }

                destination[previous + 1] = value;
            }

            return count;
        }

        /// <summary>
        /// Global Cell 只读查询。不存在的稀疏 Chunk 等价于 Empty，读取不会隐式分配 Chunk。
        /// </summary>
        public bool TryGetCell(Vector3Int globalCell, out ElementCell cell)
        {
            if (!TryResolveResidentCell(globalCell, out ElementWorldChunk chunk, out Vector3Int localCell))
            {
                cell = default;
                return false;
            }

            cell = chunk.GetCell(localCell);
            return true;
        }

        public bool IsSolid(Vector3Int globalCell)
        {
            return TryResolveResidentCell(globalCell, out ElementWorldChunk chunk, out Vector3Int localCell)
                && chunk.Grid.IsSolid(localCell);
        }

        public uint GetChunkVersion(ElementChunkKey key)
        {
            // 每个 ElementWorldChunk 内部复用一个单 Chunk ElementGrid，所以局部版本坐标固定为 0,0,0。
            return IsInitialized && _store != null && _store.TryGetChunk(key, out ElementWorldChunk chunk)
                ? chunk.Grid.GetChunkVersion(0, 0, 0)
                : 0u;
        }

        /// <summary>
        /// Exposure/后续 Rendering 使用的只读 Interest Region 查询。稳定水所在的 Solver-Sleeping Chunk
        /// 仍必须可读，否则一旦水面稳定，角色 Wet 与视觉都会错误消失。Read 永远不会创建 Chunk。
        /// </summary>
        public bool TryGetActiveCell(Vector3Int globalCell, out ElementCell cell)
        {
            if (!IsInitialized || _store == null)
            {
                cell = default;
                return false;
            }

            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell,
                _profile.ChunkSize,
                out ElementChunkKey key,
                out Vector3Int localCell);
            if (!_hasInterestChunk
                || !ElementChunkActivityPlanner.IsInsideActiveRegion(
                    key,
                    _interestChunk,
                    _profile.ActiveRadiusXZChunks,
                    _profile.ActiveRadiusYChunks)
                || !_store.TryGetChunk(key, out ElementWorldChunk chunk))
            {
                cell = default;
                return false;
            }

            cell = chunk.GetCell(localCell);
            return true;
        }

        /// <summary>
        /// 当前 Interest Region 的连续 World AABB，仅用于集中 Physics Broadphase；
        /// 精确 Exposure 仍会逐 Global Cell 检查 Chunk 是否真的 Active。
        /// </summary>
        public Bounds GetActiveWorldBounds()
        {
            if (!IsInitialized || !_hasInterestChunk)
                return default;

            float chunkWorldSize = _profile.ChunkSize * _profile.CellSize;
            var minKey = new Vector3Int(
                _interestChunk.X - _profile.ActiveRadiusXZChunks,
                _interestChunk.Y - _profile.ActiveRadiusYChunks,
                _interestChunk.Z - _profile.ActiveRadiusXZChunks);
            var chunkCounts = new Vector3Int(
                _profile.ActiveRadiusXZChunks * 2 + 1,
                _profile.ActiveRadiusYChunks * 2 + 1,
                _profile.ActiveRadiusXZChunks * 2 + 1);
            Vector3 size = (Vector3)chunkCounts * chunkWorldSize;
            Vector3 min = _origin + (Vector3)minKey * chunkWorldSize;
            return new Bounds(min + size * 0.5f, size);
        }

        private bool TryInitialize()
        {
            if (IsInitialized)
                return true;
            if (_profile == null)
            {
                GameLog.Error("ElementWorldRuntime requires an ElementWorldProfile.", "ElementField");
                return false;
            }
            if (_interestPoint == null)
            {
                GameLog.Error("ElementWorldRuntime requires an Interest Point Transform.", "ElementField");
                return false;
            }
            if (!HasSupportedTransform())
            {
                GameLog.Error(
                    "ElementWorldRuntime requires identity world rotation and unit world scale.",
                    "ElementField");
                return false;
            }

            _solidBaker = GetComponent<ElementFieldSolidBaker>();
            if (_solidBaker == null)
            {
                GameLog.Error(
                    "ElementWorldRuntime requires ElementFieldSolidBaker so Collider geometry can block Cell flow.",
                    "ElementField");
                return false;
            }

            try
            {
                _origin = transform.position;
                _effectiveWaterSimulationMode = ResolveEffectiveWaterSimulationMode();
                _fluidDepositSink = _gpuPbfFluidRuntime;
                _settings = _profile.CreateSimulationSettings();
                if (_settings.SimulateCellWater
                    != (_effectiveWaterSimulationMode == WaterSimulationMode.LegacyCell))
                {
                    // Unsupported Graphics 的回退是初始化期的 effective mode 决策；Profile 本身不被改写。
                    _settings = new ElementFieldSimulationSettings(
                        _settings.CellSize,
                        _settings.MaxDownFlowPerTick,
                        _settings.MaxLateralFlowPerTick,
                        _settings.FireDecayPerTick,
                        _settings.ChunkSize,
                        _effectiveWaterSimulationMode == WaterSimulationMode.LegacyCell,
                        _settings.Extinguish);
                }
                _store = new ElementWorldStore(
                    _profile.ChunkSize,
                    _profile.MaximumResidentChunks,
                    InitializeWorldChunk);
                _writeQueue = new ElementWriteQueue(_profile.MaxPendingWrites);
                if (_effectiveWaterSimulationMode == WaterSimulationMode.GpuPbf
                    && _fluidGameplayOccupancy != null
                    && _gpuPbfFluidRuntime != null)
                {
                    // Queue/Planner/Scratch 都在初始化边界创建；后续 10Hz Tick 只覆盖已有数组。
                    _liquidOccupancy = _fluidGameplayOccupancy;
                    _fluidReactionQueue = new FluidReactionCommandQueue(_profile.MaxPendingWrites);
                    _fluidFireReactionSystem = new FluidFireReactionSystem(_profile.MaxPendingWrites);
                    _fireReactionSamples = new FluidFireCellSample[_profile.MaxPendingWrites];
                    if (!_gpuPbfFluidRuntime.TryAttachReactionQueue(
                            _fluidReactionQueue,
                            _origin,
                            _settings.CellSize))
                    {
                        _liquidOccupancy = null;
                        _fluidReactionQueue = null;
                        _fluidFireReactionSystem = null;
                        _fireReactionSamples = null;
                    }
                }
                _simulator = new ElementWorldSimulator(
                    _profile.MaximumResidentChunks,
                    _profile.SettleAfterUnchangedTicks);
                _tickAccumulator = new ElementFieldTickAccumulator(
                    _profile.TickRate,
                    _profile.MaxCatchUpTicks);
                _tickInterval = 1f / _profile.TickRate;

                // 两个工作数组按 Resident 上限一次性分配；后续 Tick 只覆盖内容，不 new List/Array。
                _activeChunks = new ElementWorldChunk[_profile.MaximumResidentChunks];
                _reclaimKeys = new ElementChunkKey[_profile.MaximumResidentChunks];
                IsInitialized = true;

                RefreshInterestChunk();
                RebuildActivitySets();
                return true;
            }
            catch (Exception exception)
            {
                GameLog.Error(
                    $"ElementWorldRuntime initialization failed: {exception.Message}",
                    "ElementField");
                return false;
            }
        }

        private WaterSimulationMode ResolveEffectiveWaterSimulationMode()
        {
            if (_profile.WaterSimulationMode != WaterSimulationMode.GpuPbf)
                return WaterSimulationMode.LegacyCell;

            if (SystemInfo.supportsComputeShaders
                && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            {
                return WaterSimulationMode.GpuPbf;
            }

            if (!_hasReportedUnsupportedPbfFallback)
            {
                _hasReportedUnsupportedPbfFallback = true;
                GameLog.Warn(
                    "当前 Graphics Device 不支持 ComputeShader；Water 已在本次 Play 初始化回退为 Legacy Cell。",
                    "ElementField");
            }

            return WaterSimulationMode.LegacyCell;
        }

        private void InitializeWorldChunk(ElementWorldChunk chunk)
        {
            // Store 首次创建 Chunk 时同步 Bake，保证本 Tick 后续 Deposit/Flow 立刻看到正确 SolidMask。
            // Physics.CheckBox 的成本只在稀疏 Chunk 分配边界发生，不会污染常规 10 Hz Solver 热路径。
            using (BakeChunkSolidMarker.Auto())
            {
                _lastBakedSolidCellCount = _solidBaker.BakeWorldChunk(
                    chunk,
                    _origin,
                    _settings.CellSize);
            }
            _bakedChunkCount++;
        }

        private void SimulateOneTick()
        {
            _worldTick++;

            var writeStats = new ElementFieldSimulationStats
            {
                RejectedWrites = _writeQueue.TakeRejectedEnqueueCount(),
            };
            using (ApplyWritesMarker.Auto())
            {
                while (_writeQueue.TryDequeue(out ElementWriteRequest request))
                {
                    writeStats.ProcessedWrites++;
                    if (!ElementWorldWriteProcessor.TryApply(
                            _store,
                            in request,
                            _origin,
                            _settings.CellSize,
                            _worldTick,
                            out _))
                    {
                        writeStats.RejectedWrites++;
                    }
                }
            }

            // 新写入会创建/续租 Chunk，因此必须在模拟前重建一次 Active Snapshot。
            RebuildActivitySets();
            PlanAndApplyGpuWaterFireReactions();
            ElementFieldSimulationStats simulationStats;
            using (SimulateMarker.Auto())
            {
                simulationStats = _simulator.SimulateActiveChunks(
                    _store,
                    _activeChunks,
                    _activeChunkCount,
                    _worldTick,
                    _tickInterval,
                    in _settings);
            }
            simulationStats.ProcessedWrites = writeStats.ProcessedWrites;
            simulationStats.RejectedWrites = writeStats.RejectedWrites;
            _lastStats = simulationStats;

            // Solver 可能按需创建边界接收 Chunk；再次分类使 Inspector 与下一 Tick 立即看到真实生命周期。
            RebuildActivitySets();
        }

        private void PlanAndApplyGpuWaterFireReactions()
        {
            if (_effectiveWaterSimulationMode != WaterSimulationMode.GpuPbf
                || _liquidOccupancy == null
                || !_liquidOccupancy.HasValidSnapshot
                || _fluidReactionQueue == null
                || _fluidFireReactionSystem == null)
            {
                return;
            }

            SortActiveChunksForReaction();
            int fireCount = CollectFireReactionSamples();
            _fluidFireReactionSystem.TryPlanAndCommit(
                _liquidOccupancy,
                _fireReactionSamples,
                fireCount,
                Mathf.Max(_tickInterval, _fluidGameplayOccupancy.SnapshotIntervalSeconds),
                in _settings.Extinguish,
                _liquidOccupancy.AmountUnitsPerParticle,
                _fluidReactionQueue);

            while (_fluidReactionQueue.TryDequeueFireDelta(out ElementCellDeltaRequest delta))
                TryApplyInternalFireDelta(in delta);
        }

        private int CollectFireReactionSamples()
        {
            int count = 0;
            int size = _profile.ChunkSize;
            for (int chunkIndex = 0; chunkIndex < _activeChunkCount; chunkIndex++)
            {
                ElementWorldChunk chunk = _activeChunks[chunkIndex];
                for (int z = 0; z < size && count < _fireReactionSamples.Length; z++)
                for (int y = 0; y < size && count < _fireReactionSamples.Length; y++)
                for (int x = 0; x < size && count < _fireReactionSamples.Length; x++)
                {
                    var local = new Vector3Int(x, y, z);
                    ElementCell cell = chunk.GetCell(local);
                    if (cell.MaterialKind != ElementMaterialKind.Fire || cell.Amount == 0)
                        continue;
                    _fireReactionSamples[count++] = new FluidFireCellSample(
                        ElementWorldCoordinates.ComposeGlobalCell(chunk.Key, local, size),
                        cell.Amount);
                }
            }
            return count;
        }

        private bool TryApplyInternalFireDelta(in ElementCellDeltaRequest request)
        {
            if (request.ExpectedMaterial != ElementMaterialKind.Fire
                || request.AmountToRemove == 0
                || !TryResolveResidentCell(request.GlobalCell, out ElementWorldChunk chunk, out Vector3Int local))
            {
                return false;
            }

            ElementCell current = chunk.GetCell(local);
            if (current.MaterialKind != ElementMaterialKind.Fire || current.Amount == 0)
                return false;

            int remaining = Mathf.Max(0, current.Amount - request.AmountToRemove);
            chunk.SetCell(
                local,
                remaining == 0
                    ? default
                    : new ElementCell(ElementMaterialKind.Fire, (byte)remaining));
            chunk.Grid.MarkCellDirty(local);
            chunk.WakeForSimulation(_worldTick);
            return true;
        }

        private void SortActiveChunksForReaction()
        {
            for (int index = 1; index < _activeChunkCount; index++)
            {
                ElementWorldChunk value = _activeChunks[index];
                int insert = index - 1;
                while (insert >= 0 && CompareReactionKeys(_activeChunks[insert].Key, value.Key) > 0)
                {
                    _activeChunks[insert + 1] = _activeChunks[insert];
                    insert--;
                }
                _activeChunks[insert + 1] = value;
            }
        }

        private static int CompareReactionKeys(ElementChunkKey left, ElementChunkKey right)
        {
            int z = left.Z.CompareTo(right.Z);
            if (z != 0)
                return z;
            int y = left.Y.CompareTo(right.Y);
            return y != 0 ? y : left.X.CompareTo(right.X);
        }

        private void RefreshInterestChunk()
        {
            _interestGlobalCell = ElementWorldCoordinates.WorldToGlobalCell(
                _interestPoint.position,
                _origin,
                _profile.CellSize);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                _interestGlobalCell,
                _profile.ChunkSize,
                out ElementChunkKey nextInterestChunk,
                out _);

            if (_hasInterestChunk && nextInterestChunk == _interestChunk)
                return;

            _interestChunk = nextInterestChunk;
            _hasInterestChunk = true;
            _interestChunkKey = new Vector3Int(
                _interestChunk.X,
                _interestChunk.Y,
                _interestChunk.Z);

            // 跨 Chunk 时立即刷新，不必等待下一个 Simulation Tick 才更新调度窗口。
            RebuildActivitySets();
        }

        private void RebuildActivitySets()
        {
            if (!_hasInterestChunk || _store == null)
                return;

            using var marker = RebuildActivityMarker.Auto();
            _activeChunkCount = 0;
            _activeElementCellCount = 0;
            _sleepingChunkCount = 0;
            _reclaimedChunkCountLastTick = 0;
            int reclaimCount = 0;

            Dictionary<ElementChunkKey, ElementWorldChunk>.Enumerator enumerator =
                _store.Chunks.GetEnumerator();
            while (enumerator.MoveNext())
            {
                KeyValuePair<ElementChunkKey, ElementWorldChunk> pair = enumerator.Current;
                ElementWorldChunk chunk = pair.Value;
                bool insidePlayerRegion = ElementChunkActivityPlanner.IsInsideActiveRegion(
                    pair.Key,
                    _interestChunk,
                    _profile.ActiveRadiusXZChunks,
                    _profile.ActiveRadiusYChunks);
                ElementChunkActivityState state = ElementChunkActivityPlanner.Evaluate(
                    pair.Key,
                    _interestChunk,
                    chunk.HasAnyElement,
                    chunk.RequiresSimulation,
                    chunk.LastRelevantTick,
                    _worldTick,
                    _profile.SleepGraceTicks,
                    _profile.ActiveRadiusXZChunks,
                    _profile.ActiveRadiusYChunks);

                chunk.ActivityState = state;
                if (state == ElementChunkActivityState.Active)
                {
                    // 只有玩家兴趣范围或真实 Gameplay 事件可以续租。若 Grace Lease 自己每 Tick 都刷新时间，
                    // 远处 Chunk 会永远 Active，Sleep 将永远无法发生。
                    if (insidePlayerRegion)
                        chunk.MarkRelevant(_worldTick);
                    _activeChunks[_activeChunkCount++] = chunk;
                    _activeElementCellCount += chunk.NonEmptyCellCount;
                }
                else if (state == ElementChunkActivityState.Sleeping)
                {
                    _sleepingChunkCount++;
                }
                else
                {
                    _reclaimKeys[reclaimCount++] = pair.Key;
                }
            }
            enumerator.Dispose();

            // Dictionary 枚举过程中不能 Remove；先把 Key 写入预分配 Buffer，枚举结束后再统一回收。
            for (int i = 0; i < reclaimCount; i++)
            {
                if (_store.TryRemoveEmptyChunk(_reclaimKeys[i]))
                    _reclaimedChunkCountLastTick++;
            }

            _residentChunkCount = _store.ResidentChunkCount;
        }

        private bool HasSupportedTransform()
        {
            bool unitScale = (transform.lossyScale - Vector3.one).sqrMagnitude
                <= TransformTolerance * TransformTolerance;
            bool identityRotation = Quaternion.Angle(transform.rotation, Quaternion.identity)
                <= TransformTolerance;
            return unitScale && identityRotation;
        }

        private bool TryResolveResidentCell(
            Vector3Int globalCell,
            out ElementWorldChunk chunk,
            out Vector3Int localCell)
        {
            if (!IsInitialized || _store == null)
            {
                chunk = null;
                localCell = default;
                return false;
            }

            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell,
                _profile.ChunkSize,
                out ElementChunkKey key,
                out localCell);
            return _store.TryGetChunk(key, out chunk);
        }

        private int CompareVisibleChunkKeys(ElementChunkKey left, ElementChunkKey right)
        {
            // View Cap 小于可见元素 Chunk 数时优先保留离 Interest Point 更近的 Chunk。
            // Manhattan Distance 只做整数加法，不需要 sqrt，且在当前 Axis-Aligned Interest Box 中稳定。
            long leftDistance = Math.Abs((long)left.X - _interestChunk.X)
                + Math.Abs((long)left.Y - _interestChunk.Y)
                + Math.Abs((long)left.Z - _interestChunk.Z);
            long rightDistance = Math.Abs((long)right.X - _interestChunk.X)
                + Math.Abs((long)right.Y - _interestChunk.Y)
                + Math.Abs((long)right.Z - _interestChunk.Z);
            int distance = leftDistance.CompareTo(rightDistance);
            if (distance != 0)
                return distance;

            int z = left.Z.CompareTo(right.Z);
            if (z != 0)
                return z;
            int y = left.Y.CompareTo(right.Y);
            return y != 0 ? y : left.X.CompareTo(right.X);
        }
    }
}

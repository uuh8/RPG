using System;
using Game.Core;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 流体 Chunk Streaming 的 Unity 生命周期边界。GPU 归档期间仍是权威；只有 Readback 完整、
    /// RAM Store 原子提交成功后才释放 slot。回调只登记结果，世界权威变更统一在 Update 执行。
    /// </summary>
    [DefaultExecutionOrder(50)]
    [DisallowMultipleComponent]
    public sealed class FluidChunkStreamingRuntime : MonoBehaviour, IFluidDepositSink,
        ILiquidOccupancyReadOnly, IFluidDormantReadOnly, IFluidDormantWakeSink
    {
        private static readonly ProfilerMarker PlanMarker =
            new ProfilerMarker("GpuFluid.Streaming.Plan");
        private static readonly ProfilerMarker ArchiveBuildMarker =
            new ProfilerMarker("GpuFluid.Streaming.ArchiveBuild");
        private static readonly ProfilerMarker ArchiveCommitMarker =
            new ProfilerMarker("GpuFluid.Streaming.ArchiveCommit");
        private static readonly ProfilerMarker RestoreExpandMarker =
            new ProfilerMarker("GpuFluid.Streaming.RestoreExpand");
        private static readonly ProfilerMarker PendingReplayMarker =
            new ProfilerMarker("GpuFluid.Streaming.PendingReplay");

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private FluidChunkLifecycleState _debugState;
        [SerializeField] private uint _debugTransactionId;
        [SerializeField] private int _debugArchivedChunkCount;
        [SerializeField] private int _debugArchivedCellRecordCount;
        [SerializeField] private ulong _debugArchivedParticleCount;
        [SerializeField] private ulong _debugRestoredParticleCount;
        [SerializeField] private int _debugPendingWriteCount;
        [SerializeField] private uint _debugRejectedPendingWriteCount;
        [SerializeField] private uint _debugCompletedArchiveCount;
        [SerializeField] private uint _debugArchiveRollbackCount;
        [SerializeField] private uint _debugArchiveReadbackErrorCount;
        [SerializeField] private uint _debugCompletedRestoreCount;
        [SerializeField] private uint _debugRestoreRollbackCount;
        [SerializeField] private uint _debugRestoreReadbackErrorCount;
        [SerializeField] private uint _debugCapacityPressureCount;
        [SerializeField] private uint _debugOversizedChunkRestoreCount;
        [SerializeField] private int _debugLastArchiveReadbackBytes;
        [SerializeField] private float _debugLastArchiveDurationMilliseconds;
        [SerializeField] private int _debugLastRestoreParticleCount;
        [SerializeField] private float _debugLastRestoreDurationMilliseconds;
        [SerializeField] private uint _debugLocalDormancyArchiveCount;
        [SerializeField] private uint _debugDormantWakeRequestCount;
        [SerializeField] private FluidDormantWakeReason _debugLastDormantWakeReason;
        [SerializeField] private int _debugRetainedAliveSamples;
        [SerializeField] private int _debugRetainedSpeedBelow01;
        [SerializeField] private int _debugRetainedSpeedBelow05;
        [SerializeField] private int _debugRetainedSpeedBelow10;
        [SerializeField] private int _debugRetainedSpeedBelow25;
        [SerializeField] private float _debugRetainedMaximumSpeed;

        private FluidChunkStreamingSettings _settings;
        private IFluidChunkTransferBackend _backend;
        private IFluidDepositSink _downstream;
        private FluidChunkArchiveBuilder _builder;
        private FluidChunkArchiveStore _store;
        private FluidPendingWriteQueue _pendingWrites;
        private FluidChunkTransferState _transferState;
        private IFluidArchiveReadbackScheduler _readbackScheduler;
        private IFluidRestoreStatusReadbackScheduler _restoreStatusScheduler;
        private NativeArray<FluidGpuArchiveSample> _archiveSamples;
        private NativeArray<FluidGpuRestoreParticle> _restoreParticles;
        private NativeArray<FluidGpuTransferStatus> _restoreStatus;
        private FluidArchiveCellRecord[] _restoreRecords;
        private FluidGpuRestoreParticle[] _restoreParticleStaging;
        private Action<bool> _readbackCallback;
        private Action<bool> _restoreStatusCallback;
        private FluidArchiveReadbackLease _lease;
        private FluidRestoreStatusReadbackLease _restoreLease;
        private LiquidMaterialAmountScaleSnapshot _amountScales;
        private ILiquidOccupancyReadOnly _gameplayOccupancy;
        private IFluidGameplayTopologyReadOnly _gameplayTopology;
        private ElementChunkKey _restoringChunk;
        private ElementChunkKey _awaitingGameplayChunk;
        private uint _awaitingGameplayTopologyVersion;
        private int _restoreParticleCount;
        private int _restoreRecordCount;
        private int _restoreRecordIndex;
        private int _restoreRecordParticleOffset;
        private int _restorePreparedParticleCount;
        private uint _restoreSnapshotVersion;
        private uint _restorePreparationTransactionId;
        private bool _awaitingGameplayPublish;
        private bool _restorePreparing;
        private bool _hasRequestedRestoreChunk;
        private ElementChunkKey _requestedRestoreChunk;
        private Vector3 _worldOrigin;
        private float _cellSize;
        private int _chunkSize;
        private int _activeRadiusXZ;
        private int _activeRadiusY;
        private int _particleCapacity;
        private ElementChunkKey _interestChunk;
        private FluidChunkRegion _retainedRegion;
        private bool _hasInterestChunk;
        private bool _hasBackendLease;
        private bool _archiveAfterInterestChange;
        private bool _archiveIncludesSleepingRetained;
        private bool _isInitialized;
        private double _interestChangedAt;
        private double _nextArchiveAttemptAt;
        private double _archiveStartedAt;
        private double _restoreStartedAt;
        private uint _nextTransactionId;

        public bool IsFluidInitialized => _isInitialized && _downstream != null
            && _downstream.IsFluidInitialized;
        public int ArchivedChunkCount => _store != null ? _store.ChunkCount : 0;
        public int ArchivedCellRecordCount => _store != null ? _store.RecordCount : 0;
        public int PendingWriteCount => _pendingWrites != null ? _pendingWrites.Count : 0;
        public uint RejectedPendingWriteCount => _pendingWrites != null ? _pendingWrites.RejectedCount : 0u;
        public uint CurrentTransactionId => _transferState != null ? _transferState.TransactionId : 0u;
        public ulong ArchivedParticleCount => _debugArchivedParticleCount;
        public ulong RestoredParticleCount => _debugRestoredParticleCount;
        public uint ArchiveReadbackErrorCount => _debugArchiveReadbackErrorCount;
        public uint RestoreReadbackErrorCount => _debugRestoreReadbackErrorCount;
        public uint ArchiveRollbackCount => _debugArchiveRollbackCount;
        public uint RestoreRollbackCount => _debugRestoreRollbackCount;
        public uint CapacityPressureCount => _debugCapacityPressureCount;
        public uint OversizedChunkRestoreCount => _debugOversizedChunkRestoreCount;
        public uint CompletedArchiveCount => _debugCompletedArchiveCount;
        public uint CompletedRestoreCount => _debugCompletedRestoreCount;
        public int LastArchiveReadbackBytes => _debugLastArchiveReadbackBytes;
        public float LastArchiveDurationMilliseconds => _debugLastArchiveDurationMilliseconds;
        public int LastRestoreParticleCount => _debugLastRestoreParticleCount;
        public float LastRestoreDurationMilliseconds => _debugLastRestoreDurationMilliseconds;
        public int DormantChunkCount => _store != null ? _store.DormantChunkCount : 0;
        public int DormantCellRecordCount => _store != null ? _store.DormantRecordCount : 0;
        public ulong DormantAmount => _store != null ? _store.DormantAmount : 0u;
        public uint LocalDormancyArchiveCount => _debugLocalDormancyArchiveCount;
        public uint DormantWakeRequestCount => _debugDormantWakeRequestCount;
        public FluidDormantWakeReason LastDormantWakeReason => _debugLastDormantWakeReason;
        public bool IsRestorePreparing => _restorePreparing;
        public int RestorePreparedParticleCount => _restorePreparedParticleCount;
        public int RetainedAliveSamples => _debugRetainedAliveSamples;
        public int RetainedSpeedBelow01 => _debugRetainedSpeedBelow01;
        public int RetainedSpeedBelow05 => _debugRetainedSpeedBelow05;
        public int RetainedSpeedBelow10 => _debugRetainedSpeedBelow10;
        public int RetainedSpeedBelow25 => _debugRetainedSpeedBelow25;
        public float RetainedMaximumSpeed => _debugRetainedMaximumSpeed;
        public FluidChunkLifecycleState State => _debugState;
        public bool HasDormantData => _store != null && _store.DormantChunkCount > 0;
        public uint DormantVersion => _store != null ? _store.Version : 0u;
        public int MaximumDormantCellRecords => _settings.MaximumArchivedCellRecords;
        public bool HasValidSnapshot => _isInitialized && _hasInterestChunk
            && ((_gameplayOccupancy != null && _gameplayOccupancy.HasValidSnapshot)
                || (_store != null && _store.ChunkCount > 0));
        public Bounds SnapshotBounds => _isInitialized && _hasInterestChunk
            ? _retainedRegion.ToWorldBounds(_worldOrigin, _cellSize, _chunkSize)
            : default;
        public uint SnapshotVersion => _gameplayOccupancy != null
            ? _gameplayOccupancy.SnapshotVersion
            : 0u;

        internal bool TryInitialize(
            in FluidChunkStreamingSettings settings,
            GpuPbfFluidRuntime gpuRuntime,
            Vector3 worldOrigin,
            float cellSize,
            int chunkSize,
            int activeRadiusXZ,
            int activeRadiusY,
            FluidGameplayOccupancyBridge gameplayOccupancy = null)
        {
            return TryInitialize(
                in settings,
                gpuRuntime,
                gpuRuntime,
                new UnityFluidArchiveReadbackScheduler(),
                new UnityFluidRestoreStatusReadbackScheduler(),
                gameplayOccupancy,
                gameplayOccupancy,
                worldOrigin,
                cellSize,
                chunkSize,
                activeRadiusXZ,
                activeRadiusY);
        }

        internal bool TryInitialize(
            in FluidChunkStreamingSettings settings,
            IFluidChunkTransferBackend backend,
            IFluidDepositSink downstream,
            IFluidArchiveReadbackScheduler readbackScheduler,
            Vector3 worldOrigin,
            float cellSize,
            int chunkSize,
            int activeRadiusXZ,
            int activeRadiusY)
        {
            return TryInitialize(in settings, backend, downstream, readbackScheduler,
                new UnityFluidRestoreStatusReadbackScheduler(), null, null, worldOrigin, cellSize,
                chunkSize, activeRadiusXZ, activeRadiusY);
        }

        internal bool TryInitialize(
            in FluidChunkStreamingSettings settings,
            IFluidChunkTransferBackend backend,
            IFluidDepositSink downstream,
            IFluidArchiveReadbackScheduler readbackScheduler,
            IFluidRestoreStatusReadbackScheduler restoreStatusScheduler,
            ILiquidOccupancyReadOnly gameplayOccupancy,
            IFluidGameplayTopologyReadOnly gameplayTopology,
            Vector3 worldOrigin,
            float cellSize,
            int chunkSize,
            int activeRadiusXZ,
            int activeRadiusY)
        {
            if (_isInitialized) return true;
            if (!settings.Enabled || backend == null || downstream == null || readbackScheduler == null
                || restoreStatusScheduler == null
                || cellSize <= 0f || chunkSize <= 0
                || activeRadiusXZ < 0 || activeRadiusY < 0)
                return false;

            _backend = backend;
            _downstream = downstream;
            _readbackScheduler = readbackScheduler;
            _restoreStatusScheduler = restoreStatusScheduler;
            _gameplayOccupancy = gameplayOccupancy;
            _gameplayTopology = gameplayTopology;
            if (!_backend.TryGetParticleCapacity(out _particleCapacity) || _particleCapacity <= 0)
                return false;

            try
            {
                _settings = settings;
                _worldOrigin = worldOrigin;
                _cellSize = cellSize;
                _chunkSize = chunkSize;
                _activeRadiusXZ = activeRadiusXZ;
                _activeRadiusY = activeRadiusY;
                _builder = new FluidChunkArchiveBuilder(_particleCapacity);
                _store = new FluidChunkArchiveStore(
                    settings.MaximumArchivedChunks,
                    settings.MaximumArchivedCellRecords,
                    chunkSize);
                _pendingWrites = new FluidPendingWriteQueue(settings.MaximumPendingWrites);
                _transferState = new FluidChunkTransferState();
                _archiveSamples = new NativeArray<FluidGpuArchiveSample>(
                    _particleCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _restoreParticles = new NativeArray<FluidGpuRestoreParticle>(
                    _particleCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _restoreStatus = new NativeArray<FluidGpuTransferStatus>(
                    1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                _restoreRecords = new FluidArchiveCellRecord[_particleCapacity];
                _restoreParticleStaging = new FluidGpuRestoreParticle[_particleCapacity];
                _readbackCallback = OnReadbackCompleted;
                _restoreStatusCallback = OnRestoreStatusCompleted;
                _debugState = FluidChunkLifecycleState.GpuResident;
                _isInitialized = true;
                return true;
            }
            catch (Exception)
            {
                DisposeNativeStorage();
                _backend = null;
                _downstream = null;
                _readbackScheduler = null;
                _restoreStatusScheduler = null;
                return false;
            }
        }

        internal void SetInterestChunk(ElementChunkKey interestChunk)
        {
            SetInterestChunkAt(interestChunk, Time.realtimeSinceStartupAsDouble);
        }

        internal void SetInterestChunkAt(ElementChunkKey interestChunk, double now)
        {
            if (!_isInitialized) return;
            if (_hasInterestChunk && _interestChunk == interestChunk) return;
            _retainedRegion = FluidChunkStreamingPlanner.BuildWarmRegion(
                interestChunk, _activeRadiusXZ, _activeRadiusY, _settings.WarmPaddingChunks);
            _interestChunk = interestChunk;
            _hasInterestChunk = true;
            _archiveAfterInterestChange = true;
            _interestChangedAt = now;
        }

        public bool TryEnqueueDeposit(in ElementWriteRequest request)
        {
            if (!IsFluidInitialized) return false;
            if (MustDelayWrite(in request))
            {
                bool accepted = _pendingWrites.TryEnqueue(in request);
                _debugPendingWriteCount = _pendingWrites.Count;
                _debugRejectedPendingWriteCount = _pendingWrites.RejectedCount;
                return accepted;
            }
            return _downstream.TryEnqueueDeposit(in request);
        }

        private void Update()
        {
            Tick(Time.realtimeSinceStartupAsDouble, true);
        }

        internal void TickForTests(double now)
        {
            Tick(now, false);
        }

        private void Tick(double now, bool respectPause)
        {
            using var marker = PlanMarker.Auto();
            if (!_isInitialized) return;
            ProcessReadbackCompletion();
            FinishRestoreAfterGameplayPublish();
            ReplayPendingWrites();
            if (!_hasInterestChunk || _transferState.IsInFlight || (respectPause && Time.timeScale <= 0f))
                return;

            if (_restorePreparing)
            {
                ContinueRestorePreparation();
                return;
            }

            if (!_awaitingGameplayPublish && _hasRequestedRestoreChunk
                && _store.ContainsChunk(_requestedRestoreChunk))
            {
                StartRestorePreparation(_requestedRestoreChunk);
                return;
            }

            if (!_awaitingGameplayPublish
                && _store.TryFindNearestChunk(
                    _interestChunk, in _retainedRegion, FluidArchivedChunkKind.Cold,
                    false, out ElementChunkKey restoreChunk))
            {
                StartRestorePreparation(restoreChunk);
                return;
            }

            if (now < _nextArchiveAttemptAt) return;
            _backend.TryGetFreeParticleCount(out uint freeParticles);
            if (freeParticles <= (uint)_settings.GameplaySpawnReserveParticles
                && _debugCapacityPressureCount < uint.MaxValue)
                _debugCapacityPressureCount++;
            float sinceChange = (float)Math.Max(0d, now - _interestChangedAt);
            if (!FluidChunkStreamingPlanner.ShouldStartArchive(
                    _archiveAfterInterestChange,
                    sinceChange,
                    _settings.ArchiveGraceSeconds,
                    freeParticles,
                    _settings.GameplaySpawnReserveParticles,
                    false))
                return;

            bool includeSleepingRetained =
                FluidChunkStreamingPlanner.ShouldIncludeSleepingRetained(
                    _settings.EnableLocalDormancy,
                    freeParticles,
                    _settings.GameplaySpawnReserveParticles,
                    _backend.TryGetActivityCounts(out uint awakeParticles, out _),
                    awakeParticles);
            BeginArchive(now, includeSleepingRetained);
        }

        private void BeginArchive(double now, bool includeSleepingRetained)
        {
            uint transactionId = NextTransactionId();
            if (!_transferState.TryBegin(transactionId)) return;
            _debugTransactionId = transactionId;

            Bounds retainedBounds = _retainedRegion.ToWorldBounds(_worldOrigin, _cellSize, _chunkSize);
            if (!_backend.TryAcquireArchiveReadbackLease(
                    retainedBounds, _worldOrigin, _cellSize, _chunkSize,
                    includeSleepingRetained, transactionId, out _lease))
            {
                // Backend 没有交出 Lease 时不能调用 Cancel/Release；这里只结束 CPU 状态并稍后重试。
                _transferState.RecordCompletion(transactionId, true);
                _transferState.Finish(transactionId);
                if (_debugArchiveRollbackCount < uint.MaxValue) _debugArchiveRollbackCount++;
                _debugTransactionId = 0u;
                _nextArchiveAttemptAt = now + 0.5d;
                return;
            }

            _hasBackendLease = true;
            _archiveIncludesSleepingRetained = includeSleepingRetained;
            _debugState = FluidChunkLifecycleState.Archiving;
            _archiveStartedAt = Time.realtimeSinceStartupAsDouble;
            _debugLastArchiveReadbackBytes = checked(_particleCapacity * FluidGpuArchiveSample.Stride);
            _archiveAfterInterestChange = false;
            try
            {
                _readbackScheduler.Request(
                    ref _archiveSamples, _lease.Samples, _readbackCallback);
            }
            catch (Exception)
            {
                _transferState.RecordCompletion(transactionId, true);
                ProcessReadbackCompletion();
            }
            // 无可归档粒子或 Driver 瞬时失败时避免容量压力导致每帧重试。
            _nextArchiveAttemptAt = now + 0.5d;
        }

        private void OnReadbackCompleted(bool hasError)
        {
            if (!_isInitialized || !_transferState.IsInFlight) return;
            _transferState.RecordCompletion(_transferState.TransactionId, hasError);
        }

        private void OnRestoreStatusCompleted(bool hasError)
        {
            if (!_isInitialized || !_transferState.IsInFlight
                || _transferState.Kind != FluidChunkTransferKind.Restore) return;
            _transferState.RecordCompletion(_transferState.TransactionId, hasError);
        }

        private void ProcessReadbackCompletion()
        {
            if (!_transferState.TryConsumeCompletion(out uint transactionId, out bool hasError))
                return;

            if (_transferState.Kind == FluidChunkTransferKind.Restore)
            {
                ProcessRestoreCompletion(transactionId, hasError);
                return;
            }

            bool committed = false;
            int archivedParticles = 0;
            try
            {
                if (hasError || _lease.LayoutVersion != FluidGpuLayout.LayoutVersion)
                {
                    if (hasError && _debugArchiveReadbackErrorCount < uint.MaxValue)
                        _debugArchiveReadbackErrorCount++;
                }
                else
                {
                    UpdateRetainedSpeedDiagnostics();
                    var context = new FluidArchiveBuildContext(
                        _lease.WorldOrigin, _lease.CellSize, _lease.ChunkSize, _lease.AmountScales);
                    using (ArchiveBuildMarker.Auto())
                    {
                        committed = !_archiveIncludesSleepingRetained
                            || HasExclusiveRetainedArchiveAuthority();
                        if (committed)
                            committed = _builder.TryRebuild(_archiveSamples, in context);
                        if (committed && _builder.RecordCount == 0)
                            committed = false;
                        if (committed)
                        {
                            for (int i = 0; i < _archiveSamples.Length; i++)
                                if ((_archiveSamples[i].Flags
                                    & (FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked))
                                    == (FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked))
                                    archivedParticles++;
                            committed = _store.TryCommit(
                                _builder, transactionId, in _retainedRegion);
                            if (!committed && _debugCapacityPressureCount < uint.MaxValue)
                                _debugCapacityPressureCount++;
                        }
                    }
                    if (committed) _amountScales = _lease.AmountScales;
                }

                if (committed)
                {
                    using (ArchiveCommitMarker.Auto())
                        _backend.CommitArchiveAndRelease(transactionId);
                    _debugArchivedParticleCount += (uint)archivedParticles;
                    if (_debugCompletedArchiveCount < uint.MaxValue) _debugCompletedArchiveCount++;
                    if (_archiveIncludesSleepingRetained
                        && _debugLocalDormancyArchiveCount < uint.MaxValue)
                        _debugLocalDormancyArchiveCount++;
                }
                else
                {
                    _backend.CancelArchive(transactionId);
                    if (_debugArchiveRollbackCount < uint.MaxValue) _debugArchiveRollbackCount++;
                }
            }
            finally
            {
                if (_hasBackendLease)
                    _backend.ReleaseTransferReadbackLease();
                _hasBackendLease = false;
                _transferState.Finish(transactionId);
                _debugTransactionId = 0u;
                _lease = default;
                _archiveIncludesSleepingRetained = false;
                _debugLastArchiveDurationMilliseconds = ElapsedMilliseconds(_archiveStartedAt);
                _debugState = _store.ChunkCount > 0
                    ? FluidChunkLifecycleState.Cold
                    : FluidChunkLifecycleState.GpuResident;
                _debugArchivedChunkCount = _store.ChunkCount;
                _debugArchivedCellRecordCount = _store.RecordCount;
            }
        }

        private bool HasExclusiveRetainedArchiveAuthority()
        {
            Bounds retained = _lease.RetainedBounds;
            for (int i = 0; i < _archiveSamples.Length; i++)
            {
                FluidGpuArchiveSample sample = _archiveSamples[i];
                if ((sample.Flags & FluidGpuLayout.AliveFlag) == 0u
                    || !retained.Contains(sample.Position))
                    continue;
                if ((sample.Flags & FluidActivityFlags.ArchiveLocked) == 0u)
                    return false;
            }
            return true;
        }

        private void UpdateRetainedSpeedDiagnostics()
        {
            int alive = 0, below01 = 0, below05 = 0, below10 = 0, below25 = 0;
            float maximum = 0f;
            Bounds retained = _lease.RetainedBounds;
            for (int i = 0; i < _archiveSamples.Length; i++)
            {
                FluidGpuArchiveSample sample = _archiveSamples[i];
                if ((sample.Flags & FluidGpuLayout.AliveFlag) == 0u
                    || !retained.Contains(sample.Position))
                    continue;
                float speed = sample.Velocity.magnitude;
                alive++;
                if (speed <= .01f) below01++;
                if (speed <= .05f) below05++;
                if (speed <= .10f) below10++;
                if (speed <= .25f) below25++;
                if (speed > maximum) maximum = speed;
            }
            _debugRetainedAliveSamples = alive;
            _debugRetainedSpeedBelow01 = below01;
            _debugRetainedSpeedBelow05 = below05;
            _debugRetainedSpeedBelow10 = below10;
            _debugRetainedSpeedBelow25 = below25;
            _debugRetainedMaximumSpeed = maximum;
        }

        private void StartRestorePreparation(ElementChunkKey chunk)
        {
            if (_amountScales == null || !_store.TryGetChunkVersion(chunk, out uint snapshotVersion))
                return;
            int recordCount = _store.CopyChunkRecords(chunk, _restoreRecords);
            if (recordCount <= 0) return;
            _restoringChunk = chunk;
            _restoreSnapshotVersion = snapshotVersion;
            _restorePreparationTransactionId = NextTransactionId();
            _restoreRecordCount = recordCount;
            _restoreRecordIndex = 0;
            _restoreRecordParticleOffset = 0;
            _restorePreparedParticleCount = 0;
            _restorePreparing = true;
            _hasRequestedRestoreChunk = false;
            _debugState = FluidChunkLifecycleState.Restoring;
            _restoreStartedAt = Time.realtimeSinceStartupAsDouble;
            ContinueRestorePreparation();
        }

        private void ContinueRestorePreparation()
        {
            int budget = _settings.MaximumRestoreParticlesPerFrame;
            using (RestoreExpandMarker.Auto())
            {
                while (budget > 0 && _restoreRecordIndex < _restoreRecordCount)
                {
                    FluidArchiveCellRecord record = _restoreRecords[_restoreRecordIndex];
                    if (!_amountScales.TryGet(record.Material, out uint scale))
                    {
                        CancelRestorePreparation();
                        return;
                    }
                    int expanded = FluidChunkRestorePlanner.ExpandRange(
                        in record, scale, _worldOrigin, _cellSize, _chunkSize,
                        _restoreSnapshotVersion, _restorePreparationTransactionId,
                        _restoreRecordParticleOffset, budget,
                        _restoreParticleStaging, _restorePreparedParticleCount);
                    if (expanded < 0)
                    {
                        if (_debugOversizedChunkRestoreCount < uint.MaxValue)
                            _debugOversizedChunkRestoreCount++;
                        CancelRestorePreparation();
                        return;
                    }
                    _restorePreparedParticleCount += expanded;
                    budget -= expanded;
                    int recordParticles = (int)(record.Amount / scale);
                    _restoreRecordParticleOffset += expanded;
                    if (_restoreRecordParticleOffset >= recordParticles)
                    {
                        _restoreRecordIndex++;
                        _restoreRecordParticleOffset = 0;
                    }
                }
            }
            if (_restoreRecordIndex < _restoreRecordCount) return;
            int total = _restorePreparedParticleCount;
            uint transactionId = _restorePreparationTransactionId;
            _restorePreparing = false;
            if (total <= 0 || total > _particleCapacity)
            {
                if (_debugOversizedChunkRestoreCount < uint.MaxValue)
                    _debugOversizedChunkRestoreCount++;
                CancelRestorePreparation();
                return;
            }
            for (int i = 0; i < total; i++) _restoreParticles[i] = _restoreParticleStaging[i];
            if (!_transferState.TryBegin(transactionId, FluidChunkTransferKind.Restore)) return;
            _debugTransactionId = transactionId;
            if (!_backend.TryAcquireRestoreStatusLease(
                    _restoreParticles, total, _settings.GameplaySpawnReserveParticles,
                    transactionId, out _restoreLease))
            {
                _transferState.RecordCompletion(transactionId, true);
                _transferState.Finish(transactionId);
                if (_debugRestoreRollbackCount < uint.MaxValue) _debugRestoreRollbackCount++;
                _debugTransactionId = 0u;
                return;
            }
            _hasBackendLease = true;
            _restoreParticleCount = total;
            _debugLastRestoreParticleCount = total;
            try
            {
                _restoreStatusScheduler.Request(
                    ref _restoreStatus, _restoreLease.Status, _restoreStatusCallback);
            }
            catch (Exception)
            {
                _transferState.RecordCompletion(transactionId, true);
                ProcessReadbackCompletion();
            }
        }

        private void CancelRestorePreparation()
        {
            _restorePreparing = false;
            _restoreRecordCount = 0;
            _restoreRecordIndex = 0;
            _restoreRecordParticleOffset = 0;
            _restorePreparedParticleCount = 0;
            _restorePreparationTransactionId = 0u;
            _debugState = _store != null && _store.ChunkCount > 0
                ? FluidChunkLifecycleState.Cold
                : FluidChunkLifecycleState.GpuResident;
        }

        private void ProcessRestoreCompletion(uint transactionId, bool hasError)
        {
            FluidGpuTransferStatus status = _restoreStatus[0];
            bool valid = !hasError && status.Succeeded == 1u
                && status.TransactionId == transactionId
                && status.ParticleCount == (uint)_restoreParticleCount;
            try
            {
                if (valid)
                {
                    _backend.CommitRestore(
                        transactionId, _restoreParticleCount, out _awaitingGameplayTopologyVersion);
                    _awaitingGameplayChunk = _restoringChunk;
                    _awaitingGameplayPublish = true;
                    _debugRestoredParticleCount += (uint)_restoreParticleCount;
                    if (_debugCompletedRestoreCount < uint.MaxValue) _debugCompletedRestoreCount++;
                }
                else
                {
                    _backend.RollbackRestore(transactionId, _restoreParticleCount);
                    if (hasError && _debugRestoreReadbackErrorCount < uint.MaxValue)
                        _debugRestoreReadbackErrorCount++;
                    if (_debugRestoreRollbackCount < uint.MaxValue) _debugRestoreRollbackCount++;
                }
            }
            finally
            {
                _backend.ReleaseTransferReadbackLease();
                _hasBackendLease = false;
                _transferState.Finish(transactionId);
                _debugTransactionId = 0u;
                _restoreLease = default;
                _debugLastRestoreDurationMilliseconds = ElapsedMilliseconds(_restoreStartedAt);
                _restoreParticleCount = 0;
                if (!_awaitingGameplayPublish) _debugState = FluidChunkLifecycleState.Cold;
            }
        }

        private void FinishRestoreAfterGameplayPublish()
        {
            if (!_awaitingGameplayPublish) return;
            if (_gameplayTopology != null
                && (!_gameplayTopology.HasValidSnapshot
                    || _gameplayTopology.TopologyVersion < _awaitingGameplayTopologyVersion))
                return;
            _store.RemoveChunk(_awaitingGameplayChunk);
            _debugArchivedParticleCount = _debugArchivedParticleCount >= (uint)_debugLastRestoreParticleCount
                ? _debugArchivedParticleCount - (uint)_debugLastRestoreParticleCount
                : 0u;
            _awaitingGameplayPublish = false;
            _debugArchivedChunkCount = _store.ChunkCount;
            _debugArchivedCellRecordCount = _store.RecordCount;
            _debugState = _store.ChunkCount > 0
                ? FluidChunkLifecycleState.Cold
                : FluidChunkLifecycleState.GpuResident;
        }

        public bool TryGetAmount(
            Vector3Int globalCell, Game.Materials.MaterialId materialKind, out byte amount)
        {
            amount = 0;
            if (!_isInitialized || !_hasInterestChunk) return false;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell, _chunkSize, out ElementChunkKey chunk, out _);
            if (_store.ContainsChunk(chunk))
            {
                // Cold 区域离屏暂停 Gameplay；进入 Warm 后 Archive 在 Topology Gate 完成前继续是权威。
                if (!_retainedRegion.Contains(chunk)) return false;
                _store.TryGetAmount(globalCell, materialKind, out uint archiveAmount);
                amount = (byte)Math.Min(byte.MaxValue, archiveAmount);
                return true;
            }
            return _gameplayOccupancy != null
                && _gameplayOccupancy.TryGetAmount(globalCell, materialKind, out amount);
        }

        public bool TryGetAmountUnitsPerParticle(
            Game.Materials.MaterialId material, out uint amountUnits)
        {
            if (_gameplayOccupancy != null
                && _gameplayOccupancy.TryGetAmountUnitsPerParticle(material, out amountUnits))
                return true;
            amountUnits = 0u;
            return _amountScales != null && _amountScales.TryGet(material, out amountUnits);
        }

        public int CopyOccupiedCells(
            Game.Materials.MaterialId material, LiquidMaterialCellSample[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!_isInitialized || destination.Length == 0) return 0;
            int count = _gameplayOccupancy != null
                ? _gameplayOccupancy.CopyOccupiedCells(material, destination)
                : 0;

            // GPU Snapshot 可能已经包含刚 Activate 的粒子；Archive Authority 未交接前先原地剔除该 Chunk。
            int write = 0;
            for (int i = 0; i < count; i++)
            {
                ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                    destination[i].GlobalCell, _chunkSize, out ElementChunkKey chunk, out _);
                if (_store.ContainsChunk(chunk) && _retainedRegion.Contains(chunk)) continue;
                destination[write++] = destination[i];
            }
            count = write;

            if (_store.TryFindNearestChunk(
                    _interestChunk, in _retainedRegion, FluidArchivedChunkKind.Cold,
                    true, out ElementChunkKey archiveChunk))
            {
                int records = _store.CopyChunkRecords(archiveChunk, _restoreRecords);
                for (int i = 0; i < records && count < destination.Length; i++)
                {
                    FluidArchiveCellRecord record = _restoreRecords[i];
                    if (record.Material != material) continue;
                    destination[count++] = new LiquidMaterialCellSample(
                        ToGlobalCell(record), (byte)Math.Min(byte.MaxValue, record.Amount));
                }
            }
            return count;
        }

        public int CopyDormantCells(
            Game.Materials.MaterialId material,
            LiquidMaterialCellSample[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            return _isInitialized && _hasInterestChunk
                ? _store.CopyCells(
                    in _retainedRegion, FluidArchivedChunkKind.Dormant, material, destination)
                : 0;
        }

        public bool RequestWake(Vector3Int globalCell, FluidDormantWakeReason reason)
        {
            if (!_isInitialized || !_hasInterestChunk) return false;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell, _chunkSize, out ElementChunkKey chunk, out _);
            return RequestWakeChunk(chunk, reason);
        }

        private bool RequestWakeChunk(ElementChunkKey chunk, FluidDormantWakeReason reason)
        {
            if (!_retainedRegion.Contains(chunk)
                || !_store.TryGetChunkKind(chunk, out FluidArchivedChunkKind kind)
                || kind != FluidArchivedChunkKind.Dormant)
                return false;
            _requestedRestoreChunk = chunk;
            _hasRequestedRestoreChunk = true;
            _debugLastDormantWakeReason = reason;
            if (_debugDormantWakeRequestCount < uint.MaxValue)
                _debugDormantWakeRequestCount++;
            return true;
        }

        private Vector3Int ToGlobalCell(in FluidArchiveCellRecord record)
        {
            int layer = _chunkSize * _chunkSize;
            int localZ = record.LocalCellIndex / layer;
            int remainder = record.LocalCellIndex - localZ * layer;
            int localY = remainder / _chunkSize;
            int localX = remainder - localY * _chunkSize;
            return new Vector3Int(
                checked(record.Chunk.X * _chunkSize + localX),
                checked(record.Chunk.Y * _chunkSize + localY),
                checked(record.Chunk.Z * _chunkSize + localZ));
        }

        private void ReplayPendingWrites()
        {
            using var marker = PendingReplayMarker.Auto();
            int budget = _settings.MaximumPendingWrites;
            while (budget-- > 0 && _pendingWrites.TryPeek(out ElementWriteRequest request))
            {
                if (MustDelayWrite(in request) || !_downstream.TryEnqueueDeposit(in request)) return;
                _pendingWrites.TryDequeue(out _);
            }
            _debugPendingWriteCount = _pendingWrites.Count;
            _debugRejectedPendingWriteCount = _pendingWrites.RejectedCount;
        }

        private static float ElapsedMilliseconds(double startedAt)
        {
            return (float)Math.Max(0d, (Time.realtimeSinceStartupAsDouble - startedAt) * 1000d);
        }

        private bool MustDelayWrite(in ElementWriteRequest request)
        {
            if (!_hasInterestChunk) return false;
            Vector3 radius = Vector3.one * Mathf.Max(0f, request.Radius);
            Vector3Int minCell = ElementWorldCoordinates.WorldToGlobalCell(
                request.WorldPosition - radius, _worldOrigin, _cellSize);
            Vector3Int maxCell = ElementWorldCoordinates.WorldToGlobalCell(
                request.WorldPosition + radius, _worldOrigin, _cellSize);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(minCell, _chunkSize,
                out ElementChunkKey minChunk, out _);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(maxCell, _chunkSize,
                out ElementChunkKey maxChunk, out _);

            if (_transferState.IsInFlight
                && (!_retainedRegion.Contains(minChunk) || !_retainedRegion.Contains(maxChunk)))
                return true;

            for (int z = minChunk.Z; z <= maxChunk.Z; z++)
            for (int y = minChunk.Y; y <= maxChunk.Y; y++)
            for (int x = minChunk.X; x <= maxChunk.X; x++)
            {
                ElementChunkKey candidate = new ElementChunkKey(x, y, z);
                if (!_store.ContainsChunk(candidate)) continue;
                if (_retainedRegion.Contains(candidate)
                    && _store.TryGetChunkKind(candidate, out FluidArchivedChunkKind kind)
                    && kind == FluidArchivedChunkKind.Dormant)
                {
                    RequestWakeChunk(candidate, FluidDormantWakeReason.Write);
                }
                return true;
            }
            return false;
        }

        private uint NextTransactionId()
        {
            unchecked { _nextTransactionId++; }
            if (_nextTransactionId == 0u) _nextTransactionId = 1u;
            return _nextTransactionId;
        }

        private void OnDestroy()
        {
            if (_transferState != null && _transferState.IsInFlight)
            {
                if (_hasBackendLease && _readbackScheduler.IsPending)
                    _readbackScheduler.WaitForCompletion();
                uint transactionId = _transferState.TransactionId;
                if (_hasBackendLease)
                {
                    if (_transferState.Kind == FluidChunkTransferKind.Restore)
                    {
                        if (_restoreStatusScheduler.IsPending)
                            _restoreStatusScheduler.WaitForCompletion();
                        _backend.RollbackRestore(transactionId, _restoreParticleCount);
                    }
                    else
                    {
                        _backend.CancelArchive(transactionId);
                    }
                    _backend.ReleaseTransferReadbackLease();
                    _hasBackendLease = false;
                }
                if (_transferState.Phase == FluidChunkTransferPhase.WaitingForReadback)
                    _transferState.RecordCompletion(transactionId, true);
                _transferState.Finish(transactionId);
            }
            DisposeNativeStorage();
            _isInitialized = false;
        }

        private void DisposeNativeStorage()
        {
            if (_archiveSamples.IsCreated) _archiveSamples.Dispose();
            if (_restoreParticles.IsCreated) _restoreParticles.Dispose();
            if (_restoreStatus.IsCreated) _restoreStatus.Dispose();
        }
    }
}

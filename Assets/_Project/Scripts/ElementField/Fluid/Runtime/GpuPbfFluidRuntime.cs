using System;
using Game.Core;
using Game.Materials;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.Rendering;

namespace Game.ElementField
{
    /// <summary>
    /// GPU PBF Runtime Adapter。它负责固定节拍、一次性 Buffer 生命周期、Spawn、Solver、Collision，
    /// 并在完整 Tick 后发布 current-position Spatial Hash，供只读 Rendering 消费同一份 Simulation Truth。
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class GpuPbfFluidRuntime : MonoBehaviour, IFluidGpuSource, IFluidDepositSink,
        IFluidSpawnSink, IFluidGameplayReadbackSource
    {
        private const int ThreadsPerGroup = 64;
        private const string SpatialHashBuildSampleName = "GpuFluid.SpatialHash.Build";
        private const string SpatialHashSortSampleName = "GpuFluid.SpatialHash.Sort";
        private const string SpatialHashRangesSampleName = "GpuFluid.SpatialHash.Ranges";
        private const string SpatialHashPublishSampleName = "GpuFluid.SpatialHash.PublishCurrent";

        private static readonly int ParticleCapacityId = Shader.PropertyToID("_ParticleCapacity");
        private static readonly int HashTableCapacityId = Shader.PropertyToID("_HashTableCapacity");
        private static readonly int SpawnRequestCountId = Shader.PropertyToID("_SpawnRequestCount");
        private static readonly int DeltaTimeId = Shader.PropertyToID("_DeltaTime");
        private static readonly int MaxSpeedId = Shader.PropertyToID("_MaxSpeed");
        private static readonly int GravityId = Shader.PropertyToID("_Gravity");
        private static readonly int PositionsId = Shader.PropertyToID("_Positions");
        private static readonly int PredictedPositionsId = Shader.PropertyToID("_PredictedPositions");
        private static readonly int VelocitiesId = Shader.PropertyToID("_Velocities");
        private static readonly int DensityLambdaId = Shader.PropertyToID("_DensityLambda");
        private static readonly int MetadataId = Shader.PropertyToID("_ParticleMetadata");
        private static readonly int DeltaPositionsId = Shader.PropertyToID("_DeltaPositions");
        private static readonly int DeltaVelocitiesId = Shader.PropertyToID("_DeltaVelocities");
        private static readonly int VorticitiesId = Shader.PropertyToID("_Vorticities");
        private static readonly int SpatialEntriesId = Shader.PropertyToID("_SpatialEntries");
        private static readonly int CellRangesId = Shader.PropertyToID("_CellRanges");
        private static readonly int SpatialCellsId = Shader.PropertyToID("_SpatialCells");
        private static readonly int LiquidMaterialParametersId =
            Shader.PropertyToID("_LiquidMaterialParameters");
        private static readonly int SmoothingRadiusId = Shader.PropertyToID("_SmoothingRadius");
        private static readonly int BitonicStageId = Shader.PropertyToID("_BitonicStage");
        private static readonly int BitonicPassId = Shader.PropertyToID("_BitonicPass");
        private static readonly int FreeIndicesId = Shader.PropertyToID("_FreeIndices");
        private static readonly int CountersId = Shader.PropertyToID("_Counters");
        private static readonly int SpawnRequestsId = Shader.PropertyToID("_SpawnRequests");
        private static readonly int ConsumeRequestsId = Shader.PropertyToID("_ConsumeRequests");
        private static readonly int ConvertRequestsId = Shader.PropertyToID("_ConvertRequests");
        private static readonly int ReactionCommandCountId = Shader.PropertyToID("_ReactionCommandCount");
        private static readonly int WorldOriginId = Shader.PropertyToID("_WorldOrigin");
        private static readonly int CellSizeId = Shader.PropertyToID("_CellSize");
        private static readonly int StableTickCountersId = Shader.PropertyToID("_StableTickCounters");
        private static readonly int WakeRequestsId = Shader.PropertyToID("_WakeRequests");
        private static readonly int ActivityCountersId = Shader.PropertyToID("_ActivityCounters");
        private static readonly int SolverDispatchArgsId = Shader.PropertyToID("_SolverDispatchArgs");
        private static readonly int HashDispatchArgsId = Shader.PropertyToID("_HashDispatchArgs");
        private static readonly int ActiveBoundsMinId = Shader.PropertyToID("_ActiveBoundsMin");
        private static readonly int ActiveBoundsMaxId = Shader.PropertyToID("_ActiveBoundsMax");
        private static readonly int SleepVelocityThresholdId = Shader.PropertyToID("_SleepVelocityThreshold");
        private static readonly int SleepDensityErrorThresholdId = Shader.PropertyToID("_SleepDensityErrorThreshold");
        private static readonly int SleepAfterStableTicksId = Shader.PropertyToID("_SleepAfterStableTicks");
        private static readonly int ParticleGroupCountId = Shader.PropertyToID("_ParticleGroupCount");
        private static readonly int HashTableGroupCountId = Shader.PropertyToID("_HashTableGroupCount");
        private static readonly int WakeAllInterestParticlesId = Shader.PropertyToID("_WakeAllInterestParticles");
        private static readonly int ParticleMassId = Shader.PropertyToID("_ParticleMass");
        private static readonly int RestDensityId = Shader.PropertyToID("_RestDensity");
        private static readonly int LambdaEpsilonId = Shader.PropertyToID("_LambdaEpsilon");
        private static readonly int ArtificialPressureId = Shader.PropertyToID("_ArtificialPressure");
        private static readonly int MaximumPositionCorrectionId = Shader.PropertyToID("_MaximumPositionCorrection");
        private static readonly int TensileStrengthId = Shader.PropertyToID("_TensileStrength");
        private static readonly int MaximumTensilePositionCorrectionId =
            Shader.PropertyToID("_MaximumTensilePositionCorrection");
        private static readonly int ViscosityId = Shader.PropertyToID("_Viscosity");
        private static readonly int VorticityId = Shader.PropertyToID("_Vorticity");
        private static readonly int CohesionStrengthId = Shader.PropertyToID("_CohesionStrength");
        private static readonly int CohesionRestDistanceId = Shader.PropertyToID("_CohesionRestDistance");
        private static readonly int MaximumCohesionDeltaSpeedId = Shader.PropertyToID("_MaximumCohesionDeltaSpeed");
        private static readonly int ColliderCountId = Shader.PropertyToID("_ColliderCount");
        private static readonly int ParticleRadiusId = Shader.PropertyToID("_ParticleRadius");
        private static readonly int CollisionFrictionId = Shader.PropertyToID("_CollisionFriction");
        private static readonly int CollisionRestitutionId = Shader.PropertyToID("_CollisionRestitution");
        private static readonly int ColliderProxiesId = Shader.PropertyToID("_ColliderProxies");
        private static readonly int CollisionContactsId = Shader.PropertyToID("_CollisionContacts");
        private static readonly int CollisionContactsReadOnlyId =
            Shader.PropertyToID("_CollisionContactsReadOnly");
        private static readonly int GameplaySamplesId = Shader.PropertyToID("_GameplaySamples");
        private static readonly ProfilerMarker SpatialHashBuildMarker =
            new ProfilerMarker(SpatialHashBuildSampleName);
        private static readonly ProfilerMarker SpatialHashSortMarker =
            new ProfilerMarker(SpatialHashSortSampleName);
        private static readonly ProfilerMarker SpatialHashRangesMarker =
            new ProfilerMarker(SpatialHashRangesSampleName);
        private static readonly ProfilerMarker SpatialHashPublishMarker =
            new ProfilerMarker(SpatialHashPublishSampleName);

        [Header("GPU PBF Phase A")]
        [SerializeField] private LiquidSimulationProfile _profile;
        [SerializeField] private ComputeShader _particleLifecycleShader;
        [SerializeField] private ComputeShader _spatialHashShader;
        [SerializeField] private ComputeShader _pbfSolverShader;
        [SerializeField] private ComputeShader _collisionShader;

        [Header("Fluid Collision")]
        [Tooltip("初始化时只扫描该 Root 下显式挂载 FluidColliderAuthoring 的对象。")]
        [SerializeField] private Transform _fluidColliderRoot;

        [Header("Simulation Bounds")]
        [Tooltip("Bounds 的中心只读取这个 Transform.position；Runtime 不进行 Find 或层级搜索。")]
        [SerializeField] private Transform _simulationBoundsCenter;
        [SerializeField] private Vector3 _simulationBoundsSize = new Vector3(12f, 8f, 12f);

        [Header("Development Counters (Async <= 2Hz)")]
        [SerializeField] private uint _debugAwakeParticleCount;
        [SerializeField] private uint _debugSleepingParticleCount;
        [SerializeField] private uint _debugInterestParticleCount;
        [SerializeField] private uint _debugRejectedParticleCount;
        [SerializeField] private uint _debugSpawnRequestOverflow;
        [SerializeField] private int _debugColliderOverflow;
        [SerializeField] private uint _debugReactionQueueOverflow;

        private FluidGpuResourceSet _resources;
        private FluidSimulationClock _clock;
        private LiquidSimulationSettings _settings;
        private FluidSpawnQueue _spawnQueue;
        private FluidColliderProxyCollector _colliderCollector;
        private FluidSpawnRequest[] _spawnRequestUploadBuffer;
        private FluidGpuSpawnRequest[] _spawnUploadBuffer;
        private FluidConsumeCommand[] _consumeUploadBuffer;
        private FluidConvertCommand[] _convertUploadBuffer;
        private FluidReactionCommandQueue _reactionQueue;
        private Vector3 _reactionWorldOrigin;
        private float _reactionCellSize;
        private Bounds _activeBounds;
        private FluidResidentBoundsTracker _residentBoundsTracker;
        private Bounds _lastActivityBounds;
        private bool _hasActivityBounds;
        private int _initializePoolKernel;
        private int _initializePoolAuxiliaryKernel;
        private int _initializeActivityKernel;
        private int _spawnParticlesKernel;
        private int _consumeParticlesKernel;
        private int _convertParticlesKernel;
        private int _applyGravityKernel;
        private int _predictPositionsKernel;
        private int _commitPositionsKernel;
        private int _packGameplaySamplesKernel;
        private int _clearActivityCountersKernel;
        private int _updateParticleActivityKernel;
        private int _writeSolverDispatchArgsKernel;
        private int _buildSpatialEntriesKernel;
        private int _bitonicSortKernel;
        private int _clearCellRangesKernel;
        private int _buildCellRangesKernel;
        private int _markNeighborWakeRequestsKernel;
        private int _computeDensityLambdaKernel;
        private int _computeDeltaPositionKernel;
        private int _applyDeltaPositionKernel;
        private int _applyTensileDeltaPositionKernel;
        private int _updateVelocitiesKernel;
        private int _computeCohesionDeltaVelocitiesKernel;
        private int _computeXsphDeltaVelocitiesKernel;
        private int _applyDeltaVelocitiesKernel;
        private int _computeVorticitiesKernel;
        private int _computeVorticityDeltaVelocitiesKernel;
        private int _clampVelocitiesKernel;
        private int _clearCollisionContactsKernel;
        private int _projectCollisionsKernel;
        private int _applyCollisionVelocitiesKernel;
        private int _particleGroupCount;
        private int _hashTableGroupCount;
        private CommandBuffer _spatialHashBuildCommands;
        private CommandBuffer _spatialHashSortCommands;
        private CommandBuffer _spatialHashRangesCommands;
        private bool _hasReportedInitializationFailure;
        private FluidTopologyVersionTracker _topologyVersionTracker;
        private FluidDepositQueueAdapter _depositAdapter;
        private LiquidMaterialAmountScaleSnapshot _gameplayAmountScales;
        private FluidReadbackLeaseTracker _gameplayReadbackLeaseTracker;
        private uint _gameplaySnapshotVersion;
        private AsyncGPUReadbackRequest _debugReadbackRequest;
        private Action<AsyncGPUReadbackRequest> _debugReadbackCallback;
        private float _debugReadbackElapsed;
        private bool _debugReadbackOutstanding;
        private bool _debugReadbackActivityNext = true;
        private bool _debugReleasePending;

        public bool IsFluidInitialized => _resources != null
            && !_gameplayReadbackLeaseTracker.IsReleasePending;

        /// <summary>
        /// ElementWorld 的初始化 Policy 在发布 PBF Route 前显式确认唯一 GPU Runtime 已就绪。
        /// 这不是热路径；失败会让 World 初始化整体失败，而不是偷偷把 Water 改写回 Cell。
        /// </summary>
        internal bool TryEnsureInitializedForWorld()
        {
            TryInitialize();
            return IsFluidInitialized;
        }

        public bool TryEnqueueDeposit(in ElementWriteRequest request)
        {
            return IsFluidInitialized
                && _depositAdapter != null
                && _depositAdapter.TryEnqueueDeposit(in request);
        }

        internal bool TryAttachReactionQueue(
            FluidReactionCommandQueue queue,
            Vector3 worldOrigin,
            float cellSize)
        {
            if (!IsFluidInitialized || queue == null || !IsPositiveFinite(cellSize))
                return false;

            // 只允许 ElementWorld 初始化边界连接。固定数组不能推迟到 Update/Tick 内分配。
            _reactionQueue = queue;
            _reactionWorldOrigin = worldOrigin;
            _reactionCellSize = cellSize;
            _consumeUploadBuffer = new FluidConsumeCommand[queue.Capacity];
            _convertUploadBuffer = new FluidConvertCommand[queue.Capacity];
            return true;
        }

        private void Awake()
        {
            _debugReadbackCallback = OnDebugReadbackCompleted;
            TryInitialize();
        }

        private void OnEnable()
        {
            _gameplayReadbackLeaseTracker.CancelPendingRelease();
            _debugReleasePending = false;
            if (_resources == null)
                TryInitialize();
        }

        private void Update()
        {
            if (_resources == null)
                return;

            // Bounds 是 Presentation culling 的输入，即使暂停也保持跟随中心 Transform，
            // 但只读取 position，不做 Find/分配或任何 Simulation 时间推进。
            RefreshActiveBounds();

            // 暂停期间不调用 Clock.Consume：不会积累要在恢复后补跑的 simulation debt。
            if (Time.timeScale == 0f)
                return;

            int tickCount = _clock.Consume(Time.deltaTime);
            for (int tick = 0; tick < tickCount; tick++)
                RunFixedTick();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ScheduleDevelopmentCounterReadback(Time.unscaledDeltaTime);
#endif
        }

        private void OnDisable()
        {
            RequestResourceRelease();
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_debugReadbackOutstanding)
            {
                _debugReadbackRequest.WaitForCompletion();
                OnDebugReadbackCompleted(_debugReadbackRequest);
            }
#endif
            RequestResourceRelease();
        }

        /// <summary>
        /// 这是唯一公开的 Spawn 入口。它仅进入固定容量 Queue，不注册 IElementWriteSink，
        /// 因而 Task 2 不会改变 Legacy ElementField 的 simulation truth。
        /// </summary>
        public bool TryEnqueueSpawn(in FluidSpawnRequest request)
        {
            if (!IsFluidInitialized
                || !FluidSpawnRequestValidator.IsValidForParticleCapacity(
                    in request,
                    _settings.ParticleCapacity))
            {
                return false;
            }

            // Enqueue 只表示未来 Tick 的工作；GPU topology 尚未改变，不能提前发布新版本。
            if (!_spawnQueue.TryEnqueue(in request))
                return false;

            // Queue 接受后即扩张 Presentation Coverage。即使粒子随后因 Pool 满而被 GPU 拒绝，
            // 这里只会留下少量空的 Far Voxel，不会复制或改变 Simulation Truth。
            _residentBoundsTracker.IncludeSpawn(
                request.WorldPosition,
                request.Radius,
                _settings.SmoothingRadius);
            return true;
        }

        public bool TryGetGpuSnapshot(out FluidGpuSnapshot snapshot)
        {
            if (!IsFluidInitialized)
            {
                snapshot = default;
                return false;
            }

            snapshot = new FluidGpuSnapshot(
                _resources.Positions,
                _resources.PredictedPositions,
                _resources.Velocities,
                _resources.Metadata,
                _resources.SpatialEntries,
                _resources.CellRanges,
                _resources.SpatialCells,
                _resources.LiquidMaterialParameters,
                _resources.ParticleCapacity,
                _resources.HashTableCapacity,
                _settings.SmoothingRadius,
                _settings.ParticleMass,
                _activeBounds,
                _residentBoundsTracker.HasBounds
                    ? _residentBoundsTracker.Bounds
                    : _activeBounds,
                FluidGpuLayout.LayoutVersion,
                _topologyVersionTracker.PublishedVersion);
            return true;
        }

        bool IFluidGameplayReadbackSource.TryGetGameplayParticleCapacity(out int particleCapacity)
        {
            if (!IsFluidInitialized)
            {
                particleCapacity = 0;
                return false;
            }

            particleCapacity = _settings.ParticleCapacity;
            return true;
        }

        bool IFluidGameplayReadbackSource.TryAcquireGameplayReadbackLease(
            Vector3 worldOrigin,
            float cellSize,
            out FluidGameplayReadbackLease lease)
        {
            if (!IsFluidInitialized
                || !IsPositiveFinite(cellSize)
                || !_gameplayReadbackLeaseTracker.TryAcquire())
            {
                lease = default;
                return false;
            }

            // Bridge 在 LateUpdate 调用，Simulation 的 Update Dispatch 已先提交；pack 与 readback 在同一
            // Graphics Queue 上保持顺序，因此一次 Request 得到匹配的 Position/active/material。
            _particleLifecycleShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
            _particleLifecycleShader.Dispatch(_packGameplaySamplesKernel, _particleGroupCount, 1, 1);
            unchecked
            {
                _gameplaySnapshotVersion++;
            }

            var metadata = new FluidGameplayReadbackMetadata(
                _activeBounds,
                worldOrigin,
                cellSize,
                _settings.ParticleCapacity,
                _gameplayAmountScales,
                FluidGpuLayout.LayoutVersion,
                _topologyVersionTracker.PublishedVersion,
                _gameplaySnapshotVersion);
            lease = new FluidGameplayReadbackLease(_resources.GameplaySamples, in metadata);
            return true;
        }

        void IFluidGameplayReadbackSource.ReleaseGameplayReadbackLease()
        {
            if (_gameplayReadbackLeaseTracker.ReleaseLease())
                TryFinishDeferredRelease();
        }

        private void ScheduleDevelopmentCounterReadback(float deltaTime)
        {
            if (_resources == null || _debugReadbackOutstanding)
                return;
            _debugReadbackElapsed += deltaTime;
            if (_debugReadbackElapsed < 0.5f)
                return;

            _debugReadbackElapsed = 0f;
            GraphicsBuffer source = _debugReadbackActivityNext
                ? _resources.ActivityCounters
                : _resources.Counters;
            try
            {
                _debugReadbackOutstanding = true;
                _debugReadbackRequest = AsyncGPUReadback.Request(source, _debugReadbackCallback);
            }
            catch (Exception)
            {
                _debugReadbackOutstanding = false;
            }
        }

        private void OnDebugReadbackCompleted(AsyncGPUReadbackRequest request)
        {
            if (!_debugReadbackOutstanding)
                return;
            _debugReadbackOutstanding = false;
            if (!request.hasError)
            {
                var values = request.GetData<uint>();
                if (_debugReadbackActivityNext && values.Length >= FluidGpuLayout.ActivityCounterCount)
                {
                    _debugAwakeParticleCount = values[FluidGpuLayout.AwakeActivityCounterIndex];
                    _debugSleepingParticleCount = values[FluidGpuLayout.SleepingActivityCounterIndex];
                    _debugInterestParticleCount = values[FluidGpuLayout.InterestActivityCounterIndex];
                }
                else if (!_debugReadbackActivityNext && values.Length >= FluidGpuLayout.CounterCount)
                {
                    _debugRejectedParticleCount = values[FluidGpuLayout.DroppedParticleCountCounterIndex];
                }
            }

            _debugReadbackActivityNext = !_debugReadbackActivityNext;
            _debugSpawnRequestOverflow = _spawnQueue != null
                ? _spawnQueue.RejectedRequestCount
                : _debugSpawnRequestOverflow;
            _debugColliderOverflow = _colliderCollector != null
                ? _colliderCollector.OverflowCount
                : _debugColliderOverflow;
            _debugReactionQueueOverflow = _reactionQueue != null
                ? _reactionQueue.RejectedBatchCount
                : _debugReactionQueueOverflow;
            if (_debugReleasePending && _gameplayReadbackLeaseTracker.RequestRelease())
                TryFinishDeferredRelease();
        }

        private void TryInitialize()
        {
            if (_resources != null)
                return;

            if (!ValidateConfiguration())
            {
                DisableAfterInitializationFailure();
                return;
            }

            try
            {
                _settings = _profile.CreateSettings();
                _gameplayAmountScales = _settings.LiquidMaterials.CreateAmountScaleSnapshot();
                if (!FluidSpatialHash.IsPowerOfTwo(_settings.ParticleCapacity)
                    || !FluidSpatialHash.IsPowerOfTwo(_settings.HashTableCapacity))
                {
                    throw new System.ArgumentException(
                        "Spatial Hash 需要 power-of-two 的 Particle/Hash Capacity。");
                }

                _clock = new FluidSimulationClock(_settings.FixedTickRate, _settings.MaxCatchUpTicks);
                _spawnQueue = new FluidSpawnQueue(_settings.MaxSpawnRequests);
                _depositAdapter = new FluidDepositQueueAdapter(
                    this,
                    _settings.LiquidMaterials,
                    _settings.ParticleCapacity,
                    _settings.ParticleRadius);
                _spawnRequestUploadBuffer = new FluidSpawnRequest[_settings.MaxSpawnRequests];
                _spawnUploadBuffer = new FluidGpuSpawnRequest[_settings.MaxSpawnRequests];
                _resources = new FluidGpuResourceSet(
                    _settings.ParticleCapacity,
                    _settings.HashTableCapacity,
                    _settings.MaxSpawnRequests,
                    _settings.MaxFluidColliders,
                    _settings.LiquidMaterials.CreateGpuRows());
                _colliderCollector = new FluidColliderProxyCollector(
                    _fluidColliderRoot,
                    _settings.MaxFluidColliders);

                CacheKernels();
                _particleGroupCount = DivideRoundUp(_settings.ParticleCapacity);
                _hashTableGroupCount = DivideRoundUp(_settings.HashTableCapacity);
                RefreshActiveBounds();

                // D3D11.0 每个 Kernel 最多绑定 8 个 RW UAV；初始化拆成 core/auxiliary 两次 Dispatch。
                // Buffer binding 是 per-kernel 的，不能为方便而把全量 ResourceSet 绑定给每个 Kernel。
                BindInitializePoolBuffers();
                _particleLifecycleShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
                _particleLifecycleShader.Dispatch(_initializePoolKernel, _particleGroupCount, 1, 1);

                BindInitializePoolAuxiliaryBuffers();
                _particleLifecycleShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
                _particleLifecycleShader.SetInt(HashTableCapacityId, _settings.HashTableCapacity);
                _particleLifecycleShader.Dispatch(
                    _initializePoolAuxiliaryKernel,
                    _hashTableGroupCount,
                    1,
                    1);

                BindInitializeActivityBuffers();
                _particleLifecycleShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
                _particleLifecycleShader.Dispatch(
                    _initializeActivityKernel,
                    _particleGroupCount,
                    1,
                    1);

                BindSpawnParticlesBuffers();
                BindApplyGravityBuffers();
                BindPredictPositionsBuffers();
                BindCommitPositionsBuffers();
                BindGameplayPackingBuffers();
                BindActivityBuffers();
                BindPbfSolverBuffers();
                BindCollisionBuffers();
                UploadColliderProxies();
                if (_colliderCollector.OverflowCount > 0)
                {
                    GameLog.Warn(
                        $"Fluid Collider Proxy 超出容量，按层级顺序忽略 {_colliderCollector.OverflowCount} 个。",
                        "ElementField");
                }
                RecordSpatialHashCommandBuffers();
                _lastActivityBounds = _activeBounds;
                _hasActivityBounds = true;
            }
            catch (System.Exception exception)
            {
                ReleaseResourcesImmediately();
                ReportInitializationFailure($"GPU PBF 初始化失败：{exception.Message}");
                enabled = false;
            }
        }

        private bool ValidateConfiguration()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                // Unsupported Graphics 的 Feature Toggle 与 Legacy fallback 只由 ElementWorldRuntime
                // 在 Play 初始化时决定并限频诊断；子系统静默拒绝，避免两个组件重复 Warn。
                return false;
            }

            if (_profile == null
                || _particleLifecycleShader == null
                || _spatialHashShader == null
                || _pbfSolverShader == null
                || _collisionShader == null)
            {
                ReportInitializationFailure("缺少 LiquidSimulationProfile 或 ParticleLifecycle/SpatialHash/PbfSolver/PbfCollision ComputeShader，GPU PBF 已禁用。");
                return false;
            }

            if (_fluidColliderRoot == null
                || _simulationBoundsCenter == null
                || !IsPositiveFinite(_simulationBoundsSize.x)
                || !IsPositiveFinite(_simulationBoundsSize.y)
                || !IsPositiveFinite(_simulationBoundsSize.z))
            {
                ReportInitializationFailure("Fluid Collider Root 与 Simulation Bounds Center 必须赋值，且 Bounds Size 三轴大于 0，GPU PBF 已禁用。");
                return false;
            }

            return true;
        }

        private void CacheKernels()
        {
            if (!_particleLifecycleShader.HasKernel("InitializePool")
                || !_particleLifecycleShader.HasKernel("InitializePoolAuxiliary")
                || !_particleLifecycleShader.HasKernel("InitializeActivity")
                || !_particleLifecycleShader.HasKernel("SpawnParticles")
                || !_particleLifecycleShader.HasKernel("ConsumeParticles")
                || !_particleLifecycleShader.HasKernel("ConvertParticles")
                || !_particleLifecycleShader.HasKernel("ClearActivityCounters")
                || !_particleLifecycleShader.HasKernel("UpdateParticleActivity")
                || !_particleLifecycleShader.HasKernel("WriteSolverDispatchArgs")
                || !_particleLifecycleShader.HasKernel("ApplyGravity")
                || !_particleLifecycleShader.HasKernel("PredictPositions")
                || !_particleLifecycleShader.HasKernel("CommitPositions")
                || !_particleLifecycleShader.HasKernel("PackGameplaySamples"))
            {
                throw new System.ArgumentException("PbfParticleLifecycle 缺少 Task 2 必需 Kernel。");
            }

            _initializePoolKernel = _particleLifecycleShader.FindKernel("InitializePool");
            _initializePoolAuxiliaryKernel = _particleLifecycleShader.FindKernel("InitializePoolAuxiliary");
            _initializeActivityKernel = _particleLifecycleShader.FindKernel("InitializeActivity");
            _spawnParticlesKernel = _particleLifecycleShader.FindKernel("SpawnParticles");
            _consumeParticlesKernel = _particleLifecycleShader.FindKernel("ConsumeParticles");
            _convertParticlesKernel = _particleLifecycleShader.FindKernel("ConvertParticles");
            _applyGravityKernel = _particleLifecycleShader.FindKernel("ApplyGravity");
            _predictPositionsKernel = _particleLifecycleShader.FindKernel("PredictPositions");
            _commitPositionsKernel = _particleLifecycleShader.FindKernel("CommitPositions");
            _packGameplaySamplesKernel = _particleLifecycleShader.FindKernel("PackGameplaySamples");
            _clearActivityCountersKernel = _particleLifecycleShader.FindKernel("ClearActivityCounters");
            _updateParticleActivityKernel = _particleLifecycleShader.FindKernel("UpdateParticleActivity");
            _writeSolverDispatchArgsKernel = _particleLifecycleShader.FindKernel("WriteSolverDispatchArgs");

            if (!_spatialHashShader.HasKernel("BuildSpatialEntries")
                || !_spatialHashShader.HasKernel("BitonicSort")
                || !_spatialHashShader.HasKernel("ClearCellRanges")
                || !_spatialHashShader.HasKernel("BuildCellRanges"))
            {
                throw new System.ArgumentException("PbfSpatialHash 缺少 Task 3 必需 Kernel。");
            }

            _buildSpatialEntriesKernel = _spatialHashShader.FindKernel("BuildSpatialEntries");
            _bitonicSortKernel = _spatialHashShader.FindKernel("BitonicSort");
            _clearCellRangesKernel = _spatialHashShader.FindKernel("ClearCellRanges");
            _buildCellRangesKernel = _spatialHashShader.FindKernel("BuildCellRanges");
            if (!_spatialHashShader.HasKernel("MarkNeighborWakeRequests"))
                throw new System.ArgumentException("PbfSpatialHash 缺少 Task 12 Wake Kernel。");
            _markNeighborWakeRequestsKernel = _spatialHashShader.FindKernel("MarkNeighborWakeRequests");

            if (!_pbfSolverShader.HasKernel("ComputeDensityLambda")
                || !_pbfSolverShader.HasKernel("ComputeDeltaPosition")
                || !_pbfSolverShader.HasKernel("ApplyDeltaPosition")
                || !_pbfSolverShader.HasKernel("ApplyTensileDeltaPosition")
                || !_pbfSolverShader.HasKernel("UpdateVelocities")
                || !_pbfSolverShader.HasKernel("ComputeCohesionDeltaVelocities")
                || !_pbfSolverShader.HasKernel("ComputeXsphDeltaVelocities")
                || !_pbfSolverShader.HasKernel("ApplyDeltaVelocities")
                || !_pbfSolverShader.HasKernel("ComputeVorticities")
                || !_pbfSolverShader.HasKernel("ComputeVorticityDeltaVelocities")
                || !_pbfSolverShader.HasKernel("ClampVelocities"))
            {
                throw new System.ArgumentException("PbfSolver 缺少 Task 4 必需 Kernel。");
            }

            _computeDensityLambdaKernel = _pbfSolverShader.FindKernel("ComputeDensityLambda");
            _computeDeltaPositionKernel = _pbfSolverShader.FindKernel("ComputeDeltaPosition");
            _applyDeltaPositionKernel = _pbfSolverShader.FindKernel("ApplyDeltaPosition");
            _applyTensileDeltaPositionKernel =
                _pbfSolverShader.FindKernel("ApplyTensileDeltaPosition");
            _updateVelocitiesKernel = _pbfSolverShader.FindKernel("UpdateVelocities");
            _computeCohesionDeltaVelocitiesKernel = _pbfSolverShader.FindKernel("ComputeCohesionDeltaVelocities");
            _computeXsphDeltaVelocitiesKernel = _pbfSolverShader.FindKernel("ComputeXsphDeltaVelocities");
            _applyDeltaVelocitiesKernel = _pbfSolverShader.FindKernel("ApplyDeltaVelocities");
            _computeVorticitiesKernel = _pbfSolverShader.FindKernel("ComputeVorticities");
            _computeVorticityDeltaVelocitiesKernel = _pbfSolverShader.FindKernel("ComputeVorticityDeltaVelocities");
            _clampVelocitiesKernel = _pbfSolverShader.FindKernel("ClampVelocities");

            if (!_collisionShader.HasKernel("ClearCollisionContacts")
                || !_collisionShader.HasKernel("ProjectCollisions")
                || !_collisionShader.HasKernel("ApplyCollisionVelocities"))
            {
                throw new System.ArgumentException("PbfCollision 缺少 Task 5 必需 Kernel。");
            }

            _clearCollisionContactsKernel = _collisionShader.FindKernel("ClearCollisionContacts");
            _projectCollisionsKernel = _collisionShader.FindKernel("ProjectCollisions");
            _applyCollisionVelocitiesKernel = _collisionShader.FindKernel("ApplyCollisionVelocities");
        }

        private void RunFixedTick()
        {
            bool colliderChanged = _colliderCollector.RefreshChangedProxies();
            if (colliderChanged)
                UploadColliderProxies();

            UploadQueuedSpawns();
            int consumeCommandCount = UploadQueuedConsumes();
            int convertCommandCount = UploadQueuedConversions();
            bool boundsChanged = !_hasActivityBounds || _activeBounds != _lastActivityBounds;
            bool wakeAll = colliderChanged
                || consumeCommandCount > 0
                || convertCommandCount > 0
                || boundsChanged;
            MarkNeighborWakeRequests();
            UpdateParticleActivity(wakeAll);
            _lastActivityBounds = _activeBounds;
            _hasActivityBounds = true;

            float substepDeltaTime = 1f / (_settings.FixedTickRate * _settings.Substeps);
            for (int substep = 0; substep < _settings.Substeps; substep++)
            {
                SetCollisionParameters();
                // Contact 是本 Substep 的临时约束结果；只清 Buffer，不创建集合或调用 Unity Physics。
                DispatchSolver(_collisionShader, _clearCollisionContactsKernel);
                _particleLifecycleShader.SetFloat(DeltaTimeId, substepDeltaTime);
                _particleLifecycleShader.SetFloat(MaxSpeedId, _settings.MaxSpeed);
                _particleLifecycleShader.SetVector(GravityId, _settings.Gravity);
                DispatchSolver(_particleLifecycleShader, _applyGravityKernel);
                DispatchSolver(_particleLifecycleShader, _predictPositionsKernel);

                // Hash 只在本 Substep 预测后构建一次。Correction 被限制为小于 h，
                // 因而复用候选 Range 可避免 Iteration 内反复 Sort；真实 cell 二次过滤仍在 Solver 内执行。
                BuildSpatialHashForSolver();

                SetPbfSolverParameters(substepDeltaTime);
                for (int iteration = 0; iteration < _settings.SolverIterations; iteration++)
                {
                    // Strict two-phase constraint：前两段只写各自槽位，第三段才改 PredictedPositions，避免邻居读写竞争。
                    DispatchSolver(_pbfSolverShader, _computeDensityLambdaKernel);
                    DispatchSolver(_pbfSolverShader, _computeDeltaPositionKernel);
                    DispatchSolver(_pbfSolverShader, _applyDeltaPositionKernel);
                    if (_settings.TensileStrength > 0f)
                        DispatchSolver(_pbfSolverShader, _applyTensileDeltaPositionKernel);
                    // 每次 Density 修正都可能把粒子推回墙内，因此每个 Iteration 的 Apply 后都重新投影。
                    DispatchSolver(_collisionShader, _projectCollisionsKernel);
                }

                DispatchSolver(_pbfSolverShader, _updateVelocitiesKernel);
                if (_settings.LiquidMaterials.HasAnyCohesion)
                {
                    DispatchSolver(_pbfSolverShader, _computeCohesionDeltaVelocitiesKernel);
                    DispatchSolver(_pbfSolverShader, _applyDeltaVelocitiesKernel);
                }
                if (_settings.LiquidMaterials.HasAnyViscosity)
                {
                    DispatchSolver(_pbfSolverShader, _computeXsphDeltaVelocitiesKernel);
                    DispatchSolver(_pbfSolverShader, _applyDeltaVelocitiesKernel);
                }

                if (_settings.Vorticity > 0f)
                {
                    DispatchSolver(_pbfSolverShader, _computeVorticitiesKernel);
                    DispatchSolver(_pbfSolverShader, _computeVorticityDeltaVelocitiesKernel);
                    DispatchSolver(_pbfSolverShader, _applyDeltaVelocitiesKernel);
                }

                // Collision Response 放在所有 Velocity Filter 之后：UpdateVelocities、XSPH 或 Vorticity
                // 都不能在它之后重新写入朝墙内的 Normal 分量；只执行一次也避免重复衰减 Tangent Friction。
                DispatchSolver(_collisionShader, _applyCollisionVelocitiesKernel);
                DispatchSolver(_pbfSolverShader, _clampVelocitiesKernel);
                DispatchSolver(_particleLifecycleShader, _commitPositionsKernel);
            }

            // Solver Hash 是每个 Substep Predict 时的 immutable candidate snapshot；PBF Apply 与
            // Collision 随后会移动粒子。完整 Tick 结束后额外发布一次 Current-position Hash，
            // 让 Rendering Density Field 不会拿旧 cell 过滤新位置。该 Build 每 Tick 仅一次，
            // 不放入每个 Solver Iteration，也不把它冒充“每 Substep 只 Sort 一次”的 Solver Hash。
            using (SpatialHashPublishMarker.Auto())
                BuildSpatialHash();
        }

        private void SetCollisionParameters()
        {
            _collisionShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
            _collisionShader.SetInt(ColliderCountId, _colliderCollector.ProxyCount);
            _collisionShader.SetFloat(ParticleRadiusId, _settings.ParticleRadius);
            _collisionShader.SetFloat(CollisionFrictionId, _settings.CollisionFriction);
            _collisionShader.SetFloat(CollisionRestitutionId, _settings.CollisionRestitution);
        }

        private void MarkNeighborWakeRequests()
        {
            _spatialHashShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
            _spatialHashShader.SetInt(HashTableCapacityId, _settings.HashTableCapacity);
            _spatialHashShader.SetFloat(SmoothingRadiusId, _settings.SmoothingRadius);
            _spatialHashShader.Dispatch(
                _markNeighborWakeRequestsKernel,
                _particleGroupCount,
                1,
                1);
        }

        private void UpdateParticleActivity(bool wakeAll)
        {
            _particleLifecycleShader.Dispatch(_clearActivityCountersKernel, 1, 1, 1);
            _particleLifecycleShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
            _particleLifecycleShader.SetInt(ParticleGroupCountId, _particleGroupCount);
            _particleLifecycleShader.SetInt(HashTableGroupCountId, _hashTableGroupCount);
            _particleLifecycleShader.SetInt(
                SleepAfterStableTicksId,
                _settings.SleepAfterStableTicks);
            _particleLifecycleShader.SetInt(WakeAllInterestParticlesId, wakeAll ? 1 : 0);
            _particleLifecycleShader.SetFloat(
                SleepVelocityThresholdId,
                _settings.SleepThreshold);
            _particleLifecycleShader.SetVector(ActiveBoundsMinId, _activeBounds.min);
            _particleLifecycleShader.SetVector(ActiveBoundsMaxId, _activeBounds.max);
            _particleLifecycleShader.Dispatch(
                _updateParticleActivityKernel,
                _particleGroupCount,
                1,
                1);
            _particleLifecycleShader.Dispatch(_writeSolverDispatchArgsKernel, 1, 1, 1);
        }

        private void DispatchSolver(ComputeShader shader, int kernel)
        {
            // Args.x 为 0 时 GPU 不启动工作组；非 0 时覆盖完整 capacity，保证稀疏高位 slot 不漏算。
            shader.DispatchIndirect(kernel, _resources.SolverDispatchArgs, 0u);
        }

        private void BuildSpatialHashForSolver()
        {
            _spatialHashShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
            _spatialHashShader.SetInt(HashTableCapacityId, _settings.HashTableCapacity);
            _spatialHashShader.SetFloat(SmoothingRadiusId, _settings.SmoothingRadius);
            using (SpatialHashBuildMarker.Auto())
            {
                _spatialHashShader.DispatchIndirect(
                    _buildSpatialEntriesKernel,
                    _resources.SolverDispatchArgs,
                    0u);
            }
            using (SpatialHashSortMarker.Auto())
            {
                for (int stage = 2; _settings.ParticleCapacity > 1; stage <<= 1)
                {
                    for (int pass = stage >> 1; pass > 0; pass >>= 1)
                    {
                        _spatialHashShader.SetInt(BitonicStageId, stage);
                        _spatialHashShader.SetInt(BitonicPassId, pass);
                        _spatialHashShader.DispatchIndirect(
                            _bitonicSortKernel,
                            _resources.SolverDispatchArgs,
                            0u);
                    }
                    if (stage == _settings.ParticleCapacity)
                        break;
                }
            }
            using (SpatialHashRangesMarker.Auto())
            {
                _spatialHashShader.DispatchIndirect(
                    _clearCellRangesKernel,
                    _resources.HashDispatchArgs,
                    0u);
                _spatialHashShader.DispatchIndirect(
                    _buildCellRangesKernel,
                    _resources.SolverDispatchArgs,
                    0u);
            }
        }

        private void SetPbfSolverParameters(float substepDeltaTime)
        {
            // 所有 ID 在静态初始化已缓存；每个 Substep 仅推送值 Snapshot，不做字符串查询、反射或临时集合分配。
            _pbfSolverShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
            _pbfSolverShader.SetInt(HashTableCapacityId, _settings.HashTableCapacity);
            _pbfSolverShader.SetFloat(SmoothingRadiusId, _settings.SmoothingRadius);
            _pbfSolverShader.SetFloat(LambdaEpsilonId, _settings.LambdaEpsilon);
            _pbfSolverShader.SetFloat(MaximumPositionCorrectionId, _settings.MaximumPositionCorrection);
            _pbfSolverShader.SetFloat(TensileStrengthId, _settings.TensileStrength);
            _pbfSolverShader.SetFloat(
                MaximumTensilePositionCorrectionId,
                _settings.MaximumTensilePositionCorrection);
            _pbfSolverShader.SetFloat(DeltaTimeId, substepDeltaTime);
            _pbfSolverShader.SetFloat(VorticityId, _settings.Vorticity);
            _pbfSolverShader.SetFloat(MaxSpeedId, _settings.MaxSpeed);
        }

        private void BuildSpatialHash()
        {
            using (SpatialHashBuildMarker.Auto())
                Graphics.ExecuteCommandBuffer(_spatialHashBuildCommands);

            using (SpatialHashSortMarker.Auto())
                Graphics.ExecuteCommandBuffer(_spatialHashSortCommands);

            using (SpatialHashRangesMarker.Auto())
                Graphics.ExecuteCommandBuffer(_spatialHashRangesCommands);
        }

        private int UploadQueuedSpawns()
        {
            if (_spawnQueue.Count == 0)
                return 0;

            int requestCount = _spawnQueue.CopyAndClear(_spawnRequestUploadBuffer);
            for (int i = 0; i < requestCount; i++)
                _spawnUploadBuffer[i] = new FluidGpuSpawnRequest(in _spawnRequestUploadBuffer[i]);

            // SetData 的 offset/count 是 element 而不是 byte；仅上传本固定 Tick 的有效范围。
            _resources.SpawnRequests.SetData(_spawnUploadBuffer, 0, 0, requestCount);
            _particleLifecycleShader.SetInt(SpawnRequestCountId, requestCount);
            // Phase F 只有 Water，因此整批请求共享一个由质量/静止密度推导的 RestSpacing。
            // Phase H 若引入多液体，必须迁移为 per-request 字段，不能继续复用这个全局 uniform。
            _particleLifecycleShader.Dispatch(_spawnParticlesKernel, DivideRoundUp(requestCount), 1, 1);
            // Dispatch 调用已把 Spawn 写入同一 Graphics Queue；之后的 Renderer LateUpdate
            // 看到新版本时，其 Anisotropy Dispatch 会按 Queue 顺序消费 Spawn 后的粒子拓扑。
            _topologyVersionTracker.PublishAfterSpawnDispatch(requestCount);
            return requestCount;
        }

        private int UploadQueuedConsumes()
        {
            if (_reactionQueue == null || _consumeUploadBuffer == null)
                return 0;

            int commandCount = _reactionQueue.CopyConsumeCommandsAndClear(_consumeUploadBuffer);
            if (commandCount <= 0)
                return 0;

            _resources.ConsumeRequests.SetData(_consumeUploadBuffer, 0, 0, commandCount);
            _particleLifecycleShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
            _particleLifecycleShader.SetInt(ReactionCommandCountId, commandCount);
            _particleLifecycleShader.SetVector(WorldOriginId, _reactionWorldOrigin);
            _particleLifecycleShader.SetFloat(CellSizeId, _reactionCellSize);
            _particleLifecycleShader.SetBuffer(_consumeParticlesKernel, PositionsId, _resources.Positions);
            _particleLifecycleShader.SetBuffer(_consumeParticlesKernel, PredictedPositionsId, _resources.PredictedPositions);
            _particleLifecycleShader.SetBuffer(_consumeParticlesKernel, VelocitiesId, _resources.Velocities);
            _particleLifecycleShader.SetBuffer(_consumeParticlesKernel, DensityLambdaId, _resources.DensityLambda);
            _particleLifecycleShader.SetBuffer(_consumeParticlesKernel, MetadataId, _resources.Metadata);
            _particleLifecycleShader.SetBuffer(_consumeParticlesKernel, DeltaPositionsId, _resources.DeltaPositions);
            _particleLifecycleShader.SetBuffer(_consumeParticlesKernel, FreeIndicesId, _resources.FreeIndices);
            _particleLifecycleShader.SetBuffer(_consumeParticlesKernel, CountersId, _resources.Counters);
            _particleLifecycleShader.SetBuffer(_consumeParticlesKernel, ConsumeRequestsId, _resources.ConsumeRequests);
            // numthreads(1,1,1)：一个 Group 对应一个低频反应 Command。
            _particleLifecycleShader.Dispatch(_consumeParticlesKernel, commandCount, 1, 1);
            _topologyVersionTracker.PublishAfterConsumeDispatch(commandCount);
            return commandCount;
        }

        private int UploadQueuedConversions()
        {
            if (_reactionQueue == null || _convertUploadBuffer == null)
                return 0;

            int commandCount = _reactionQueue.CopyConvertCommandsAndClear(_convertUploadBuffer);
            if (commandCount <= 0)
                return 0;

            _resources.ConvertRequests.SetData(_convertUploadBuffer, 0, 0, commandCount);
            _particleLifecycleShader.SetInt(ParticleCapacityId, _settings.ParticleCapacity);
            _particleLifecycleShader.SetInt(ReactionCommandCountId, commandCount);
            _particleLifecycleShader.SetVector(WorldOriginId, _reactionWorldOrigin);
            _particleLifecycleShader.SetFloat(CellSizeId, _reactionCellSize);
            _particleLifecycleShader.SetBuffer(
                _convertParticlesKernel, PositionsId, _resources.Positions);
            _particleLifecycleShader.SetBuffer(
                _convertParticlesKernel, MetadataId, _resources.Metadata);
            _particleLifecycleShader.SetBuffer(
                _convertParticlesKernel, StableTickCountersId, _resources.StableTickCounters);
            _particleLifecycleShader.SetBuffer(
                _convertParticlesKernel, ConvertRequestsId, _resources.ConvertRequests);
            // 一个 Group 对应一条 Command；Kernel 内用 CAS 抢占 Source Material，
            // 即使相邻命令 AABB 重叠，同一粒子也只会成功 Retag 一次。
            _particleLifecycleShader.Dispatch(_convertParticlesKernel, commandCount, 1, 1);
            _topologyVersionTracker.PublishAfterConvertDispatch(commandCount);
            return commandCount;
        }

        private void BindInitializePoolBuffers()
        {
            _particleLifecycleShader.SetBuffer(_initializePoolKernel, PositionsId, _resources.Positions);
            _particleLifecycleShader.SetBuffer(
                _initializePoolKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _particleLifecycleShader.SetBuffer(_initializePoolKernel, VelocitiesId, _resources.Velocities);
            _particleLifecycleShader.SetBuffer(_initializePoolKernel, DensityLambdaId, _resources.DensityLambda);
            _particleLifecycleShader.SetBuffer(_initializePoolKernel, MetadataId, _resources.Metadata);
            _particleLifecycleShader.SetBuffer(_initializePoolKernel, DeltaPositionsId, _resources.DeltaPositions);
            _particleLifecycleShader.SetBuffer(_initializePoolKernel, SpatialEntriesId, _resources.SpatialEntries);
            _particleLifecycleShader.SetBuffer(_initializePoolKernel, FreeIndicesId, _resources.FreeIndices);
        }

        private void BindInitializePoolAuxiliaryBuffers()
        {
            _particleLifecycleShader.SetBuffer(
                _initializePoolAuxiliaryKernel,
                CellRangesId,
                _resources.CellRanges);
            _particleLifecycleShader.SetBuffer(
                _initializePoolAuxiliaryKernel,
                CountersId,
                _resources.Counters);
        }

        private void BindInitializeActivityBuffers()
        {
            _particleLifecycleShader.SetBuffer(
                _initializeActivityKernel,
                StableTickCountersId,
                _resources.StableTickCounters);
            _particleLifecycleShader.SetBuffer(
                _initializeActivityKernel,
                WakeRequestsId,
                _resources.WakeRequests);
            _particleLifecycleShader.SetBuffer(
                _initializeActivityKernel,
                ActivityCountersId,
                _resources.ActivityCounters);
            _particleLifecycleShader.SetBuffer(
                _initializeActivityKernel,
                SolverDispatchArgsId,
                _resources.SolverDispatchArgs);
            _particleLifecycleShader.SetBuffer(
                _initializeActivityKernel,
                HashDispatchArgsId,
                _resources.HashDispatchArgs);
        }

        private void BindActivityBuffers()
        {
            _particleLifecycleShader.SetBuffer(
                _clearActivityCountersKernel,
                ActivityCountersId,
                _resources.ActivityCounters);

            _particleLifecycleShader.SetBuffer(
                _updateParticleActivityKernel,
                PositionsId,
                _resources.Positions);
            _particleLifecycleShader.SetBuffer(
                _updateParticleActivityKernel,
                VelocitiesId,
                _resources.Velocities);
            _particleLifecycleShader.SetBuffer(
                _updateParticleActivityKernel,
                DensityLambdaId,
                _resources.DensityLambda);
            _particleLifecycleShader.SetBuffer(
                _updateParticleActivityKernel,
                MetadataId,
                _resources.Metadata);
            _particleLifecycleShader.SetBuffer(
                _updateParticleActivityKernel,
                StableTickCountersId,
                _resources.StableTickCounters);
            _particleLifecycleShader.SetBuffer(
                _updateParticleActivityKernel,
                WakeRequestsId,
                _resources.WakeRequests);
            _particleLifecycleShader.SetBuffer(
                _updateParticleActivityKernel,
                ActivityCountersId,
                _resources.ActivityCounters);
            _particleLifecycleShader.SetBuffer(
                _updateParticleActivityKernel,
                LiquidMaterialParametersId,
                _resources.LiquidMaterialParameters);

            _particleLifecycleShader.SetBuffer(
                _writeSolverDispatchArgsKernel,
                ActivityCountersId,
                _resources.ActivityCounters);
            _particleLifecycleShader.SetBuffer(
                _writeSolverDispatchArgsKernel,
                SolverDispatchArgsId,
                _resources.SolverDispatchArgs);
            _particleLifecycleShader.SetBuffer(
                _writeSolverDispatchArgsKernel,
                HashDispatchArgsId,
                _resources.HashDispatchArgs);

            _spatialHashShader.SetBuffer(
                _buildSpatialEntriesKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _spatialHashShader.SetBuffer(
                _buildSpatialEntriesKernel,
                MetadataId,
                _resources.Metadata);
            _spatialHashShader.SetBuffer(
                _buildSpatialEntriesKernel,
                SpatialCellsId,
                _resources.SpatialCells);
            _spatialHashShader.SetBuffer(
                _buildSpatialEntriesKernel,
                SpatialEntriesId,
                _resources.SpatialEntries);
            _spatialHashShader.SetBuffer(
                _bitonicSortKernel,
                SpatialEntriesId,
                _resources.SpatialEntries);
            _spatialHashShader.SetBuffer(
                _clearCellRangesKernel,
                CellRangesId,
                _resources.CellRanges);
            _spatialHashShader.SetBuffer(
                _buildCellRangesKernel,
                SpatialEntriesId,
                _resources.SpatialEntries);
            _spatialHashShader.SetBuffer(
                _buildCellRangesKernel,
                CellRangesId,
                _resources.CellRanges);

            _spatialHashShader.SetBuffer(
                _markNeighborWakeRequestsKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _spatialHashShader.SetBuffer(
                _markNeighborWakeRequestsKernel,
                MetadataId,
                _resources.Metadata);
            _spatialHashShader.SetBuffer(
                _markNeighborWakeRequestsKernel,
                SpatialCellsId,
                _resources.SpatialCells);
            _spatialHashShader.SetBuffer(
                _markNeighborWakeRequestsKernel,
                SpatialEntriesId,
                _resources.SpatialEntries);
            _spatialHashShader.SetBuffer(
                _markNeighborWakeRequestsKernel,
                CellRangesId,
                _resources.CellRanges);
            _spatialHashShader.SetBuffer(
                _markNeighborWakeRequestsKernel,
                WakeRequestsId,
                _resources.WakeRequests);
        }

        private void BindSpawnParticlesBuffers()
        {
            _particleLifecycleShader.SetBuffer(_spawnParticlesKernel, PositionsId, _resources.Positions);
            _particleLifecycleShader.SetBuffer(
                _spawnParticlesKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _particleLifecycleShader.SetBuffer(_spawnParticlesKernel, VelocitiesId, _resources.Velocities);
            _particleLifecycleShader.SetBuffer(_spawnParticlesKernel, DensityLambdaId, _resources.DensityLambda);
            _particleLifecycleShader.SetBuffer(_spawnParticlesKernel, MetadataId, _resources.Metadata);
            _particleLifecycleShader.SetBuffer(_spawnParticlesKernel, DeltaPositionsId, _resources.DeltaPositions);
            _particleLifecycleShader.SetBuffer(_spawnParticlesKernel, FreeIndicesId, _resources.FreeIndices);
            _particleLifecycleShader.SetBuffer(_spawnParticlesKernel, CountersId, _resources.Counters);
            _particleLifecycleShader.SetBuffer(_spawnParticlesKernel, SpawnRequestsId, _resources.SpawnRequests);
        }

        private void BindApplyGravityBuffers()
        {
            _particleLifecycleShader.SetBuffer(_applyGravityKernel, VelocitiesId, _resources.Velocities);
            _particleLifecycleShader.SetBuffer(_applyGravityKernel, MetadataId, _resources.Metadata);
        }

        private void BindPredictPositionsBuffers()
        {
            _particleLifecycleShader.SetBuffer(_predictPositionsKernel, PositionsId, _resources.Positions);
            _particleLifecycleShader.SetBuffer(
                _predictPositionsKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _particleLifecycleShader.SetBuffer(_predictPositionsKernel, VelocitiesId, _resources.Velocities);
            _particleLifecycleShader.SetBuffer(_predictPositionsKernel, MetadataId, _resources.Metadata);
        }

        private void BindCommitPositionsBuffers()
        {
            _particleLifecycleShader.SetBuffer(_commitPositionsKernel, PositionsId, _resources.Positions);
            _particleLifecycleShader.SetBuffer(
                _commitPositionsKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _particleLifecycleShader.SetBuffer(_commitPositionsKernel, MetadataId, _resources.Metadata);
        }

        private void BindGameplayPackingBuffers()
        {
            _particleLifecycleShader.SetBuffer(
                _packGameplaySamplesKernel,
                PositionsId,
                _resources.Positions);
            _particleLifecycleShader.SetBuffer(
                _packGameplaySamplesKernel,
                MetadataId,
                _resources.Metadata);
            _particleLifecycleShader.SetBuffer(
                _packGameplaySamplesKernel,
                GameplaySamplesId,
                _resources.GameplaySamples);
        }

        private void BindPbfSolverBuffers()
        {
            // RW UAV 计数（按 Kernel）：Density=3、Delta=5、Apply=3、ApplyTensile=4、Velocity=3、
            // XSPH=5、ApplyVelocity=4、Vorticity=5、VorticityDelta=5、Clamp=3，均低于 D3D11 的 8。
            // 即使某 Kernel 只经 IsActive 读取 PredictedPositions.w，它在 HLSL 中仍声明为 RWStructuredBuffer，
            // 因此必须作为 UAV 绑定并计入上限，不能把“只读”误当作 StructuredBuffer SRV。
            BindPbfNeighborReadBuffers(_computeDensityLambdaKernel);
            _pbfSolverShader.SetBuffer(_computeDensityLambdaKernel, DensityLambdaId, _resources.DensityLambda);
            _pbfSolverShader.SetBuffer(_computeDensityLambdaKernel, CountersId, _resources.Counters);

            BindPbfNeighborReadBuffers(_computeDeltaPositionKernel);
            _pbfSolverShader.SetBuffer(_computeDeltaPositionKernel, DensityLambdaId, _resources.DensityLambda);
            _pbfSolverShader.SetBuffer(_computeDeltaPositionKernel, DeltaPositionsId, _resources.DeltaPositions);
            _pbfSolverShader.SetBuffer(
                _computeDeltaPositionKernel,
                DeltaVelocitiesId,
                _resources.DeltaVelocities);
            _pbfSolverShader.SetBuffer(_computeDeltaPositionKernel, CountersId, _resources.Counters);

            _pbfSolverShader.SetBuffer(_applyDeltaPositionKernel, MetadataId, _resources.Metadata);
            _pbfSolverShader.SetBuffer(_applyDeltaPositionKernel, DeltaPositionsId, _resources.DeltaPositions);
            _pbfSolverShader.SetBuffer(
                _applyDeltaPositionKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _pbfSolverShader.SetBuffer(_applyDeltaPositionKernel, CountersId, _resources.Counters);

            _pbfSolverShader.SetBuffer(
                _applyTensileDeltaPositionKernel,
                MetadataId,
                _resources.Metadata);
            _pbfSolverShader.SetBuffer(
                _applyTensileDeltaPositionKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _pbfSolverShader.SetBuffer(
                _applyTensileDeltaPositionKernel,
                DeltaVelocitiesId,
                _resources.DeltaVelocities);
            _pbfSolverShader.SetBuffer(
                _applyTensileDeltaPositionKernel,
                CountersId,
                _resources.Counters);

            _pbfSolverShader.SetBuffer(_updateVelocitiesKernel, PositionsId, _resources.Positions);
            _pbfSolverShader.SetBuffer(
                _updateVelocitiesKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _pbfSolverShader.SetBuffer(_updateVelocitiesKernel, MetadataId, _resources.Metadata);
            _pbfSolverShader.SetBuffer(_updateVelocitiesKernel, VelocitiesId, _resources.Velocities);
            _pbfSolverShader.SetBuffer(_updateVelocitiesKernel, CountersId, _resources.Counters);

            BindPbfNeighborReadBuffers(_computeCohesionDeltaVelocitiesKernel);
            _pbfSolverShader.SetBuffer(
                _computeCohesionDeltaVelocitiesKernel,
                DensityLambdaId,
                _resources.DensityLambda);
            _pbfSolverShader.SetBuffer(
                _computeCohesionDeltaVelocitiesKernel,
                DeltaVelocitiesId,
                _resources.DeltaVelocities);
            _pbfSolverShader.SetBuffer(
                _computeCohesionDeltaVelocitiesKernel,
                CountersId,
                _resources.Counters);

            BindPbfNeighborReadBuffers(_computeXsphDeltaVelocitiesKernel);
            _pbfSolverShader.SetBuffer(
                _computeXsphDeltaVelocitiesKernel,
                DensityLambdaId,
                _resources.DensityLambda);
            _pbfSolverShader.SetBuffer(_computeXsphDeltaVelocitiesKernel, VelocitiesId, _resources.Velocities);
            _pbfSolverShader.SetBuffer(
                _computeXsphDeltaVelocitiesKernel,
                DeltaVelocitiesId,
                _resources.DeltaVelocities);
            _pbfSolverShader.SetBuffer(_computeXsphDeltaVelocitiesKernel, CountersId, _resources.Counters);

            _pbfSolverShader.SetBuffer(_applyDeltaVelocitiesKernel, MetadataId, _resources.Metadata);
            _pbfSolverShader.SetBuffer(
                _applyDeltaVelocitiesKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _pbfSolverShader.SetBuffer(_applyDeltaVelocitiesKernel, VelocitiesId, _resources.Velocities);
            _pbfSolverShader.SetBuffer(
                _applyDeltaVelocitiesKernel,
                DeltaVelocitiesId,
                _resources.DeltaVelocities);
            _pbfSolverShader.SetBuffer(_applyDeltaVelocitiesKernel, CountersId, _resources.Counters);

            BindPbfNeighborReadBuffers(_computeVorticitiesKernel);
            _pbfSolverShader.SetBuffer(
                _computeVorticitiesKernel,
                DensityLambdaId,
                _resources.DensityLambda);
            _pbfSolverShader.SetBuffer(_computeVorticitiesKernel, VelocitiesId, _resources.Velocities);
            _pbfSolverShader.SetBuffer(_computeVorticitiesKernel, VorticitiesId, _resources.Vorticities);
            _pbfSolverShader.SetBuffer(_computeVorticitiesKernel, CountersId, _resources.Counters);

            BindPbfNeighborReadBuffers(_computeVorticityDeltaVelocitiesKernel);
            _pbfSolverShader.SetBuffer(
                _computeVorticityDeltaVelocitiesKernel,
                DensityLambdaId,
                _resources.DensityLambda);
            _pbfSolverShader.SetBuffer(
                _computeVorticityDeltaVelocitiesKernel,
                VorticitiesId,
                _resources.Vorticities);
            _pbfSolverShader.SetBuffer(
                _computeVorticityDeltaVelocitiesKernel,
                DeltaVelocitiesId,
                _resources.DeltaVelocities);
            _pbfSolverShader.SetBuffer(
                _computeVorticityDeltaVelocitiesKernel,
                CountersId,
                _resources.Counters);

            _pbfSolverShader.SetBuffer(
                _clampVelocitiesKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _pbfSolverShader.SetBuffer(_clampVelocitiesKernel, MetadataId, _resources.Metadata);
            _pbfSolverShader.SetBuffer(_clampVelocitiesKernel, VelocitiesId, _resources.Velocities);
            _pbfSolverShader.SetBuffer(_clampVelocitiesKernel, CountersId, _resources.Counters);
        }

        private void BindPbfNeighborReadBuffers(int kernel)
        {
            _pbfSolverShader.SetBuffer(kernel, PredictedPositionsId, _resources.PredictedPositions);
            _pbfSolverShader.SetBuffer(kernel, MetadataId, _resources.Metadata);
            _pbfSolverShader.SetBuffer(kernel, SpatialEntriesId, _resources.SpatialEntries);
            _pbfSolverShader.SetBuffer(kernel, CellRangesId, _resources.CellRanges);
            _pbfSolverShader.SetBuffer(kernel, SpatialCellsId, _resources.SpatialCells);
            _pbfSolverShader.SetBuffer(
                kernel,
                LiquidMaterialParametersId,
                _resources.LiquidMaterialParameters);
        }

        private void BindCollisionBuffers()
        {
            // UAV 数：Clear=1、Project=3、Velocity=2；其余是 SRV，均低于 D3D11 的 8-UAV 上限。
            _collisionShader.SetBuffer(
                _clearCollisionContactsKernel,
                CollisionContactsId,
                _resources.CollisionContacts);

            _collisionShader.SetBuffer(
                _projectCollisionsKernel,
                PositionsId,
                _resources.Positions);
            _collisionShader.SetBuffer(
                _projectCollisionsKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _collisionShader.SetBuffer(_projectCollisionsKernel, MetadataId, _resources.Metadata);
            _collisionShader.SetBuffer(
                _projectCollisionsKernel,
                ColliderProxiesId,
                _resources.ColliderProxies);
            _collisionShader.SetBuffer(
                _projectCollisionsKernel,
                CollisionContactsId,
                _resources.CollisionContacts);
            _collisionShader.SetBuffer(_projectCollisionsKernel, CountersId, _resources.Counters);

            _collisionShader.SetBuffer(
                _applyCollisionVelocitiesKernel,
                VelocitiesId,
                _resources.Velocities);
            _collisionShader.SetBuffer(
                _applyCollisionVelocitiesKernel,
                MetadataId,
                _resources.Metadata);
            _collisionShader.SetBuffer(
                _applyCollisionVelocitiesKernel,
                CollisionContactsReadOnlyId,
                _resources.CollisionContacts);
            _collisionShader.SetBuffer(
                _applyCollisionVelocitiesKernel,
                CountersId,
                _resources.Counters);
        }

        private void UploadColliderProxies()
        {
            int proxyCount = _colliderCollector.ProxyCount;
            if (proxyCount == 0)
                return;

            // SetData 的 Count 是元素数。数组与 Buffer 都在初始化时固定，重建不会产生 GC Alloc。
            _resources.ColliderProxies.SetData(
                _colliderCollector.Proxies,
                0,
                0,
                proxyCount);
        }

        private void RecordSpatialHashCommandBuffers()
        {
            // 容量、h、Buffer 与 Sort Pass 都在初始化时固定：录制一次、每个 Substep 只执行，避免热路径重新录制/分配。
            _spatialHashBuildCommands = new CommandBuffer { name = SpatialHashBuildSampleName };
            _spatialHashBuildCommands.BeginSample(SpatialHashBuildSampleName);
            _spatialHashBuildCommands.SetComputeIntParam(
                _spatialHashShader,
                ParticleCapacityId,
                _settings.ParticleCapacity);
            _spatialHashBuildCommands.SetComputeIntParam(
                _spatialHashShader,
                HashTableCapacityId,
                _settings.HashTableCapacity);
            _spatialHashBuildCommands.SetComputeFloatParam(
                _spatialHashShader,
                SmoothingRadiusId,
                _settings.SmoothingRadius);
            _spatialHashBuildCommands.SetComputeBufferParam(
                _spatialHashShader,
                _buildSpatialEntriesKernel,
                PredictedPositionsId,
                _resources.PredictedPositions);
            _spatialHashBuildCommands.SetComputeBufferParam(
                _spatialHashShader,
                _buildSpatialEntriesKernel,
                MetadataId,
                _resources.Metadata);
            _spatialHashBuildCommands.SetComputeBufferParam(
                _spatialHashShader,
                _buildSpatialEntriesKernel,
                SpatialCellsId,
                _resources.SpatialCells);
            _spatialHashBuildCommands.SetComputeBufferParam(
                _spatialHashShader,
                _buildSpatialEntriesKernel,
                SpatialEntriesId,
                _resources.SpatialEntries);
            _spatialHashBuildCommands.DispatchCompute(
                _spatialHashShader,
                _buildSpatialEntriesKernel,
                _particleGroupCount,
                1,
                1);
            _spatialHashBuildCommands.EndSample(SpatialHashBuildSampleName);

            _spatialHashSortCommands = new CommandBuffer { name = SpatialHashSortSampleName };
            _spatialHashSortCommands.BeginSample(SpatialHashSortSampleName);
            _spatialHashSortCommands.SetComputeIntParam(
                _spatialHashShader,
                ParticleCapacityId,
                _settings.ParticleCapacity);
            _spatialHashSortCommands.SetComputeBufferParam(
                _spatialHashShader,
                _bitonicSortKernel,
                SpatialEntriesId,
                _resources.SpatialEntries);
            for (int stage = 2; _settings.ParticleCapacity > 1; stage <<= 1)
            {
                for (int pass = stage >> 1; pass > 0; pass >>= 1)
                {
                    _spatialHashSortCommands.SetComputeIntParam(
                        _spatialHashShader,
                        BitonicStageId,
                        stage);
                    _spatialHashSortCommands.SetComputeIntParam(
                        _spatialHashShader,
                        BitonicPassId,
                        pass);
                    _spatialHashSortCommands.DispatchCompute(
                        _spatialHashShader,
                        _bitonicSortKernel,
                        _particleGroupCount,
                        1,
                        1);
                }

                if (stage == _settings.ParticleCapacity)
                    break;
            }
            _spatialHashSortCommands.EndSample(SpatialHashSortSampleName);

            _spatialHashRangesCommands = new CommandBuffer { name = SpatialHashRangesSampleName };
            _spatialHashRangesCommands.BeginSample(SpatialHashRangesSampleName);
            _spatialHashRangesCommands.SetComputeIntParam(
                _spatialHashShader,
                ParticleCapacityId,
                _settings.ParticleCapacity);
            _spatialHashRangesCommands.SetComputeIntParam(
                _spatialHashShader,
                HashTableCapacityId,
                _settings.HashTableCapacity);
            _spatialHashRangesCommands.SetComputeBufferParam(
                _spatialHashShader,
                _clearCellRangesKernel,
                CellRangesId,
                _resources.CellRanges);
            _spatialHashRangesCommands.DispatchCompute(
                _spatialHashShader,
                _clearCellRangesKernel,
                _hashTableGroupCount,
                1,
                1);
            _spatialHashRangesCommands.SetComputeBufferParam(
                _spatialHashShader,
                _buildCellRangesKernel,
                SpatialEntriesId,
                _resources.SpatialEntries);
            _spatialHashRangesCommands.SetComputeBufferParam(
                _spatialHashShader,
                _buildCellRangesKernel,
                CellRangesId,
                _resources.CellRanges);
            _spatialHashRangesCommands.DispatchCompute(
                _spatialHashShader,
                _buildCellRangesKernel,
                _particleGroupCount,
                1,
                1);
            _spatialHashRangesCommands.EndSample(SpatialHashRangesSampleName);
        }

        private void RefreshActiveBounds()
        {
            _activeBounds = new Bounds(_simulationBoundsCenter.position, _simulationBoundsSize);
            // 玩家移动只会扩大 Resident Coverage，不会把已经冻结在身后的液体裁掉。
            _residentBoundsTracker.Include(_activeBounds);
        }

        private void DisableAfterInitializationFailure()
        {
            enabled = false;
        }

        private void ReportInitializationFailure(string message)
        {
            if (_hasReportedInitializationFailure)
                return;

            _hasReportedInitializationFailure = true;
            GameLog.Warn(message, "ElementField");
        }

        private void RequestResourceRelease()
        {
            _debugReleasePending = true;
            if (_gameplayReadbackLeaseTracker.RequestRelease())
                TryFinishDeferredRelease();
        }

        private void TryFinishDeferredRelease()
        {
            if (_debugReadbackOutstanding)
                return;
            _debugReleasePending = false;
            ReleaseResourcesImmediately();
        }

        private void ReleaseResourcesImmediately()
        {
            _depositAdapter = null;
            _spatialHashBuildCommands = ReleaseCommandBuffer(_spatialHashBuildCommands);
            _spatialHashSortCommands = ReleaseCommandBuffer(_spatialHashSortCommands);
            _spatialHashRangesCommands = ReleaseCommandBuffer(_spatialHashRangesCommands);

            if (_resources != null)
            {
                _resources.Dispose();
                _resources = null;
            }

            _clock = null;
            _spawnQueue = null;
            _spawnRequestUploadBuffer = null;
            _spawnUploadBuffer = null;
            _consumeUploadBuffer = null;
            _convertUploadBuffer = null;
            _reactionQueue = null;
            _colliderCollector = null;
            _debugReadbackOutstanding = false;
            _gameplayReadbackLeaseTracker.Reset();
        }

        private static CommandBuffer ReleaseCommandBuffer(CommandBuffer commandBuffer)
        {
            if (commandBuffer == null)
                return null;

            commandBuffer.Release();
            return null;
        }

        private static int DivideRoundUp(int value)
        {
            return (value + ThreadsPerGroup - 1) / ThreadsPerGroup;
        }

        private static bool IsPositiveFinite(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

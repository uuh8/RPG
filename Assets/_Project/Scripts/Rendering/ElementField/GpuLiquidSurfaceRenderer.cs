using System;
using Game.Core;
using Game.ElementField;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>把可抛异常的 Profile snapshot 边界转成显式失败，供 Renderer 与 focused test 共用。</summary>
    internal static class LiquidRenderSettingsFactory
    {
        internal static bool TryCreate(
            LiquidRenderProfile profile,
            out LiquidRenderSettings settings,
            out string error)
        {
            try
            {
                settings = profile.CreateSettings();
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                settings = default;
                error = exception.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Presentation-only GPU 编排器：读取 IFluidGpuSource 的 committed particle snapshot，依次提交
    /// Density Field、Marching Cubes、Indirect Args 和 Procedural Draw。它绝不写回 Simulation Truth。
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class GpuLiquidSurfaceRenderer : MonoBehaviour
    {
        public const int SurfaceVertexStride = 32;
        public const int SurfaceTriangleStride = 96;

        private const uint RequiredFluidLayoutVersion = FluidGpuLayout.LayoutVersion;
        private const int DensityThreadGroupSize = 4;
        private const int MarchingCubesThreadGroupSize = 4;

        private static readonly ProfilerMarker DensityMarker =
            new ProfilerMarker("GpuFluid.Surface.Density");
        private static readonly ProfilerMarker AnisotropyMarker =
            new ProfilerMarker("GpuFluid.Surface.Anisotropy");
        private static readonly ProfilerMarker MarchingCubesMarker =
            new ProfilerMarker("GpuFluid.Surface.MarchingCubes");
        private static readonly ProfilerMarker DrawMarker =
            new ProfilerMarker("GpuFluid.Surface.IndirectDraw");

        private static readonly int PredictedPositionsId = Shader.PropertyToID("_PredictedPositions");
        private static readonly int ParticleMetadataId = Shader.PropertyToID("_ParticleMetadata");
        private static readonly int SpatialEntriesId = Shader.PropertyToID("_SpatialEntries");
        private static readonly int CellRangesId = Shader.PropertyToID("_CellRanges");
        private static readonly int SpatialCellsId = Shader.PropertyToID("_SpatialCells");
        private static readonly int DensityTextureId = Shader.PropertyToID("_DensityTexture");
        private static readonly int SurfaceTrianglesId = Shader.PropertyToID("_FluidSurfaceTriangles");
        private static readonly int TriangleCounterId = Shader.PropertyToID("_TriangleCounter");
        private static readonly int OverflowCounterId = Shader.PropertyToID("_OverflowCounter");
        private static readonly int IndirectArgumentsId = Shader.PropertyToID("_IndirectArguments");
        private static readonly int ParticleCapacityId = Shader.PropertyToID("_ParticleCapacity");
        private static readonly int HashTableCapacityId = Shader.PropertyToID("_HashTableCapacity");
        private static readonly int SmoothingRadiusId = Shader.PropertyToID("_SmoothingRadius");
        private static readonly int ParticleMassId = Shader.PropertyToID("_ParticleMass");
        private static readonly int GridResolutionXId = Shader.PropertyToID("_GridResolutionX");
        private static readonly int GridResolutionYId = Shader.PropertyToID("_GridResolutionY");
        private static readonly int GridResolutionZId = Shader.PropertyToID("_GridResolutionZ");
        private static readonly int WorldOriginId = Shader.PropertyToID("_WorldOrigin");
        private static readonly int VoxelSizeId = Shader.PropertyToID("_VoxelSize");
        private static readonly int IsoLevelId = Shader.PropertyToID("_IsoLevel");
        private static readonly int MaximumTriangleCountId = Shader.PropertyToID("_MaximumTriangleCount");
        private static readonly int AnisotropyTransformsId = Shader.PropertyToID("_AnisotropyTransforms");
        private static readonly int AnisotropyCountersId = Shader.PropertyToID("_AnisotropyCounters");
        private static readonly int NeighborThresholdId = Shader.PropertyToID("_NeighborThreshold");
        private static readonly int MinimumAnisotropyScaleId = Shader.PropertyToID("_MinimumAnisotropyScale");
        private static readonly int MaximumAnisotropyScaleId = Shader.PropertyToID("_MaximumAnisotropyScale");
        private static readonly int MaximumAnisotropyRatioId = Shader.PropertyToID("_MaximumAnisotropyRatio");
        private static readonly int AnisotropyCellRadiusId = Shader.PropertyToID("_AnisotropyCellRadius");

        [Tooltip("应引用实现 IFluidGpuSource 的 Runtime；Rendering 只能读取其 GPU Snapshot。")]
        [SerializeField] private MonoBehaviour _fluidSourceComponent;
        [Tooltip("提供初始化时已确定的 effective Water mode；用于在分配 GPU Surface 资源前互斥 Legacy/PBF Renderer。")]
        [SerializeField] private ElementWorldRuntime _worldRuntime;
        [SerializeField] private LiquidRenderProfile _profile;
        [SerializeField] private ComputeShader _densityCompute;
        [SerializeField] private ComputeShader _anisotropyCompute;
        [SerializeField] private ComputeShader _marchingCubesCompute;
        [SerializeField] private Material _material;

        [Header("Development Counters (Async <= 2Hz)")]
        [SerializeField] private uint _debugTriangleOverflow;

        private IFluidGpuSource _fluidSource;
        private LiquidRenderSettings _settings;
        private FluidSurfaceGpuResources _resources;
        private MaterialPropertyBlock _materialPropertyBlock;
        private RenderParams _renderParams;
        private int _densityKernel;
        private int _clearAnisotropyCountersKernel;
        private int _gatherAnisotropyMeanKernel;
        private int _buildAnisotropyTransformKernel;
        private int _clearSurfaceCountersKernel;
        private int _extractSurfaceKernel;
        private int _buildIndirectArgsKernel;
        private bool _hasReportedFailure;
        private bool _hasStarted;
        private FluidAnisotropyUpdateSchedule _anisotropySchedule;
        private AsyncGPUReadbackRequest _debugCounterRequest;
        private Action<AsyncGPUReadbackRequest> _debugCounterCallback;
        private float _debugCounterElapsed;
        private bool _debugCounterOutstanding;
        private bool _resourceReleasePending;

        private void Awake()
        {
            _debugCounterCallback = OnDebugCounterReadbackCompleted;
            if (_worldRuntime == null)
                _worldRuntime = GetComponentInParent<ElementWorldRuntime>();
            if (_worldRuntime == null)
            {
                DisableWithFailure("GpuLiquidSurfaceRenderer 缺少 ElementWorldRuntime mode source。");
                return;
            }
            if (!WaterRendererModePolicy.ShouldRenderGpuPbf(
                    _worldRuntime.EffectiveWaterSimulationMode))
            {
                // effective Legacy（包括 unsupported Graphics fallback）由 ElementWorldRuntime 统一诊断；
                // 这里在任何 GPU 资源分配前静默退出，避免同一 fallback 原因重复 Warn。
                enabled = false;
                return;
            }

            _fluidSource = _fluidSourceComponent as IFluidGpuSource;
            if (_fluidSource == null
                || _profile == null
                || _densityCompute == null
                || _marchingCubesCompute == null
                || _material == null)
            {
                DisableWithFailure("GpuLiquidSurfaceRenderer 缺少 Source、Profile、Compute 或 Material。");
                return;
            }

            if (!IsSupportedGraphicsDevice())
            {
                DisableWithFailure("GPU Liquid Surface 首版只支持具备 Compute/Indirect/R32 的 D3D11 或 D3D12。");
                return;
            }

            // CreateSettings 只能调用一次且必须在异常边界内；非法序列化 Profile 应禁用 Renderer，不能让 Awake 抛出。
            if (!LiquidRenderSettingsFactory.TryCreate(_profile, out _settings, out string settingsError))
            {
                DisableWithFailure($"GpuLiquidSurfaceRenderer Profile 无效：{settingsError}");
                return;
            }
            if (_settings.UseAnisotropy && _anisotropyCompute == null)
            {
                DisableWithFailure("GpuLiquidSurfaceRenderer 启用 Anisotropy 时缺少对应 ComputeShader。");
                return;
            }
            if (!_densityCompute.HasKernel("GatherIsotropicDensity")
                || (_settings.UseAnisotropy && !_densityCompute.HasKernel("GatherAnisotropicDensity"))
                || (_settings.UseAnisotropy
                    && (!_anisotropyCompute.HasKernel("ClearAnisotropyCounters")
                        || !_anisotropyCompute.HasKernel("GatherAnisotropyMean")
                        || !_anisotropyCompute.HasKernel("BuildAnisotropyTransform")))
                || !_marchingCubesCompute.HasKernel("ClearSurfaceCounters")
                || !_marchingCubesCompute.HasKernel("ExtractSurface")
                || !_marchingCubesCompute.HasKernel("BuildIndirectArgs"))
            {
                DisableWithFailure("GPU Liquid Surface 的 Compute Kernel Contract 不完整。");
                return;
            }

            _densityKernel = _densityCompute.FindKernel(
                _settings.UseAnisotropy ? "GatherAnisotropicDensity" : "GatherIsotropicDensity");
            _anisotropySchedule = new FluidAnisotropyUpdateSchedule(
                _settings.AnisotropyUpdateIntervalFrames);
            if (_settings.UseAnisotropy)
            {
                _clearAnisotropyCountersKernel = _anisotropyCompute.FindKernel("ClearAnisotropyCounters");
                _gatherAnisotropyMeanKernel = _anisotropyCompute.FindKernel("GatherAnisotropyMean");
                _buildAnisotropyTransformKernel = _anisotropyCompute.FindKernel("BuildAnisotropyTransform");
            }
            _clearSurfaceCountersKernel = _marchingCubesCompute.FindKernel("ClearSurfaceCounters");
            _extractSurfaceKernel = _marchingCubesCompute.FindKernel("ExtractSurface");
            _buildIndirectArgsKernel = _marchingCubesCompute.FindKernel("BuildIndirectArgs");

            // 这两个 Native/managed wrapper 都是初始化时的一次性长期对象；LateUpdate 只修改其内容。
            _materialPropertyBlock = new MaterialPropertyBlock();
            _renderParams = new RenderParams(_material)
            {
                matProps = _materialPropertyBlock,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(Vector3.zero, Vector3.one)
            };
        }

        private void OnEnable()
        {
            _resourceReleasePending = false;
            // 再次启用属于 Unity 生命周期初始化边界，不是每帧热路径；资源只在这里重建一次。
            if (_hasStarted && _resources == null && _fluidSource != null)
                InitializeSurfaceResources();
        }

        private void Start()
        {
            _hasStarted = true;
            InitializeSurfaceResources();
        }

        private void LateUpdate()
        {
            if (_fluidSource == null
                || !_fluidSource.TryGetGpuSnapshot(out FluidGpuSnapshot snapshot)
                || snapshot.LayoutVersion != RequiredFluidLayoutVersion)
            {
                return;
            }

            if (_resources == null)
                return;

            FluidSurfaceGridSettings grid;
            try
            {
                grid = FluidSurfaceGridPlanner.Plan(snapshot.ActiveBounds, in _settings);
                if (_resources.ParticleCapacity != snapshot.ParticleCapacity
                    || !_resources.TryUpdateGrid(in grid))
                {
                    DisableWithFailure("Fluid Surface Grid/Capacity 在运行中改变；为避免热路径重分配已禁用 Renderer。");
                    return;
                }
            }
            catch (Exception exception)
            {
                // 只有非法 Bounds/Contract 才进入失败分支；正常 LateUpdate 不构造 message/array/closure。
                DisableWithFailure(exception.Message);
                return;
            }

            if (_settings.UseAnisotropy
                && _anisotropySchedule.ShouldUpdateAndAdvance(snapshot.TopologyVersion))
            {
                DispatchAnisotropy(in snapshot);
            }
            DispatchDensity(in snapshot, in grid);
            DispatchMarchingCubes(in grid);
            DrawSurface(in grid);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ScheduleDebugCounterReadback(Time.unscaledDeltaTime);
#endif
        }

        private void InitializeSurfaceResources()
        {
            if (_resources != null || _fluidSource == null)
                return;
            if (!_fluidSource.TryGetGpuSnapshot(out FluidGpuSnapshot snapshot)
                || snapshot.LayoutVersion != RequiredFluidLayoutVersion)
            {
                DisableWithFailure("GpuLiquidSurfaceRenderer 初始化时未取得兼容的 Fluid GPU Snapshot。");
                return;
            }

            try
            {
                FluidSurfaceGridSettings grid =
                    FluidSurfaceGridPlanner.Plan(snapshot.ActiveBounds, in _settings);
                _resources = new FluidSurfaceGpuResources(in grid, snapshot.ParticleCapacity);
                _anisotropySchedule.Reset();
            }
            catch (Exception exception)
            {
                DisableWithFailure(exception.Message);
            }
        }

        private void OnDisable()
        {
            _resourceReleasePending = true;
            DisposeResources();
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_debugCounterOutstanding)
            {
                _debugCounterRequest.WaitForCompletion();
                OnDebugCounterReadbackCompleted(_debugCounterRequest);
            }
#endif
            _resourceReleasePending = true;
            DisposeResources();
        }

        private void ScheduleDebugCounterReadback(float deltaTime)
        {
            if (_resources == null || _debugCounterOutstanding)
                return;
            _debugCounterElapsed += deltaTime;
            if (_debugCounterElapsed < 0.5f)
                return;
            _debugCounterElapsed = 0f;
            try
            {
                _debugCounterOutstanding = true;
                _debugCounterRequest = AsyncGPUReadback.Request(
                    _resources.OverflowCounter,
                    _debugCounterCallback);
            }
            catch (Exception)
            {
                _debugCounterOutstanding = false;
            }
        }

        private void OnDebugCounterReadbackCompleted(AsyncGPUReadbackRequest request)
        {
            if (!_debugCounterOutstanding)
                return;
            _debugCounterOutstanding = false;
            if (!request.hasError)
            {
                var values = request.GetData<uint>();
                if (values.Length > 0)
                    _debugTriangleOverflow = values[0];
            }
            if (_resourceReleasePending)
                DisposeResources();
        }

        private void DispatchDensity(
            in FluidGpuSnapshot snapshot,
            in FluidSurfaceGridSettings grid)
        {
            using (DensityMarker.Auto())
            {
                _densityCompute.SetInt(ParticleCapacityId, snapshot.ParticleCapacity);
                _densityCompute.SetInt(HashTableCapacityId, snapshot.HashTableCapacity);
                _densityCompute.SetFloat(SmoothingRadiusId, snapshot.SmoothingRadius);
                _densityCompute.SetFloat(ParticleMassId, snapshot.ParticleMass);
                _densityCompute.SetInt(AnisotropyCellRadiusId, _settings.AnisotropyCellRadius);
                SetGridParameters(_densityCompute, in grid);
                _densityCompute.SetBuffer(_densityKernel, PredictedPositionsId, snapshot.PredictedPositions);
                _densityCompute.SetBuffer(_densityKernel, ParticleMetadataId, snapshot.Metadata);
                _densityCompute.SetBuffer(_densityKernel, SpatialEntriesId, snapshot.SpatialEntries);
                _densityCompute.SetBuffer(_densityKernel, CellRangesId, snapshot.CellRanges);
                _densityCompute.SetBuffer(_densityKernel, SpatialCellsId, snapshot.SpatialCells);
                if (_settings.UseAnisotropy)
                {
                    _densityCompute.SetBuffer(
                        _densityKernel,
                        AnisotropyTransformsId,
                        _resources.AnisotropyBuffer);
                }
                _densityCompute.SetTexture(_densityKernel, DensityTextureId, _resources.DensityTexture);
                _densityCompute.Dispatch(
                    _densityKernel,
                    DivideRoundUp(grid.Resolution.x, DensityThreadGroupSize),
                    DivideRoundUp(grid.Resolution.y, DensityThreadGroupSize),
                    DivideRoundUp(grid.Resolution.z, DensityThreadGroupSize));
            }
        }

        private void DispatchAnisotropy(in FluidGpuSnapshot snapshot)
        {
            using (AnisotropyMarker.Auto())
            {
                SetAnisotropyParameters(in snapshot);
                _anisotropyCompute.SetBuffer(
                    _clearAnisotropyCountersKernel,
                    AnisotropyCountersId,
                    _resources.AnisotropyCounters);
                _anisotropyCompute.Dispatch(_clearAnisotropyCountersKernel, 1, 1, 1);

                BindAnisotropyNeighborhood(_gatherAnisotropyMeanKernel, in snapshot);
                _anisotropyCompute.SetBuffer(
                    _gatherAnisotropyMeanKernel,
                    AnisotropyTransformsId,
                    _resources.AnisotropyBuffer);
                _anisotropyCompute.Dispatch(
                    _gatherAnisotropyMeanKernel,
                    DivideRoundUp(snapshot.ParticleCapacity, 64),
                    1,
                    1);

                BindAnisotropyNeighborhood(_buildAnisotropyTransformKernel, in snapshot);
                _anisotropyCompute.SetBuffer(
                    _buildAnisotropyTransformKernel,
                    AnisotropyTransformsId,
                    _resources.AnisotropyBuffer);
                _anisotropyCompute.SetBuffer(
                    _buildAnisotropyTransformKernel,
                    AnisotropyCountersId,
                    _resources.AnisotropyCounters);
                _anisotropyCompute.Dispatch(
                    _buildAnisotropyTransformKernel,
                    DivideRoundUp(snapshot.ParticleCapacity, 64),
                    1,
                    1);
            }
        }

        private void SetAnisotropyParameters(in FluidGpuSnapshot snapshot)
        {
            _anisotropyCompute.SetInt(ParticleCapacityId, snapshot.ParticleCapacity);
            _anisotropyCompute.SetInt(HashTableCapacityId, snapshot.HashTableCapacity);
            _anisotropyCompute.SetInt(NeighborThresholdId, _settings.AnisotropyNeighborThreshold);
            _anisotropyCompute.SetFloat(SmoothingRadiusId, snapshot.SmoothingRadius);
            _anisotropyCompute.SetFloat(MinimumAnisotropyScaleId, _settings.MinimumAnisotropyScale);
            _anisotropyCompute.SetFloat(MaximumAnisotropyScaleId, _settings.MaximumAnisotropyScale);
            _anisotropyCompute.SetFloat(MaximumAnisotropyRatioId, _settings.MaximumAnisotropyRatio);
        }

        private void BindAnisotropyNeighborhood(int kernel, in FluidGpuSnapshot snapshot)
        {
            _anisotropyCompute.SetBuffer(kernel, PredictedPositionsId, snapshot.PredictedPositions);
            _anisotropyCompute.SetBuffer(kernel, ParticleMetadataId, snapshot.Metadata);
            _anisotropyCompute.SetBuffer(kernel, SpatialEntriesId, snapshot.SpatialEntries);
            _anisotropyCompute.SetBuffer(kernel, CellRangesId, snapshot.CellRanges);
            _anisotropyCompute.SetBuffer(kernel, SpatialCellsId, snapshot.SpatialCells);
        }

        private void DispatchMarchingCubes(in FluidSurfaceGridSettings grid)
        {
            using (MarchingCubesMarker.Auto())
            {
                SetGridParameters(_marchingCubesCompute, in grid);
                _marchingCubesCompute.SetFloat(IsoLevelId, _settings.IsoLevel);
                _marchingCubesCompute.SetInt(MaximumTriangleCountId, grid.MaximumTriangleCount);

                BindSurfaceWriteBuffers(_clearSurfaceCountersKernel);
                _marchingCubesCompute.Dispatch(_clearSurfaceCountersKernel, 1, 1, 1);

                _marchingCubesCompute.SetTexture(
                    _extractSurfaceKernel,
                    DensityTextureId,
                    _resources.DensityTexture);
                BindSurfaceWriteBuffers(_extractSurfaceKernel);
                _marchingCubesCompute.Dispatch(
                    _extractSurfaceKernel,
                    DivideRoundUp(grid.Resolution.x - 1, MarchingCubesThreadGroupSize),
                    DivideRoundUp(grid.Resolution.y - 1, MarchingCubesThreadGroupSize),
                    DivideRoundUp(grid.Resolution.z - 1, MarchingCubesThreadGroupSize));

                BindSurfaceWriteBuffers(_buildIndirectArgsKernel);
                _marchingCubesCompute.Dispatch(_buildIndirectArgsKernel, 1, 1, 1);
            }
        }

        private void DrawSurface(in FluidSurfaceGridSettings grid)
        {
            using (DrawMarker.Auto())
            {
                _materialPropertyBlock.SetBuffer(SurfaceTrianglesId, _resources.TriangleBuffer);
                // Bounds 是 padded Density Grid 的真实世界范围；URP 用它进行整批 Culling/透明排序。
                _renderParams.worldBounds = grid.WorldBounds;
                Graphics.RenderPrimitivesIndirect(
                    _renderParams,
                    MeshTopology.Triangles,
                    _resources.IndirectArguments,
                    1,
                    0);
            }
        }

        private void BindSurfaceWriteBuffers(int kernel)
        {
            _marchingCubesCompute.SetBuffer(
                kernel, SurfaceTrianglesId, _resources.TriangleBuffer);
            _marchingCubesCompute.SetBuffer(
                kernel, TriangleCounterId, _resources.TriangleCounter);
            _marchingCubesCompute.SetBuffer(
                kernel, OverflowCounterId, _resources.OverflowCounter);
            _marchingCubesCompute.SetBuffer(
                kernel, IndirectArgumentsId, _resources.IndirectArguments);
        }

        private static void SetGridParameters(
            ComputeShader compute,
            in FluidSurfaceGridSettings grid)
        {
            compute.SetInt(GridResolutionXId, grid.Resolution.x);
            compute.SetInt(GridResolutionYId, grid.Resolution.y);
            compute.SetInt(GridResolutionZId, grid.Resolution.z);
            compute.SetVector(WorldOriginId, grid.WorldOrigin);
            compute.SetVector(VoxelSizeId, grid.VoxelSize);
        }

        private static bool IsSupportedGraphicsDevice()
        {
            bool supportedD3d = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
            return supportedD3d
                && SystemInfo.supportsComputeShaders
                && SystemInfo.supportsInstancing
                && SystemInfo.supportsIndirectArgumentsBuffer
                && SystemInfo.IsFormatSupported(
                    GraphicsFormat.R32_SFloat,
                    GraphicsFormatUsage.Sample);
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private void DisableWithFailure(string message)
        {
            if (!_hasReportedFailure)
            {
                _hasReportedFailure = true;
                GameLog.Warn(message, "Rendering");
            }

            enabled = false;
        }

        private void DisposeResources()
        {
            if (_debugCounterOutstanding)
            {
                _resourceReleasePending = true;
                return;
            }
            if (_resources == null)
                return;
            _resources.Dispose();
            _resources = null;
            _resourceReleasePending = false;
        }
    }
}

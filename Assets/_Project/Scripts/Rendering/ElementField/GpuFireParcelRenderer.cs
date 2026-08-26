using System;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// Fire Cell 的只读 GPU Projection。CPU 只低频抽取 Cell Seed；已出生 Parcel 的运动、生命周期、
    /// Surface/Gas 相位都留在固定 GPU Pool。Renderer 从不回写 Fire Amount 或创建 Gameplay Gas。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GpuFireParcelRenderer : MonoBehaviour
    {
        private const uint SeedJitter = 0x9E3779B9u;
        private static readonly ProfilerMarker SeedMarker = new ProfilerMarker("GpuFire.SeedUpload");
        private static readonly ProfilerMarker SimulateMarker = new ProfilerMarker("GpuFire.ParcelLifecycle");
        private static readonly int StatesId = Shader.PropertyToID("_FireParcelStates");
        private static readonly int SpawnsId = Shader.PropertyToID("_FireParcelSpawns");

        [SerializeField] private ElementWorldRuntime _worldRuntime;
        [SerializeField] private Transform _interestCenter;
        [SerializeField] private GpuFireParcelRenderProfile _profile;
        [SerializeField] private ComputeShader _lifecycleCompute;
        [SerializeField] private ComputeShader _surfaceDensityCompute;
        [SerializeField] private ComputeShader _surfaceMarchingCubesCompute;
        [SerializeField] private Material _surfaceMaterial;
        [SerializeField] private Material _gasMaterial;

        private IElementWorldReadOnly _world;
        private ElementChunkKey[] _visibleKeys;
        private FireParcelSpawnGpu[] _seedCpu;
        private GraphicsBuffer _spawnBuffer;
        private GraphicsBuffer _stateBuffer;
        private GraphicsBuffer _drawArgs;
        private FireParcelSurfaceResources _surfaceResources;
        private MaterialPropertyBlock _surfaceProperties;
        private MaterialPropertyBlock _surfaceSplatProperties;
        private MaterialPropertyBlock _gasProperties;
        private RenderParams _surfaceRenderParams;
        private RenderParams _surfaceSplatRenderParams;
        private RenderParams _gasRenderParams;
        private FireParcelPhaseSettings _phase;
        private int _clearKernel;
        private int _spawnKernel;
        private int _simulateKernel;
        private int _densityKernel;
        private int _surfaceClearKernel;
        private int _surfaceExtractKernel;
        private int _surfaceArgsKernel;
        private uint _spawnSequence;
        private uint _visualSequence;
        private float _spawnAccumulator;
        private bool _initialized;

        private void Start()
        {
            if (!TryInitialize())
                enabled = false;
        }

        private bool TryInitialize()
        {
            if (_worldRuntime == null || _profile == null || _lifecycleCompute == null
                || _surfaceDensityCompute == null || _surfaceMarchingCubesCompute == null
                || _surfaceMaterial == null || _gasMaterial == null)
            {
                GameLog.Error("GpuFireParcelRenderer 缺少 Runtime/Profile/Compute/Material。", "Rendering");
                return false;
            }
            _world = _worldRuntime;
            if (!_world.IsInitialized)
                return false;
            if (!_lifecycleCompute.HasKernel("ClearParcels")
                || !_lifecycleCompute.HasKernel("SpawnParcels")
                || !_lifecycleCompute.HasKernel("SimulateParcels")
                || !_surfaceDensityCompute.HasKernel("GatherFireDensity")
                || !_surfaceMarchingCubesCompute.HasKernel("ClearFireSurface")
                || !_surfaceMarchingCubesCompute.HasKernel("ExtractFireSurface")
                || !_surfaceMarchingCubesCompute.HasKernel("BuildFireDrawArgs"))
            {
                // Shader Import 失败时 HasKernel=false。初始化前停止，避免每帧 Dispatch kernel 0 刷屏。
                GameLog.Error("GPU Fire Compute Kernel Contract 不完整，请先检查 Shader Import Error。", "Rendering");
                return false;
            }
            _interestCenter ??= transform;
            _phase = _profile.CreatePhaseSettings();
            _visibleKeys = new ElementChunkKey[_world.MaximumResidentChunkCount];
            _seedCpu = new FireParcelSpawnGpu[_profile.MaximumSeedsPerTick];
            _spawnBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                _profile.MaximumSeedsPerTick, FireParcelGpuLayout.SpawnStride);
            _stateBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                _profile.Capacity, FireParcelGpuLayout.StateStride);
            _drawArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1,
                GraphicsBuffer.IndirectDrawArgs.size);
            _drawArgs.SetData(new uint[] { 6u, (uint)_profile.Capacity, 0u, 0u });

            FireParcelSurfaceGridSettings initialGrid = FireParcelSurfaceGridPlanner.Plan(
                _interestCenter.position, _profile.SurfaceBoundsRadius, _profile.SurfaceVoxelSize);
            _surfaceResources = new FireParcelSurfaceResources(
                in initialGrid, _profile.MaximumSurfaceTriangleCount);

            _clearKernel = _lifecycleCompute.FindKernel("ClearParcels");
            _spawnKernel = _lifecycleCompute.FindKernel("SpawnParcels");
            _simulateKernel = _lifecycleCompute.FindKernel("SimulateParcels");
            _densityKernel = _surfaceDensityCompute.FindKernel("GatherFireDensity");
            _surfaceClearKernel = _surfaceMarchingCubesCompute.FindKernel("ClearFireSurface");
            _surfaceExtractKernel = _surfaceMarchingCubesCompute.FindKernel("ExtractFireSurface");
            _surfaceArgsKernel = _surfaceMarchingCubesCompute.FindKernel("BuildFireDrawArgs");
            BindCommon(_clearKernel);
            _lifecycleCompute.Dispatch(_clearKernel, DivideRoundUp(_profile.Capacity, 64), 1, 1);

            _surfaceProperties = new MaterialPropertyBlock();
            _surfaceSplatProperties = new MaterialPropertyBlock();
            _gasProperties = new MaterialPropertyBlock();
            _surfaceProperties.SetBuffer("_FireSurfaceTriangles", _surfaceResources.Triangles);
            _surfaceProperties.SetColor("_CoreColor", _profile.CoreColor);
            _surfaceProperties.SetColor("_EdgeColor", _profile.EdgeColor);
            _surfaceSplatProperties.SetBuffer(StatesId, _stateBuffer);
            _surfaceSplatProperties.SetFloat("_PhaseMode", 0f);
            _surfaceSplatProperties.SetColor("_CoreColor", _profile.CoreColor);
            _surfaceSplatProperties.SetColor("_EdgeColor", _profile.EdgeColor);
            _surfaceSplatProperties.SetFloat(
                "_SurfaceRadiusMultiplier",
                _profile.SurfaceSplatRadiusMultiplier);
            _surfaceSplatProperties.SetFloat(
                "_SurfaceAlphaScale",
                _profile.SurfaceSplatAlphaScale);
            _surfaceSplatProperties.SetFloat("_EdgeIrregularity", _profile.GasEdgeIrregularity * 0.5f);
            _surfaceSplatProperties.SetFloat("_VerticalStretch", 1f);
            _gasProperties.SetBuffer(StatesId, _stateBuffer);
            _gasProperties.SetFloat("_PhaseMode", 1f);
            _gasProperties.SetColor("_CoreColor", _profile.CoreColor);
            _gasProperties.SetColor("_EdgeColor", _profile.EdgeColor);
            _gasProperties.SetFloat("_GasRadiusMultiplier", _profile.GasRadiusMultiplier);
            _gasProperties.SetFloat("_GasAlphaScale", _profile.GasAlphaScale);
            _gasProperties.SetFloat("_EdgeIrregularity", _profile.GasEdgeIrregularity);
            _gasProperties.SetFloat("_VerticalStretch", _profile.GasVerticalStretch);
            _surfaceRenderParams = CreateRenderParams(_surfaceMaterial, _surfaceProperties);
            // 复用现有 Gas Material/Shader，但用独立 PropertyBlock 选择 PhaseMode=0；
            // Presentation Draw 彼此独立，不会缩短或改写 Gas 生命周期。
            _surfaceSplatRenderParams = CreateRenderParams(_gasMaterial, _surfaceSplatProperties);
            _gasRenderParams = CreateRenderParams(_gasMaterial, _gasProperties);
            _initialized = true;
            return true;
        }

        private static RenderParams CreateRenderParams(Material material, MaterialPropertyBlock properties)
        {
            return new RenderParams(material)
            {
                matProps = properties,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = new Bounds(Vector3.zero, Vector3.one)
            };
        }

        private void Update()
        {
            if (!_initialized || Time.timeScale <= 0f)
                return;
            float dt = Mathf.Min(Time.deltaTime, _profile.MaximumVisualDeltaTime);
            _spawnAccumulator += dt;
            if (_spawnAccumulator >= _profile.SpawnInterval)
            {
                _spawnAccumulator = 0f;
                UploadAndSpawnSeeds();
            }
            Simulate(dt);
        }

        private void LateUpdate()
        {
            if (!_initialized)
                return;
            Bounds bounds = new Bounds(_interestCenter.position,
                Vector3.one * (_profile.DrawBoundsRadius * 2f));
            FireParcelSurfaceGridSettings grid = FireParcelSurfaceGridPlanner.Plan(
                _interestCenter.position, _profile.SurfaceBoundsRadius, _profile.SurfaceVoxelSize);
            ReconstructSurface(in grid);
            _surfaceRenderParams.worldBounds = bounds;
            _surfaceSplatRenderParams.worldBounds = bounds;
            _gasRenderParams.worldBounds = bounds;
            Vector3 surfaceHalfSize = Vector3.one * _profile.SurfaceBoundsRadius;
            _surfaceProperties.SetVector("_SurfaceLodCenter", _interestCenter.position);
            _surfaceProperties.SetVector("_SurfaceLodHalfSize", surfaceHalfSize);
            _surfaceProperties.SetFloat("_SurfaceLodBlendWidth", _profile.SurfaceSplatBlendWidth);
            _surfaceSplatProperties.SetVector("_SurfaceLodCenter", _interestCenter.position);
            _surfaceSplatProperties.SetVector("_SurfaceLodHalfSize", surfaceHalfSize);
            _surfaceSplatProperties.SetFloat(
                "_SurfaceLodBlendWidth",
                _profile.SurfaceSplatBlendWidth);
            // Surface Splat 先画，局部连续网格后画；Shader 的互补 LOD Weight 防止 Blend 区双亮。
            Graphics.RenderPrimitivesIndirect(_surfaceSplatRenderParams, MeshTopology.Triangles,
                _drawArgs, 1, 0);
            Graphics.RenderPrimitivesIndirect(_surfaceRenderParams, MeshTopology.Triangles,
                _surfaceResources.DrawArgs, 1, 0);
            Graphics.RenderPrimitivesIndirect(_gasRenderParams, MeshTopology.Triangles, _drawArgs, 1, 0);
        }

        private void ReconstructSurface(in FireParcelSurfaceGridSettings grid)
        {
            SetSurfaceGrid(_surfaceDensityCompute, in grid);
            _surfaceDensityCompute.SetInt("_Capacity", _profile.Capacity);
            _surfaceDensityCompute.SetInt("_SurfaceParcelBudget", _profile.SurfaceParcelBudget);
            _surfaceDensityCompute.SetFloat("_SurfaceKernelScale", _profile.SurfaceKernelScale);
            _surfaceDensityCompute.SetFloat(
                "_LiquidLikeRadiusMultiplier",
                _profile.LiquidLikeRadiusMultiplier);
            uint recentWindowStart = _spawnSequence >= (uint)_profile.SurfaceParcelBudget
                ? _spawnSequence - (uint)_profile.SurfaceParcelBudget
                : 0u;
            _surfaceDensityCompute.SetInt("_SurfaceStartSlot",
                FireParcelPoolMath.ResolveRingSlot(recentWindowStart, _profile.Capacity));
            _surfaceDensityCompute.SetBuffer(_densityKernel, StatesId, _stateBuffer);
            _surfaceDensityCompute.SetTexture(_densityKernel, "_FireDensity", _surfaceResources.Density);
            _surfaceDensityCompute.Dispatch(_densityKernel,
                DivideRoundUp(grid.Resolution.x,4), DivideRoundUp(grid.Resolution.y,4), DivideRoundUp(grid.Resolution.z,4));

            SetSurfaceGrid(_surfaceMarchingCubesCompute, in grid);
            _surfaceMarchingCubesCompute.SetFloat("_IsoLevel", _profile.SurfaceIsoLevel);
            _surfaceMarchingCubesCompute.SetInt("_MaximumTriangleCount", _profile.MaximumSurfaceTriangleCount);
            BindSurface(_surfaceClearKernel);
            _surfaceMarchingCubesCompute.Dispatch(_surfaceClearKernel,1,1,1);
            _surfaceMarchingCubesCompute.SetTexture(_surfaceExtractKernel,"_FireDensity",_surfaceResources.Density);
            BindSurface(_surfaceExtractKernel);
            _surfaceMarchingCubesCompute.Dispatch(_surfaceExtractKernel,
                DivideRoundUp(grid.Resolution.x-1,4),DivideRoundUp(grid.Resolution.y-1,4),DivideRoundUp(grid.Resolution.z-1,4));
            BindSurface(_surfaceArgsKernel);
            _surfaceMarchingCubesCompute.Dispatch(_surfaceArgsKernel,1,1,1);
        }

        private void BindSurface(int kernel)
        {
            _surfaceMarchingCubesCompute.SetBuffer(kernel,"_FireSurfaceTriangles",_surfaceResources.Triangles);
            _surfaceMarchingCubesCompute.SetBuffer(kernel,"_TriangleCounter",_surfaceResources.Counter);
            _surfaceMarchingCubesCompute.SetBuffer(kernel,"_OverflowCounter",_surfaceResources.Overflow);
            _surfaceMarchingCubesCompute.SetBuffer(kernel,"_IndirectArguments",_surfaceResources.DrawArgs);
        }

        private static void SetSurfaceGrid(ComputeShader compute, in FireParcelSurfaceGridSettings grid)
        {
            compute.SetInt("_GridResolutionX",grid.Resolution.x); compute.SetInt("_GridResolutionY",grid.Resolution.y); compute.SetInt("_GridResolutionZ",grid.Resolution.z);
            compute.SetVector("_WorldOrigin",grid.Origin); compute.SetVector("_VoxelSize",grid.VoxelSize);
        }

        private void UploadAndSpawnSeeds()
        {
            using (SeedMarker.Auto())
            {
                int keyCount = _world.CopyVisibleChunkKeys(_visibleKeys);
                int seedCount = 0;
                int chunkSize = _world.ChunkSize;
                float cellSize = _world.CellSize;
                Vector3 origin = _world.Origin;
                for (int keyIndex = 0; keyIndex < keyCount && seedCount < _seedCpu.Length; keyIndex++)
                {
                    ElementChunkKey key = _visibleKeys[keyIndex];
                    for (int z = 0; z < chunkSize && seedCount < _seedCpu.Length; z++)
                    for (int y = 0; y < chunkSize && seedCount < _seedCpu.Length; y++)
                    for (int x = 0; x < chunkSize && seedCount < _seedCpu.Length; x++)
                    {
                        Vector3Int cellPosition = ElementWorldCoordinates.ComposeGlobalCell(
                            key, new Vector3Int(x, y, z), chunkSize);
                        if (!_world.TryGetCell(cellPosition, out ElementCell cell)
                            || cell.IsEmpty || cell.MaterialKind != MaterialId.Fire
                            || !FireCellEmissionSampler.ShouldEmit(
                                cellPosition,
                                _visualSequence,
                                SeedJitter,
                                cell.Amount,
                                _profile.SpawnProbabilityAtFullAmount))
                            continue;
                        Vector3 center = origin + new Vector3(
                            (cellPosition.x + 0.5f) * cellSize,
                            (cellPosition.y + 0.5f) * cellSize,
                            (cellPosition.z + 0.5f) * cellSize);
                        _seedCpu[seedCount++] = new FireParcelSpawnGpu
                        {
                            Position = center + FireCellEmissionSampler.CalculateJitter(
                                cellPosition, _visualSequence, SeedJitter, cellSize, cellSize * 0.3f),
                            // Phase 在初始化时形成不可变快照；Spawn Lifetime 必须使用同一快照，
                            // 避免 Play Mode 中编辑 Profile 造成 CPU Seed 与 GPU Uniform 时间轴分叉。
                            Lifetime = _phase.TotalLifetimeSeconds,
                            InitialVelocity = Vector3.up * 0.25f,
                            Heat = FireCellEmissionSampler.NormalizeAmount(cell.Amount)
                        };
                    }
                }
                _visualSequence++;
                if (seedCount == 0) return;
                _spawnBuffer.SetData(_seedCpu, 0, 0, seedCount);
                BindCommon(_spawnKernel);
                _lifecycleCompute.SetBuffer(_spawnKernel, SpawnsId, _spawnBuffer);
                _lifecycleCompute.SetInt("_SpawnCount", seedCount);
                _lifecycleCompute.SetInt("_SpawnStartSlot",
                    FireParcelPoolMath.ResolveRingSlot(_spawnSequence, _profile.Capacity));
                _lifecycleCompute.Dispatch(_spawnKernel, DivideRoundUp(seedCount, 64), 1, 1);
                _spawnSequence += (uint)seedCount;
            }
        }

        private void Simulate(float deltaTime)
        {
            using (SimulateMarker.Auto())
            {
                BindCommon(_simulateKernel);
                _lifecycleCompute.SetFloat("_DeltaTime", deltaTime);
                _lifecycleCompute.SetFloat("_Buoyancy", _profile.Buoyancy);
                _lifecycleCompute.SetFloat("_Drag", _profile.Drag);
                _lifecycleCompute.SetFloat("_SourceTether", _profile.SourceTether);
                _lifecycleCompute.SetFloat("_NoiseStrength", _profile.NoiseStrength);
                _lifecycleCompute.SetFloat("_NoiseScale", _profile.NoiseScale);
                _lifecycleCompute.Dispatch(_simulateKernel, DivideRoundUp(_profile.Capacity, 64), 1, 1);
            }
        }

        private void BindCommon(int kernel)
        {
            _lifecycleCompute.SetInt("_Capacity", _profile.Capacity);
            _lifecycleCompute.SetFloat("_LiquidLikeHoldSeconds", _phase.LiquidLikeHoldSeconds);
            _lifecycleCompute.SetFloat(
                "_SurfaceToGasBlendSeconds",
                _phase.SurfaceToGasBlendSeconds);
            _lifecycleCompute.SetFloat("_GasLifetimeSeconds", _phase.GasLifetimeSeconds);
            _lifecycleCompute.SetFloat("_InitialRadius", _phase.InitialRadius);
            _lifecycleCompute.SetFloat("_FinalRadius", _phase.FinalRadius);
            _lifecycleCompute.SetBuffer(kernel, StatesId, _stateBuffer);
        }

        private void OnDestroy()
        {
            _spawnBuffer?.Dispose();
            _stateBuffer?.Dispose();
            _drawArgs?.Dispose();
            _surfaceResources?.Dispose();
        }

        private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
    }
}

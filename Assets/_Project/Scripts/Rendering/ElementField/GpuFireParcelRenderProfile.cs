using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Rendering
{
    [CreateAssetMenu(fileName = "GpuFireParcelRenderProfile", menuName = "Game/Rendering/GPU Fire Parcel Profile")]
    public sealed class GpuFireParcelRenderProfile : ScriptableObject
    {
        [Header("Pool And Spawn")]
        [SerializeField, Min(1)] private int _capacity = 2048;
        [SerializeField, Min(1)] private int _maximumSeedsPerTick = 256;
        [SerializeField, Min(0.01f)] private float _spawnInterval = 0.08f;
        [SerializeField, Min(0.001f)] private float _maximumVisualDeltaTime = 0.0333f;
        [Tooltip("每个 Fire Cell 在一次视觉采样中出生 Parcel 的概率。降低它能直接减少叠加圆斑与 Overdraw。")]
        [SerializeField, Range(0.01f, 1f)] private float _spawnProbabilityAtFullAmount = 0.22f;

        [Header("Two-stage Phase (Absolute Seconds)")]
        [Tooltip("Fire Parcel 完整保持 Liquid-like 聚合表面的时间。延长它只增加总寿命，不会缩短 Gas。")]
        [SerializeField, Min(0.01f)] private float _liquidLikeHoldSeconds = 1.1f;
        [Tooltip("Liquid-like Surface 与 Gas Splat 同时存在并平滑交叉的时间。")]
        [SerializeField, Min(0.01f)] private float _surfaceToGasBlendSeconds = 0.35f;
        [FormerlySerializedAs("_lifetime")]
        [Tooltip("完成交叉后 Gas Splat 独享的完整尾段时间；最后 30% 自动平滑淡出。")]
        [SerializeField, Min(0.05f)] private float _gasLifetimeSeconds = 1.4f;
        [SerializeField, Min(0.01f)] private float _initialRadius = 0.22f;
        [SerializeField, Min(0.01f)] private float _finalRadius = 0.7f;
        [Tooltip("兼容既有 Profile 的统一半径缩放。默认把旧版 0.22–0.7m 收缩为更细小的卡通火苗。")]
        [SerializeField, Range(0.1f, 1f)] private float _parcelRadiusScale = 0.55f;

        [Header("Motion")]
        [SerializeField] private float _buoyancy = 2.4f;
        [SerializeField, Min(0f)] private float _drag = 1.8f;
        [SerializeField, Min(0f)] private float _sourceTether = 5f;
        [SerializeField, Min(0f)] private float _noiseStrength = 0.75f;
        [SerializeField, Min(0.001f)] private float _noiseScale = 1.5f;

        [Header("Surface And Gas")]
        [SerializeField, Min(0.01f)] private float _surfaceBoundsRadius = 8f;
        [SerializeField, Min(0.01f)] private float _surfaceVoxelSize = 0.14f;
        [SerializeField, Min(0.001f)] private float _surfaceIsoLevel = 0.7f;
        [SerializeField, Min(1)] private int _surfaceParcelBudget = 512;
        [SerializeField, Min(1)] private int _maximumSurfaceTriangleCount = 131072;
        [SerializeField, Range(0.5f, 2f)] private float _surfaceKernelScale = 1.25f;
        [Tooltip("仅放大 Marching Cubes 使用的 Liquid-like Density 支撑半径，不改变 Gas 体积。")]
        [SerializeField, Range(0.5f, 2.5f)] private float _liquidLikeRadiusMultiplier = 1.35f;
        [Tooltip("仅缩放 Gas Billboard。默认小于 1，防止 Gas 一出现就遮住 Liquid-like Surface。")]
        [SerializeField, Range(0.25f, 2f)] private float _gasSplatRadiusMultiplier = 0.78f;
        [Tooltip("局部 Marching Surface 之外的 Liquid-like Splat 半径倍率；只负责远景连续性。")]
        [SerializeField, Range(0.25f, 2f)] private float _surfaceSplatRadiusMultiplier = 0.9f;
        [Tooltip("Liquid-like 远景 Splat 透明度；Near Surface 内部会通过 LOD Mask 自动淡出。")]
        [SerializeField, Range(0.01f, 1f)] private float _surfaceSplatAlphaScale = 0.48f;
        [Tooltip("局部 Fire Surface 与远景 Liquid-like Splat 的互补淡化宽度（米）。")]
        [SerializeField, Min(0.05f)] private float _surfaceSplatBlendWidth = 1.5f;
        [SerializeField, Range(0f, 1f)] private float _gasAlphaScale = 0.42f;
        [SerializeField, Range(0f, 0.4f)] private float _gasEdgeIrregularity = 0.16f;
        [SerializeField, Range(1f, 2f)] private float _gasVerticalStretch = 1.28f;
        [Tooltip("统一压低旧 HDR Color 的能量；仍保留略高于 1 的亮部供 URP Bloom 使用。")]
        [SerializeField, Range(0.05f, 1f)] private float _emissionIntensityScale = 0.28f;
        [SerializeField] private Color _coreColor = new Color(4f, 1.25f, 0.08f, 1f);
        [SerializeField] private Color _edgeColor = new Color(1.5f, 0.08f, 0.01f, 1f);

        public int Capacity => Mathf.Max(1, _capacity);
        public int MaximumSeedsPerTick => Mathf.Clamp(_maximumSeedsPerTick, 1, Capacity);
        public float SpawnInterval => Mathf.Max(0.01f, _spawnInterval);
        public float Lifetime => LiquidLikeHoldSeconds + SurfaceToGasBlendSeconds + GasLifetimeSeconds;
        public float LiquidLikeHoldSeconds => Mathf.Max(0.01f, _liquidLikeHoldSeconds);
        public float SurfaceToGasBlendSeconds => Mathf.Max(0.01f, _surfaceToGasBlendSeconds);
        public float GasLifetimeSeconds => Mathf.Max(0.05f, _gasLifetimeSeconds);
        public float MaximumVisualDeltaTime => Mathf.Max(0.001f, _maximumVisualDeltaTime);
        public float SpawnProbabilityAtFullAmount => Mathf.Clamp01(_spawnProbabilityAtFullAmount);
        public float Buoyancy => _buoyancy;
        public float Drag => Mathf.Max(0f, _drag);
        public float SourceTether => Mathf.Max(0f, _sourceTether);
        public float NoiseStrength => Mathf.Max(0f, _noiseStrength);
        public float NoiseScale => Mathf.Max(0.001f, _noiseScale);
        public float SurfaceBoundsRadius => Mathf.Max(0.01f, _surfaceBoundsRadius);
        public float SurfaceVoxelSize => Mathf.Max(0.01f, _surfaceVoxelSize);
        public float SurfaceIsoLevel => Mathf.Max(0.001f, _surfaceIsoLevel);
        public int SurfaceParcelBudget => Mathf.Clamp(_surfaceParcelBudget, 1, Capacity);
        public int MaximumSurfaceTriangleCount => Mathf.Max(1, _maximumSurfaceTriangleCount);
        public float SurfaceKernelScale => Mathf.Clamp(_surfaceKernelScale, 0.5f, 2f);
        public float LiquidLikeRadiusMultiplier => Mathf.Clamp(_liquidLikeRadiusMultiplier, 0.5f, 2.5f);
        public float GasRadiusMultiplier => Mathf.Clamp(_gasSplatRadiusMultiplier, 0.25f, 2f);
        public float SurfaceSplatRadiusMultiplier => Mathf.Clamp(_surfaceSplatRadiusMultiplier, 0.25f, 2f);
        public float SurfaceSplatAlphaScale => Mathf.Clamp(_surfaceSplatAlphaScale, 0.01f, 1f);
        public float SurfaceSplatBlendWidth => Mathf.Max(0.05f, _surfaceSplatBlendWidth);
        public float GasAlphaScale => Mathf.Clamp01(_gasAlphaScale);
        public float GasEdgeIrregularity => Mathf.Clamp(_gasEdgeIrregularity, 0f, 0.4f);
        public float GasVerticalStretch => Mathf.Clamp(_gasVerticalStretch, 1f, 2f);
        public Color CoreColor => ScaleRgb(_coreColor, _emissionIntensityScale);
        public Color EdgeColor => ScaleRgb(_edgeColor, _emissionIntensityScale);
        public float DrawBoundsRadius => SurfaceBoundsRadius
            + Mathf.Max(_initialRadius, _finalRadius)
            + Mathf.Abs(Buoyancy) * Lifetime * Lifetime * 0.5f;

        public FireParcelPhaseSettings CreatePhaseSettings() => new FireParcelPhaseSettings(
            LiquidLikeHoldSeconds,
            SurfaceToGasBlendSeconds,
            GasLifetimeSeconds,
            _initialRadius * Mathf.Clamp(_parcelRadiusScale, 0.1f, 1f),
            _finalRadius * Mathf.Clamp(_parcelRadiusScale, 0.1f, 1f));

        private void OnValidate()
        {
            _capacity = Mathf.Max(1, _capacity);
            _maximumSeedsPerTick = Mathf.Clamp(_maximumSeedsPerTick, 1, _capacity);
            _spawnInterval = Mathf.Max(0.01f, _spawnInterval);
            _liquidLikeHoldSeconds = Mathf.Max(0.01f, _liquidLikeHoldSeconds);
            _surfaceToGasBlendSeconds = Mathf.Max(0.01f, _surfaceToGasBlendSeconds);
            _gasLifetimeSeconds = Mathf.Max(0.05f, _gasLifetimeSeconds);
            _spawnProbabilityAtFullAmount = Mathf.Clamp(_spawnProbabilityAtFullAmount, 0.01f, 1f);
            _finalRadius = Mathf.Max(_initialRadius, _finalRadius);
            _parcelRadiusScale = Mathf.Clamp(_parcelRadiusScale, 0.1f, 1f);
            _liquidLikeRadiusMultiplier = Mathf.Clamp(_liquidLikeRadiusMultiplier, 0.5f, 2.5f);
            _gasSplatRadiusMultiplier = Mathf.Clamp(_gasSplatRadiusMultiplier, 0.25f, 2f);
            _surfaceSplatRadiusMultiplier = Mathf.Clamp(_surfaceSplatRadiusMultiplier, 0.25f, 2f);
            _surfaceSplatAlphaScale = Mathf.Clamp(_surfaceSplatAlphaScale, 0.01f, 1f);
            _surfaceSplatBlendWidth = Mathf.Max(0.05f, _surfaceSplatBlendWidth);
        }

        private static Color ScaleRgb(Color color, float scale)
        {
            float safeScale = Mathf.Clamp(scale, 0.05f, 1f);
            return new Color(color.r * safeScale, color.g * safeScale, color.b * safeScale, color.a);
        }
    }
}

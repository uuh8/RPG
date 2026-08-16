using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// PBF 的 Authoring Data。它只在 Runtime 初始化边界生成 Settings Snapshot；
    /// 运行中的 GPU Solver 不再逐帧读取这个 ScriptableObject，以隔离 Inspector 编辑与热路径成本。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Element Field/Liquid Simulation Profile", fileName = "LiquidSimulationProfile")]
    public sealed class LiquidSimulationProfile : ScriptableObject
    {
        [Header("Capacity And Spawn")]
        [SerializeField, Min(1)] private int _particleCapacity = 8192;
        [SerializeField, Min(1)] private int _maxSpawnRequests = 128;
        [Tooltip("显式 FluidColliderAuthoring 的 GPU Proxy 上限；超出时按层级顺序确定性截断。")]
        [SerializeField, Min(1)] private int _maxFluidColliders = 64;
        [Tooltip("一个粒子代表多少 Legacy Element Amount 单位；它是量化比例，不是物理质量。")]
        [SerializeField, Min(1)] private int _amountUnitsPerParticle = 8;

        [Header("Particle And Density")]
        [SerializeField, Min(0.001f)] private float _particleRadius = 0.1f;
        [SerializeField, Min(0.001f)] private float _particleMass = 1f;
        [SerializeField, Min(0.001f)] private float _smoothingRadius = 0.25f;
        [SerializeField, Min(0.001f)] private float _restDensity = 1000f;

        [Header("Fixed Simulation")]
        [SerializeField, Min(0.1f)] private float _fixedTickRate = 60f;
        [SerializeField, Min(1)] private int _substeps = 2;
        [SerializeField, Min(1)] private int _solverIterations = 4;
        [Tooltip("每次 PBF Apply 最多移动的距离；必须小于 Smoothing Radius，避免一次修正跨过整个邻域。")]
        [SerializeField, Min(0.0001f)] private float _maximumPositionCorrection = 0.08f;
        [SerializeField, Min(0.000001f)] private float _lambdaEpsilon = 0.0001f;
        [SerializeField, Min(0f)] private float _artificialPressure = 0.001f;
        [SerializeField, Range(0f, 1f)] private float _viscosity = 0.01f;
        [SerializeField, Min(0f)] private float _vorticity = 0.01f;
        [SerializeField] private Vector3 _gravity = new Vector3(0f, -9.81f, 0f);
        [SerializeField, Min(0.001f)] private float _maxSpeed = 25f;

        [Header("Collision And Sleep")]
        [SerializeField, Range(0f, 1f)] private float _collisionFriction = 0.1f;
        [SerializeField, Range(0f, 1f)] private float _collisionRestitution;
        [SerializeField, Min(0f)] private float _sleepThreshold = 0.01f;
        [Tooltip("相对 Rest Density 的允许误差；0.02 表示 2%。")]
        [SerializeField, Range(0f, 0.99f)] private float _sleepDensityErrorThreshold = 0.02f;
        [SerializeField, Min(1)] private int _sleepAfterStableTicks = 30;
        [SerializeField, Range(1, FluidSimulationClock.MaxSupportedCatchUpTicks)]
        private int _maxCatchUpTicks = 3;

        /// <summary>
        /// 把可编辑 Profile 转成已验证的值副本。未来 GpuPbfFluidRuntime 只在 Awake/Enable 调用一次，
        /// 从而让后续 Fixed Tick、Substep 与 Compute 参数绑定都使用同一份不可变规则。
        /// </summary>
        public LiquidSimulationSettings CreateSettings()
        {
            return new LiquidSimulationSettings(
                _particleCapacity,
                _maxSpawnRequests,
                _maxFluidColliders,
                _amountUnitsPerParticle,
                _particleRadius,
                _particleMass,
                _smoothingRadius,
                _restDensity,
                _fixedTickRate,
                _substeps,
                _solverIterations,
                _maximumPositionCorrection,
                _lambdaEpsilon,
                _artificialPressure,
                _viscosity,
                _vorticity,
                _gravity,
                _maxSpeed,
                _collisionFriction,
                _collisionRestitution,
                _sleepThreshold,
                _maxCatchUpTicks,
                _sleepDensityErrorThreshold,
                _sleepAfterStableTicks);
        }

        private void OnValidate()
        {
            // Inspector 只提供友好下限；CreateSettings 仍是唯一可信边界，
            // 因为 Asset 可由版本控制、脚本或未触发 OnValidate 的路径写入。
            _particleCapacity = Mathf.Max(1, _particleCapacity);
            _maxSpawnRequests = Mathf.Max(1, _maxSpawnRequests);
            _maxFluidColliders = Mathf.Max(1, _maxFluidColliders);
            _amountUnitsPerParticle = Mathf.Max(1, _amountUnitsPerParticle);
            _particleRadius = Mathf.Max(0.001f, _particleRadius);
            _particleMass = Mathf.Max(0.001f, _particleMass);
            _smoothingRadius = Mathf.Max(0.001f, _smoothingRadius);
            _restDensity = Mathf.Max(0.001f, _restDensity);
            _fixedTickRate = Mathf.Max(0.1f, _fixedTickRate);
            _substeps = Mathf.Max(1, _substeps);
            _solverIterations = Mathf.Max(1, _solverIterations);
            // Correction 不能跨越整个 Kernel 支持域；限制到 h 的一半可让下轮邻居查询仍是局部修正。
            _maximumPositionCorrection = Mathf.Clamp(
                _maximumPositionCorrection,
                0.0001f,
                Mathf.Min(
                    _smoothingRadius * 0.5f,
                    _particleRadius * FluidCollisionSweep.MaxSamples / (_solverIterations + 1f)));
            _lambdaEpsilon = Mathf.Max(0.000001f, _lambdaEpsilon);
            _artificialPressure = Mathf.Max(0f, _artificialPressure);
            _viscosity = Mathf.Clamp01(_viscosity);
            _vorticity = Mathf.Max(0f, _vorticity);
            float sweepCoverageAfterWorstCorrections = _particleRadius
                * FluidCollisionSweep.MaxSamples
                - _solverIterations * _maximumPositionCorrection;
            float maximumSweepCoveredSpeed = sweepCoverageAfterWorstCorrections
                * _fixedTickRate
                * _substeps;
            _maxSpeed = Mathf.Clamp(_maxSpeed, 0.001f, maximumSweepCoveredSpeed);
            _collisionFriction = Mathf.Clamp01(_collisionFriction);
            _collisionRestitution = Mathf.Clamp01(_collisionRestitution);
            _sleepThreshold = Mathf.Max(0f, _sleepThreshold);
            _sleepDensityErrorThreshold = Mathf.Clamp(_sleepDensityErrorThreshold, 0f, 0.99f);
            _sleepAfterStableTicks = Mathf.Max(1, _sleepAfterStableTicks);
            _maxCatchUpTicks = Mathf.Clamp(
                _maxCatchUpTicks,
                1,
                FluidSimulationClock.MaxSupportedCatchUpTicks);
        }
    }
}

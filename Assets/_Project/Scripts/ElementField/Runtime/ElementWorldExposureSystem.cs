using Game.Combat;
using Game.Core;
using Unity.Profiling;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 把稀疏 World Runtime 的 Interest Region Water/Fire Cell 映射为角色 Wet/Burning。
    /// Broadphase 只覆盖玩家 Interest Region；精确阶段再把 Collider Bounds 转为 Global Cell 范围，
    /// 稳定水可以让 Solver 休眠，但仍会被 Exposure 读取；Unknown/兴趣范围外 Chunk 才会被拒绝。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ElementWorldRuntime))]
    public sealed class ElementWorldExposureSystem : MonoBehaviour
    {
        private const int MaxTargetsPerQuery = 64;
        private const byte EnvironmentTeam = byte.MaxValue;
        private static readonly ProfilerMarker ExposureMarker =
            new ProfilerMarker("ElementWorld.Exposure");

        [Header("Target Query")]
        [SerializeField] private LayerMask _targetLayers;
        [SerializeField, Min(0.05f)] private float _exposureInterval = 0.25f;
        [SerializeField, Min(0f)] private float _naturalDecayHoldGraceSeconds = 0.1f;

        [Header("Status Apply Per Exposure")]
        [SerializeField, Min(0f)] private float _maxWetApplyPerTick = 10f;
        [SerializeField, Min(0f)] private float _maxBurningApplyPerTick = 10f;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private int _lastColliderCount;
        [SerializeField] private int _lastStatusTargetCount;
        [SerializeField] private int _lastWetApplicationCount;
        [SerializeField] private int _lastBurningApplicationCount;
        [SerializeField] private int _lastMaximumWaterAmount;
        [SerializeField] private int _lastMaximumFireAmount;

        // 容量在 Component 构造时固定；Exposure 热路径只覆盖数组，不创建临时集合或 LINQ Enumerator。
        private readonly Collider[] _colliderBuffer = new Collider[MaxTargetsPerQuery];
        private readonly int[] _targetIds = new int[MaxTargetsPerQuery];
        private readonly StatusController[] _targets = new StatusController[MaxTargetsPerQuery];
        private readonly byte[] _maxWater = new byte[MaxTargetsPerQuery];
        private readonly byte[] _maxFire = new byte[MaxTargetsPerQuery];

        private ElementWorldRuntime _runtime;
        private float _elapsed;
        private int _targetCount;
        private int _environmentSourceId;

        private void Awake()
        {
            _runtime = GetComponent<ElementWorldRuntime>();
            _environmentSourceId = gameObject.GetInstanceID();
        }

        private void OnEnable()
        {
            _elapsed = 0f;
        }

        private void Start()
        {
            if (_runtime == null || !_runtime.IsInitialized)
            {
                GameLog.Error(
                    "ElementWorldExposureSystem requires an initialized ElementWorldRuntime on the same GameObject.",
                    "ElementField");
            }

            if (_targetLayers.value == 0)
            {
                GameLog.Warn(
                    "ElementWorldExposureSystem has an empty Target Layers mask.",
                    "ElementField");
            }
        }

        private void OnDisable()
        {
            ClearPreviousTargets();
            _elapsed = 0f;
        }

        private void Update()
        {
            if (Time.timeScale > 0f)
                TickExposure(Time.deltaTime);
        }

        public void TickExposure(float deltaTime)
        {
            if (deltaTime <= 0f
                || float.IsNaN(deltaTime)
                || float.IsInfinity(deltaTime)
                || _runtime == null
                || !_runtime.IsInitialized)
            {
                return;
            }

            float interval = Mathf.Max(0.05f, _exposureInterval);
            _elapsed += deltaTime;
            while (_elapsed >= interval)
            {
                _elapsed -= interval;
                using (ExposureMarker.Auto())
                    ApplyExposureOnce();
            }
        }

        public void TickForTests(float deltaTime)
        {
            TickExposure(deltaTime);
        }

        private void ApplyExposureOnce()
        {
            ClearPreviousTargets();
            ResetLastDebugStats();

            Bounds activeBounds = _runtime.GetActiveWorldBounds();
            if (activeBounds.size.sqrMagnitude <= 0f)
                return;

            int colliderCount = Physics.OverlapBoxNonAlloc(
                activeBounds.center,
                activeBounds.extents,
                _colliderBuffer,
                Quaternion.identity,
                _targetLayers,
                QueryTriggerInteraction.Collide);
            _lastColliderCount = colliderCount;

            for (int i = 0; i < colliderCount; i++)
            {
                Collider candidate = _colliderBuffer[i];
                _colliderBuffer[i] = null;
                if (candidate == null)
                    continue;

                StatusController target = candidate.GetComponentInParent<StatusController>();
                if (target == null || !target.isActiveAndEnabled)
                    continue;

                int targetIndex = FindOrAddTarget(target);
                if (targetIndex >= 0)
                    SampleBounds(candidate.bounds, activeBounds, ref _maxWater[targetIndex], ref _maxFire[targetIndex]);
            }

            float holdSeconds = Mathf.Max(0.05f, _exposureInterval)
                + _naturalDecayHoldGraceSeconds;
            for (int i = 0; i < _targetCount; i++)
            {
                StatusController target = _targets[i];
                if (target == null || !target.isActiveAndEnabled)
                    continue;

                _lastMaximumWaterAmount = Mathf.Max(_lastMaximumWaterAmount, _maxWater[i]);
                _lastMaximumFireAmount = Mathf.Max(_lastMaximumFireAmount, _maxFire[i]);
                if (_maxWater[i] > 0 && _maxWetApplyPerTick > 0f)
                {
                    target.ApplySustainedStatus(
                        StatusKind.Wet,
                        _maxWetApplyPerTick * (_maxWater[i] / (float)byte.MaxValue),
                        _environmentSourceId,
                        EnvironmentTeam,
                        holdSeconds);
                    _lastWetApplicationCount++;
                }

                if (_maxFire[i] > 0 && _maxBurningApplyPerTick > 0f)
                {
                    target.ApplySustainedStatus(
                        StatusKind.Burning,
                        _maxBurningApplyPerTick * (_maxFire[i] / (float)byte.MaxValue),
                        _environmentSourceId,
                        EnvironmentTeam,
                        holdSeconds);
                    _lastBurningApplicationCount++;
                }
            }

            _lastStatusTargetCount = _targetCount;
        }

        private int FindOrAddTarget(StatusController target)
        {
            int targetId = target.gameObject.GetInstanceID();
            for (int i = 0; i < _targetCount; i++)
            {
                if (_targetIds[i] == targetId)
                    return i;
            }

            if (_targetCount >= MaxTargetsPerQuery)
                return -1;

            int index = _targetCount++;
            _targetIds[index] = targetId;
            _targets[index] = target;
            return index;
        }

        private void SampleBounds(
            Bounds targetBounds,
            Bounds activeBounds,
            ref byte maxWater,
            ref byte maxFire)
        {
            if (!targetBounds.Intersects(activeBounds))
                return;

            Vector3 sampleMin = Vector3.Max(targetBounds.min, activeBounds.min);
            Vector3 sampleMax = Vector3.Min(targetBounds.max, activeBounds.max);
            float inset = Mathf.Min(0.0001f, _runtime.CellSize * 0.001f);
            Vector3 inclusiveMax = new Vector3(
                Mathf.Max(sampleMin.x, sampleMax.x - inset),
                Mathf.Max(sampleMin.y, sampleMax.y - inset),
                Mathf.Max(sampleMin.z, sampleMax.z - inset));

            Vector3Int min = ElementWorldCoordinates.WorldToGlobalCell(
                sampleMin,
                _runtime.Origin,
                _runtime.CellSize);
            Vector3Int max = ElementWorldCoordinates.WorldToGlobalCell(
                inclusiveMax,
                _runtime.Origin,
                _runtime.CellSize);

            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                if (!_runtime.TryGetActiveCell(new Vector3Int(x, y, z), out ElementCell cell)
                    || cell.IsEmpty)
                {
                    continue;
                }

                if (cell.MaterialKind == ElementMaterialKind.Water && cell.Amount > maxWater)
                    maxWater = cell.Amount;
                else if (cell.MaterialKind == ElementMaterialKind.Fire && cell.Amount > maxFire)
                    maxFire = cell.Amount;
            }
        }

        private void ClearPreviousTargets()
        {
            for (int i = 0; i < _targetCount; i++)
            {
                _targetIds[i] = 0;
                _targets[i] = null;
                _maxWater[i] = 0;
                _maxFire[i] = 0;
            }
            _targetCount = 0;
        }

        private void ResetLastDebugStats()
        {
            _lastColliderCount = 0;
            _lastStatusTargetCount = 0;
            _lastWetApplicationCount = 0;
            _lastBurningApplicationCount = 0;
            _lastMaximumWaterAmount = 0;
            _lastMaximumFireAmount = 0;
        }

        private void OnValidate()
        {
            _exposureInterval = Mathf.Max(0.05f, _exposureInterval);
            _naturalDecayHoldGraceSeconds = Mathf.Max(0f, _naturalDecayHoldGraceSeconds);
            _maxWetApplyPerTick = Mathf.Max(0f, _maxWetApplyPerTick);
            _maxBurningApplyPerTick = Mathf.Max(0f, _maxBurningApplyPerTick);
        }
    }
}

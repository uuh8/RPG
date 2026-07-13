using System.Collections.Generic;
using UnityEngine;

namespace Game.Combat
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider))]
    public sealed class StatusVolume : MonoBehaviour
    {
        private const int MaxCollidersPerTick = 32;
        private const float MinimumTickInterval = 0.01f;

        [Header("Status Application")]
        [SerializeField] private StatusKind _statusKind = StatusKind.Wet;
        [SerializeField, Min(MinimumTickInterval)] private float _tickInterval = 0.25f;
        [SerializeField, Min(0f)] private float _applyAmount = 5f;
        [SerializeField] private byte _sourceTeam = byte.MaxValue;
        [SerializeField] private LayerMask _targetLayers = ~0;

        private readonly Collider[] _colliderBuffer = new Collider[MaxCollidersPerTick];
        private readonly HashSet<int> _appliedTargetIds = new HashSet<int>(MaxCollidersPerTick);
        private BoxCollider _box;
        private float _elapsedTime;
        private int _sourceId;

        private void Awake()
        {
            CacheIdentityAndShape();
        }

        private void Reset()
        {
            // Volume 只描述环境覆盖范围，不应作为实体障碍参与碰撞响应。
            _box = GetComponent<BoxCollider>();
            if (_box != null)
                _box.isTrigger = true;
        }

        private void OnValidate()
        {
            _tickInterval = Mathf.Max(MinimumTickInterval, _tickInterval);
            _applyAmount = Mathf.Max(0f, _applyAmount);
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        public void ConfigureForTests(StatusKind kind, float tickInterval, float applyAmount, byte sourceTeam)
        {
            _statusKind = kind;
            _tickInterval = Mathf.Max(MinimumTickInterval, tickInterval);
            _applyAmount = Mathf.Max(0f, applyAmount);
            _sourceTeam = sourceTeam;
            CacheIdentityAndShape();
        }

        public void TickForTests(float deltaTime)
        {
            Tick(deltaTime);
        }

        private void Tick(float deltaTime)
        {
            if (deltaTime <= 0f || _applyAmount <= 0f)
                return;

            _elapsedTime += deltaTime;
            if (_elapsedTime < _tickInterval)
                return;

            int completedTicks = Mathf.FloorToInt(_elapsedTime / _tickInterval);
            _elapsedTime -= completedTicks * _tickInterval;

            // 卡顿帧可能跨过多个 tick。合并施加强度可避免同一帧重复执行 Physics 查询，
            // 对当前“强度累加并 Clamp”的状态语义与逐次施加等价。
            QueryAndApply(_applyAmount * completedTicks);
        }

        private void QueryAndApply(float amount)
        {
            if (_box == null)
                CacheIdentityAndShape();
            if (_box == null)
                return;

            Transform boxTransform = _box.transform;
            Vector3 center = boxTransform.TransformPoint(_box.center);
            Vector3 scale = boxTransform.lossyScale;
            Vector3 halfExtents = Vector3.Scale(
                _box.size * 0.5f,
                new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));

            int hitCount = Physics.OverlapBoxNonAlloc(
                center,
                halfExtents,
                _colliderBuffer,
                boxTransform.rotation,
                _targetLayers,
                QueryTriggerInteraction.Collide);

            _appliedTargetIds.Clear();
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = _colliderBuffer[i];
                if (hit == null)
                    continue;

                StatusController target = hit.GetComponentInParent<StatusController>();
                if (target == null || !_appliedTargetIds.Add(target.GetInstanceID()))
                    continue;

                target.ApplyStatus(_statusKind, amount, _sourceId, _sourceTeam);
            }
        }

        private void CacheIdentityAndShape()
        {
            _box = GetComponent<BoxCollider>();
            _sourceId = gameObject.GetInstanceID();
        }

        private void OnDrawGizmosSelected()
        {
            BoxCollider box = _box != null ? _box : GetComponent<BoxCollider>();
            if (box == null)
                return;

            Color color = GetStatusColor(_statusKind);
            Matrix4x4 previousMatrix = Gizmos.matrix;
            Color previousColor = Gizmos.color;

            // Collider 的 center/size 位于 Local Space；localToWorldMatrix 一次性加入位移、旋转和缩放，
            // 让 Scene 视图中的 Gizmo 与 Physics.OverlapBoxNonAlloc 的 World Space 查询保持一致。
            Gizmos.matrix = box.transform.localToWorldMatrix;
            Gizmos.color = new Color(color.r, color.g, color.b, 0.12f);
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = new Color(color.r, color.g, color.b, 0.9f);
            Gizmos.DrawWireCube(box.center, box.size);

            Gizmos.matrix = previousMatrix;
            Gizmos.color = previousColor;
        }

        private static Color GetStatusColor(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.Burning:
                    return new Color(1f, 0.25f, 0.05f);
                case StatusKind.Wet:
                    return new Color(0.05f, 0.45f, 1f);
                case StatusKind.Poisoned:
                    return new Color(0.25f, 1f, 0.15f);
                case StatusKind.Sticky:
                    return new Color(0.7f, 0.2f, 0.85f);
                default:
                    return Color.white;
            }
        }
    }
}

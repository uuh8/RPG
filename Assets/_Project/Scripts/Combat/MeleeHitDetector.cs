using System.Collections.Generic;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 近战命中判定。窗口激活期间每帧在武器挂点处做 OverlapBoxNonAlloc,
    /// 过滤自身/同阵营/已死/已命中后构造 DamageRequest 交目标结算。
    ///
    /// 窗口由外部（Character 侧动画驱动 / 连段系统）通过 Open/CloseHitWindow 控制,
    /// 本组件对动画系统无知 —— 这正是连段系统的接入缝。
    /// </summary>
    public class MeleeHitDetector : MonoBehaviour
    {
        [SerializeField] private AttackDefinition _attack;
        [SerializeField] private Transform _weaponPivot;          // 命中体积中心；为空时退回本 Transform
        [SerializeField] private Transform _ownerRoot;            // 攻击者根，用于 AttackerId
        [Tooltip("必须与攻击者自身 HealthComponent.TeamId 一致；命中判定据此跳过同阵营（含自身）。")]
        [SerializeField] private byte _attackerTeam = 0;
        [SerializeField] private LayerMask _hitMask = ~0;

        // 单帧 OverlapBox 命中上限。重叠 collider 超过此数时多余者被静默丢弃；如需更多请调大。
        private const int MaxHitsPerFrame = 16;
        private readonly Collider[] _buf = new Collider[MaxHitsPerFrame];
        private readonly HashSet<int> _hitSet = new HashSet<int>(); // per-swing 去重，预分配复用

        private bool _windowActive;
        private int _attackerId;

        /// <summary>开启攻击激活窗口。幂等：窗口已开时不重复清空去重集,
        /// 以便 Character 侧每帧调用 EnsureOpen 语义。</summary>
        public void OpenHitWindow()
        {
            if (_windowActive) return;
            _windowActive = true;
            _hitSet.Clear();
            _attackerId = _ownerRoot != null ? _ownerRoot.GetInstanceID() : gameObject.GetInstanceID();
        }

        /// <summary>关闭攻击激活窗口。幂等。</summary>
        public void CloseHitWindow()
        {
            _windowActive = false;
        }

        private void Update()
        {
            if (!_windowActive || _attack == null) return;
            DoOverlap();
        }

        private void DoOverlap()
        {
            Transform pivot = _weaponPivot != null ? _weaponPivot : transform;
            int count = Physics.OverlapBoxNonAlloc(
                pivot.position, _attack.HalfExtents, _buf, pivot.rotation,
                _hitMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                IDamageable target = ResolveDamageable(_buf[i]);
                if (target == null) continue;
                if (!target.IsAlive) continue;
                if (target.TeamId == _attackerTeam) continue;     // 跳过同阵营（含自身）

                Object targetObj = target as Object;
                if (targetObj == null) continue;
                int id = targetObj.GetInstanceID();
                if (!_hitSet.Add(id)) continue;                   // 本次挥砍已命中 → 去重

                Vector3 hitPoint = _buf[i].ClosestPoint(pivot.position);
                // pivot 在目标碰撞体内部时 ClosestPoint 返回 pivot 本身，差值为零 → 退回武器朝向，
                // 避免 HitDirection 为 (0,0,0) 污染下游受击反应/VFX。
                Vector3 toHit = hitPoint - pivot.position;
                Vector3 hitDir = toHit.sqrMagnitude > 1e-6f ? toHit.normalized : pivot.forward;

                var req = new DamageRequest(
                    _attackerId, _attackerTeam, _attack.BaseAmount,
                    _attack.Type, hitPoint, hitDir);
                target.ReceiveHit(in req);
            }
        }

        // 命中体 Collider → IDamageable 解析。集中于此，便于后续按需缓存（见计划文末性能注记）。
        private static IDamageable ResolveDamageable(Collider col)
        {
            return col.GetComponentInParent<IDamageable>();
        }

        private void OnDrawGizmosSelected()
        {
            if (_attack == null) return;
            Transform pivot = _weaponPivot != null ? _weaponPivot : transform;
            Gizmos.color = _windowActive ? Color.red : Color.yellow;
            Gizmos.matrix = Matrix4x4.TRS(pivot.position, pivot.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, _attack.HalfExtents * 2f);
        }
    }
}

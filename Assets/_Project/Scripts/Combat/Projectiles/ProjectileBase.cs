using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 投射物基类。抽象所有飞行投射物的公共骨架：Init 注入伤害快照/初速度/忽略施法者碰撞/定向/计时；
    /// OnCollisionEnter 统一处理 同阵营穿过 / 敌方结算一次 IDamageable.ReceiveHit / 命中后销毁。
    /// 子类只需重写 OnImpact 决定"命中后、销毁前的额外表现"（爆炸特效、附加状态等）。
    /// Arrow / Fireball / NovaFireball 皆派生于此，消除三者重复逻辑。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public abstract class ProjectileBase : MonoBehaviour
    {
        [Header("飞行 (基类通用)")]
        [SerializeField] protected float _maxLifetime = 5f; // 超时自毁，防漏网投射物累积
        // 模型朝向修正：LookRotation 把 +Z 对到飞行方向；箭尖沿 +Y 的模型填 (90,0,0)，球形特效填 0。
        [SerializeField] protected Vector3 _modelForwardOffsetEuler = Vector3.zero;

        protected Rigidbody _rb;
        protected Collider _collider;

        // 伤害快照（Init 注入；命中时构造 DamageRequest，不回查可能已销毁的攻击者）
        protected byte _attackerTeam;
        protected int _attackerId;
        protected float _damage;
        protected DamageType _type;

        private bool _consumed; // 防同一物理步多次碰撞重复结算/销毁

        protected virtual void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
        }

        /// <summary>
        /// 攻击方生成瞬间调用：注入伤害快照与初速度，忽略与施法者自身碰撞，定向、计时。
        /// useGravity：抛物线箭矢传 true（默认）；直线投射物（瞄准直射/火球/陨石）传 false。
        /// </summary>
        public void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                         Vector3 velocity, Collider casterCollider, bool useGravity = true)
        {
            _attackerTeam = attackerTeam;
            _attackerId = attackerId;
            _damage = damage;
            _type = type;

            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_collider == null) _collider = GetComponent<Collider>();

            // 忽略与施法者自身碰撞，避免出膛瞬间撞到施法者 collider 即自毁
            if (casterCollider != null && _collider != null)
                Physics.IgnoreCollision(_collider, casterCollider);

            _rb.useGravity = useGravity;
            // 高速投射物防穿透：连续碰撞检测可命中薄的静态碰撞体（地面），避免快速飞行时隧穿穿地
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rb.linearVelocity = velocity; // Unity 6：Rigidbody.velocity → linearVelocity
            if (velocity.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(velocity) * Quaternion.Euler(_modelForwardOffsetEuler);

            Destroy(gameObject, _maxLifetime);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_consumed) return;

            IDamageable target = collision.collider.GetComponentInParent<IDamageable>();

            // 同阵营（施法者自身/队友）→ 穿过，不结算不销毁
            if (target != null && target.TeamId == _attackerTeam)
                return;

            Vector3 hitPoint = collision.GetContact(0).point;
            bool damaged = false;

            // 敌方且存活 → 结算一次伤害
            if (target != null && target.IsAlive)
            {
                Vector3 vel = _rb != null ? _rb.linearVelocity : Vector3.zero;
                Vector3 hitDir = vel.sqrMagnitude > 1e-6f ? vel.normalized : transform.forward;
                var req = new DamageRequest(_attackerId, _attackerTeam, _damage, _type, hitPoint, hitDir);
                target.ReceiveHit(in req);
                damaged = true;
            }

            // 子类扩展点：命中敌方或环境都会走到（便于"撞地也爆炸"）
            OnImpact(collision, target, hitPoint, damaged);

            _consumed = true;
            Destroy(gameObject);
        }

        /// <summary>命中后、销毁前的子类扩展点（默认空）。target 可能为 null（命中环境）；damaged 表示本次是否结算了伤害。</summary>
        protected virtual void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged) { }
    }
}

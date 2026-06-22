using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 飞行箭矢。与 MeleeHitDetector 平级：复用 IDamageable/DamageRequest 的伤害提交路径，
    /// 区别只在"如何发现命中"——飞行碰撞（OnCollisionEnter），而非瞬时 OverlapBox。
    /// 生成时由攻击方 Init 注入伤害快照与初速度；命中敌方结算并销毁，命中环境直接销毁，
    /// 命中同阵营（射手自身/队友）则穿过；超时自毁兜底。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public class Arrow : MonoBehaviour
    {
        [SerializeField] private float _maxLifetime = 5f; // 超时自毁，防漏网箭矢累积

        // 模型"箭尖"轴向修正：LookRotation 把 +Z 对到飞行方向（Unity 约定），但本箭模型箭尖沿 +Y，
        // 故默认 +90°X 把 +Y 转到飞行方向。换了箭尖朝向不同的模型时在 Inspector 改这里（如指向反了改 -90）。
        [SerializeField] private Vector3 _modelForwardOffsetEuler = new Vector3(90f, 0f, 0f);

        private Rigidbody _rb;
        private Collider _collider;

        // 伤害快照（Init 注入；命中时构造 DamageRequest）
        private byte _attackerTeam;
        private int _attackerId;
        private float _damage;
        private DamageType _type;

        private bool _consumed; // 防同一物理步多次碰撞重复结算/重复销毁

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
        }

        /// <summary>
        /// 攻击方在生成瞬间调用：注入伤害快照与初速度，忽略与射手自身碰撞，定向、计时。
        /// </summary>
        public void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                         Vector3 velocity, Collider shooterCollider)
        {
            _attackerTeam = attackerTeam;
            _attackerId = attackerId;
            _damage = damage;
            _type = type;

            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_collider == null) _collider = GetComponent<Collider>();

            // 忽略与射手自身碰撞，避免出膛瞬间撞到射手 collider 即自毁
            if (shooterCollider != null && _collider != null)
                Physics.IgnoreCollision(_collider, shooterCollider);

            _rb.linearVelocity = velocity; // Unity 6：Rigidbody.velocity → linearVelocity
            if (velocity.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(velocity) * Quaternion.Euler(_modelForwardOffsetEuler);

            Destroy(gameObject, _maxLifetime);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_consumed) return;

            IDamageable target = collision.collider.GetComponentInParent<IDamageable>();

            // 同阵营（射手自身/队友）→ 穿过，不结算不销毁
            if (target != null && target.TeamId == _attackerTeam)
                return;

            // 敌方且存活 → 结算一次伤害
            if (target != null && target.IsAlive)
            {
                Vector3 hitPoint = collision.GetContact(0).point;
                Vector3 vel = _rb != null ? _rb.linearVelocity : Vector3.zero;
                Vector3 hitDir = vel.sqrMagnitude > 1e-6f ? vel.normalized : transform.forward;
                var req = new DamageRequest(_attackerId, _attackerTeam, _damage, _type, hitPoint, hitDir);
                target.ReceiveHit(in req);
            }

            // 命中敌方 / 死敌 / 环境（无 IDamageable）→ 销毁
            _consumed = true;
            Destroy(gameObject);
        }
    }
}

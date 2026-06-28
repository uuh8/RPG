using System.Collections.Generic;
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
        [Tooltip("命中后停留多久再销毁(秒)。0 = 立即消失(爆炸类)；箭矢类设 >0 可插在目标/地面上残留一小段时间。")]
        [SerializeField] protected float _impactLingerTime = 0f;

        protected Rigidbody _rb;
        protected Collider _collider;

        // 伤害快照（Init 注入；命中时构造 DamageRequest，不回查可能已销毁的攻击者）
        protected byte _attackerTeam;
        protected int _attackerId;
        protected float _damage;
        protected DamageType _type;

        private bool _consumed; // 防同一物理步多次碰撞重复结算/销毁
        private Vector3 _launchVelocity; // Init 注入的初速度快照：同队穿过时据此恢复被弹偏的直线投射物速度

        // 在场投射物注册表：新生成的投射物与所有"同阵营"已存在投射物互相 IgnoreCollision，
        // 避免同队火球互撞（连发自撞偏移 / 同队两球相撞误爆炸）。异队不忽略 → 仍碰撞 → 各自爆炸。
        private static readonly List<ProjectileBase> s_active = new List<ProjectileBase>(32);

        protected virtual void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
        }

        protected virtual void OnDestroy()
        {
            s_active.Remove(this); // 从在场表注销（命中销毁 / 超时自毁 / 场景卸载）
        }

        /// <summary>飞行中是否每帧把模型朝向对齐当前速度方向（抛物线箭矢用：机头随下坠俯冲）。默认否（直线投射物方向恒定，Init 定一次即可）。</summary>
        protected virtual bool FaceVelocityInFlight => false;

        protected virtual void FixedUpdate()
        {
            // 命中后(_consumed)或非抛物线投射物不更新；命中冻结(velocity≈0)时自动停止，保留命中姿态
            if (_consumed || !FaceVelocityInFlight || _rb == null) return;
            Vector3 v = _rb.linearVelocity;
            if (v.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(v) * Quaternion.Euler(_modelForwardOffsetEuler);
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
            _launchVelocity = velocity; // 直线投射物被同队物体擦碰弹偏后，据此恢复原方向

            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_collider == null) _collider = GetComponent<Collider>();

            // 忽略与施法者自身碰撞，避免出膛瞬间撞到施法者 collider 即自毁
            if (casterCollider != null && _collider != null)
                Physics.IgnoreCollision(_collider, casterCollider);

            // 与同阵营的其它在场投射物互相忽略碰撞，并把自己登记进表
            RegisterAndIgnoreSameTeamProjectiles();

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

            // 同阵营（施法者自身/队友）→ 穿过，不结算不销毁。
            // 投射物注册表只覆盖"投射物 vs 投射物"；角色（队友）在此反应式处理：
            // 忽略后续接触 + 恢复直线初速度方向，消除火球擦过队友被物理弹偏。
            if (target != null && target.TeamId == _attackerTeam)
            {
                if (_collider != null)
                    Physics.IgnoreCollision(_collider, collision.collider);
                if (!FaceVelocityInFlight && _rb != null)
                    _rb.linearVelocity = _launchVelocity; // 抛物线投射物速度时变，不恢复
                return;
            }

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

            // 子类扩展点：命中敌方或环境都会走到（便于"撞地也爆炸"/"插在目标上残留"）
            OnImpact(collision, target, hitPoint, damaged);

            _consumed = true;
            Destroy(gameObject, _impactLingerTime); // 0=立即；箭矢类>0 可残留（OnImpact 内已冻结物理）
        }

        /// <summary>命中后、销毁前的子类扩展点（默认空）。target 可能为 null（命中环境）；damaged 表示本次是否结算了伤害。</summary>
        protected virtual void OnImpact(Collision collision, IDamageable target, Vector3 hitPoint, bool damaged) { }

        /// <summary>
        /// 与所有"同阵营"已在场投射物互相 IgnoreCollision，再把自己登记进表。
        /// 解决：①快速连发自己的火球在出膛处互撞被弹偏；②同队两枚火球相撞误爆炸（它们本就不该互相作用）。
        /// 异阵营投射物不忽略 → 仍会物理碰撞 → 各自 OnCollisionEnter 触发爆炸（保留"异队火球相撞才爆炸"）。
        /// 在离散输入时一次性执行（非每帧热路径），遵循"按键时一次性分配可接受"。
        /// </summary>
        private void RegisterAndIgnoreSameTeamProjectiles()
        {
            for (int i = 0; i < s_active.Count; i++)
            {
                ProjectileBase other = s_active[i];
                if (other == null || other == this) continue; // Unity 重载 == 可识别已销毁对象
                if (other._attackerTeam == _attackerTeam && other._collider != null && _collider != null)
                    Physics.IgnoreCollision(_collider, other._collider);
            }
            s_active.Add(this);
        }
    }
}

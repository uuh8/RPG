using System.Collections.Generic;
using Game.Core;
using UnityEngine;
using Unity.Profiling;

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
        [Tooltip("追踪目标扫描间隔。仅配置追踪修正的投射物会使用。")]
        [SerializeField] private float _homingScanInterval = 0.1f;

        protected Rigidbody _rb;
        protected Collider _collider;

        // 伤害快照（Init 注入；命中时构造 DamageRequest，不回查可能已销毁的攻击者）
        protected byte _attackerTeam;
        protected int _attackerId;
        protected float _damage;
        protected DamageType _type;

        private bool _consumed; // 防同一物理步多次碰撞重复结算/销毁
        private Vector3 _launchVelocity; // Init 注入的初速度快照：同队穿过时据此恢复被弹偏的直线投射物速度
        private int _bounceRemaining;
        private float _currentSpeed;
        private bool _timedTriggerArmed;
        private float _timedTriggerRemaining;
        private float _homingRadius;
        private float _homingDuration;
        private float _homingTurnRateDegrees;
        private bool _homingEnabled;
        private bool _homingAcquired;
        private float _homingRemaining;
        private float _homingScanTimer;
        private Collider _homingTargetCollider;
        private IDamageable _homingTargetDamageable;
        private float _orbitRadius;
        private float _orbitAngularSpeedDegrees;
        private float _orbitAngleDegrees;
        private float _orbitPlaneTiltDegrees;
        private bool _orbitEnabled;
        private Vector3 _orbitCenter;
        private Vector3 _orbitForward;
        private Vector3 _orbitRight;
        private Vector3 _orbitUp;
        private float _orbitCenterSpeed;
        private bool _hasDebugReflection;
        private Vector3 _lastDebugCollisionPoint;
        private Vector3 _lastDebugCollisionNormal;
        private Vector3 _lastDebugReflectedDirection;
        private float _lastDebugCollisionTime;
        private bool _debugMissingRigidbodyWarned;
        private bool _initialized;

        /// <summary>
        /// 命中真实目标/环境的瞬间触发（命中点, 命中方向）。上层（法术触发）据此在命中点再施放载荷，
        /// 保持 Combat 不反依赖 Skills/Character——这里只发一个通用通知。同阵营穿过不算命中、不触发；超时自毁不触发。
        /// </summary>
        public event System.Action<Vector3, Vector3> Impacted;

        /// <summary>
        /// 定时触发计时器到点时广播当前位置与当前方向。Combat 不知道 payload；上层可选择订阅。
        /// 提前命中/销毁不会触发该事件。
        /// </summary>
        public event System.Action<Vector3, Vector3> TimedTriggerElapsed;

        // 在场投射物注册表：新生成的投射物与所有"同阵营"已存在投射物互相 IgnoreCollision，
        // 避免同队火球互撞（连发自撞偏移 / 同队两球相撞误爆炸）。异队不忽略 → 仍碰撞 → 各自爆炸。
        private static readonly List<ProjectileBase> s_active = new List<ProjectileBase>(32);
        private static readonly Collider[] s_homingHits = new Collider[16];
        private static readonly ProfilerMarker s_motionMarker = new ProfilerMarker("Projectile.Motion");

        public static int ActiveCount => s_active.Count;
        internal static IReadOnlyList<ProjectileBase> ActiveProjectiles => s_active;

        protected virtual void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
        }

        protected virtual void OnDestroy()
        {
            Unregister(); // 从在场表注销（命中销毁 / 超时自毁 / 场景卸载）
        }

        /// <summary>飞行中是否每帧把模型朝向对齐当前速度方向（抛物线箭矢用：机头随下坠俯冲）。默认否（直线投射物方向恒定，Init 定一次即可）。</summary>
        protected virtual bool FaceVelocityInFlight => false;

        protected virtual void OnEnable()
        {
            if (_initialized)
                RegisterAndIgnoreSameTeamProjectiles();
        }

        protected virtual void OnDisable()
        {
            Unregister();
        }

        private void Update()
        {
            TickTimedTrigger();
            OnProjectileUpdate();
        }

        protected virtual void OnProjectileUpdate() { }

        private void TickTimedTrigger()
        {
            if (_consumed || !_timedTriggerArmed) return;

            _timedTriggerRemaining -= Time.deltaTime;
            if (_timedTriggerRemaining > 0f) return;

            _consumed = true;
            TimedTriggerElapsed?.Invoke(transform.position, ResolveCurrentDirection());
            Destroy(gameObject);
        }

        protected virtual void FixedUpdate()
        {
            if (_consumed || _rb == null) return;

            using (s_motionMarker.Auto())
            {
                if (_orbitEnabled)
                    TickOrbit(Time.fixedDeltaTime);
                else
                    TickHoming(Time.fixedDeltaTime);

                // 非抛物线投射物不更新模型朝向；命中冻结(velocity≈0)时自动停止，保留命中姿态
                if (!FaceVelocityInFlight) return;
                Vector3 v = _rb.linearVelocity;
                if (v.sqrMagnitude > 1e-6f)
                    transform.rotation = Quaternion.LookRotation(v) * Quaternion.Euler(_modelForwardOffsetEuler);
            }
        }

        /// <summary>
        /// 攻击方生成瞬间调用：注入伤害快照与初速度，忽略与施法者自身碰撞，定向、计时。
        /// useGravity：抛物线箭矢传 true（默认）；直线投射物（瞄准直射/火球/陨石）传 false。
        /// </summary>
        public virtual void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                                 Vector3 velocity, Collider casterCollider, bool useGravity = true)
        {
            ClearDebugReflectionFacts();
            _attackerTeam = attackerTeam;
            _attackerId = attackerId;
            _damage = damage;
            _type = type;
            _launchVelocity = velocity; // 直线投射物被同队物体擦碰弹偏后，据此恢复原方向
            _currentSpeed = velocity.magnitude;

            if (_rb == null) _rb = GetComponent<Rigidbody>();
            if (_collider == null) _collider = GetComponent<Collider>();

            // 忽略与施法者自身碰撞，避免出膛瞬间撞到施法者 collider 即自毁
            if (casterCollider != null && _collider != null)
                Physics.IgnoreCollision(_collider, casterCollider);

            // 与同阵营的其它在场投射物互相忽略碰撞，并把自己登记进表
            _initialized = true;
            RegisterAndIgnoreSameTeamProjectiles();

            _rb.useGravity = useGravity;
            // 高速投射物防穿透：连续碰撞检测可命中薄的静态碰撞体（地面），避免快速飞行时隧穿穿地
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rb.linearVelocity = velocity; // Unity 6：Rigidbody.velocity → linearVelocity
            if (_orbitEnabled)
                InitializeOrbit(velocity);
            if (velocity.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(velocity) * Quaternion.Euler(_modelForwardOffsetEuler);

            Destroy(gameObject, _maxLifetime);
        }

        /// <summary>配置本次投射物可用的环境弹跳次数。由 SpellCaster 从 EmitCommand 注入。</summary>
        public void ConfigureBounce(int bounceCount)
        {
            _bounceRemaining = Mathf.Max(0, bounceCount);
        }

        /// <summary>配置本次投射物的有限追踪能力。由 SpellCaster 从 EmitCommand 注入。</summary>
        public void ConfigureHoming(float radius, float duration, float turnRateDegrees)
        {
            _homingRadius = Mathf.Max(0f, radius);
            _homingDuration = Mathf.Max(0f, duration);
            _homingTurnRateDegrees = Mathf.Max(0f, turnRateDegrees);
            _homingEnabled = _homingRadius > 0f && _homingDuration > 0f && _homingTurnRateDegrees > 0f;
            _homingAcquired = false;
            _homingRemaining = 0f;
            _homingScanTimer = 0f;
            _homingTargetCollider = null;
            _homingTargetDamageable = null;
        }

        /// <summary>配置本次投射物的轨道螺旋运动。由 SpellCaster 从 EmitCommand 注入。</summary>
        public void ConfigureOrbit(float radius, float angularSpeedDegrees, float phaseOffsetDegrees, float planeTiltDegrees)
        {
            _orbitRadius = Mathf.Max(0f, radius);
            _orbitAngularSpeedDegrees = angularSpeedDegrees;
            _orbitAngleDegrees = phaseOffsetDegrees;
            _orbitPlaneTiltDegrees = planeTiltDegrees;
            _orbitEnabled = _orbitRadius > 0f && !Mathf.Approximately(_orbitAngularSpeedDegrees, 0f);
            _orbitCenter = Vector3.zero;
            _orbitForward = Vector3.forward;
            _orbitRight = Vector3.right;
            _orbitUp = Vector3.up;
            _orbitCenterSpeed = 0f;
        }

        /// <summary>
        /// 由保护盾调用：按护盾球面法线反射当前投射物，并把伤害归属改成护盾释放者。
        /// </summary>
        public bool ReflectByShield(Vector3 shieldNormal, byte newTeam, int newAttackerId)
        {
            if (_consumed || _rb == null)
                return false;

            Vector3 velocity = _rb.linearVelocity;
            if (velocity.sqrMagnitude <= 1e-6f)
                return false;

            Vector3 normal = shieldNormal.sqrMagnitude > 1e-6f ? shieldNormal.normalized : -velocity.normalized;
            Vector3 incoming = velocity.normalized;
            Vector3 reflected = Vector3.Reflect(incoming, normal);
            if (reflected.sqrMagnitude <= 1e-6f)
                return false;

            reflected.Normalize();
            RecordDebugReflection(transform.position, normal, reflected);
            float speed = velocity.magnitude;
            Vector3 newVelocity = reflected * speed;

            _attackerTeam = newTeam;
            _attackerId = newAttackerId;
            _orbitEnabled = false;
            _homingEnabled = false;
            _homingAcquired = false;
            _homingTargetCollider = null;
            _homingTargetDamageable = null;

            _rb.position += normal * 0.03f;
            _rb.linearVelocity = newVelocity;
            _launchVelocity = newVelocity;
            _currentSpeed = speed;
            RefreshSameTeamProjectileIgnores();

            transform.rotation = Quaternion.LookRotation(reflected) * Quaternion.Euler(_modelForwardOffsetEuler);
            return true;
        }

        private void InitializeOrbit(Vector3 velocity)
        {
            Vector3 forward = velocity.sqrMagnitude > 1e-6f ? velocity.normalized : transform.forward;
            if (forward.sqrMagnitude <= 1e-6f)
                forward = Vector3.forward;

            _orbitForward = forward.normalized;
            _orbitCenter = transform.position;
            _orbitCenterSpeed = velocity.magnitude;

            Vector3 upSeed = Mathf.Abs(Vector3.Dot(_orbitForward, Vector3.up)) > 0.95f
                ? Vector3.forward
                : Vector3.up;

            Quaternion basis = Quaternion.LookRotation(_orbitForward, upSeed);
            if (!Mathf.Approximately(_orbitPlaneTiltDegrees, 0f))
                basis = Quaternion.AngleAxis(_orbitPlaneTiltDegrees, basis * Vector3.right) * basis;

            _orbitRight = basis * Vector3.right;
            _orbitUp = basis * Vector3.up;
        }

        /// <summary>
        /// 开启定时触发。delaySeconds <= 0 时在下一次 Update 触发，避免在 Arm 调用栈内重入施法。
        /// </summary>
        public void ArmTimedTrigger(float delaySeconds)
        {
            _timedTriggerArmed = true;
            _timedTriggerRemaining = Mathf.Max(0f, delaySeconds);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_consumed) return;

            IDamageable target = collision.collider.GetComponentInParent<IDamageable>();

            // 同阵营（施法者自身/队友）→ 穿过，不结算不销毁、不算命中（不触发）。
            if (target != null && target.TeamId == _attackerTeam)
            {
                if (_collider != null)
                    Physics.IgnoreCollision(_collider, collision.collider);
                if (!FaceVelocityInFlight && _rb != null)
                    _rb.linearVelocity = _launchVelocity; // 抛物线投射物速度时变，不恢复
                return;
            }

            if (TryBounce(collision, target))
                return;

            if (TryHandleCollisionBeforeDefault(collision, target))
                return;

            Vector3 hitPoint = collision.GetContact(0).point;
            Vector3 vel = _rb != null ? _rb.linearVelocity : Vector3.zero;
            Vector3 hitDir = vel.sqrMagnitude > 1e-6f ? vel.normalized : transform.forward;
            bool damaged = false;

            // 敌方且存活 → 结算一次伤害
            if (target != null && target.IsAlive)
            {
                var req = new DamageRequest(_attackerId, _attackerTeam, _damage, _type, hitPoint, hitDir);
                target.ReceiveHit(in req);
                damaged = true;
            }

            // 子类扩展点：命中敌方或环境都会走到（撞地也爆炸/插在目标上残留）
            OnImpact(collision, target, hitPoint, damaged);

            // 命中通知：法术触发据此在命中点跑载荷（普通投射物无监听者，空触发无开销）
            _consumed = true;
            Impacted?.Invoke(hitPoint, hitDir);

            Destroy(gameObject, _impactLingerTime);
        }

        protected virtual bool TryHandleCollisionBeforeDefault(Collision collision, IDamageable target)
        {
            return false;
        }

        private bool TryBounce(Collision collision, IDamageable target)
        {
            if (target != null || _bounceRemaining <= 0 || _rb == null)
                return false;

            if (collision.contactCount <= 0)
                return false;

            Vector3 velocity = _rb.linearVelocity;
            Vector3 intendedVelocity = _launchVelocity.sqrMagnitude > 1e-6f ? _launchVelocity : velocity;
            Vector3 incoming = intendedVelocity.sqrMagnitude > 1e-6f ? intendedVelocity.normalized : transform.forward;
            Vector3 normal = collision.GetContact(0).normal;
            float normalDot = Vector3.Dot(incoming, normal);
            Vector3 reflected = normalDot < 0f ? Vector3.Reflect(incoming, normal) : incoming;

            if (reflected.sqrMagnitude <= 1e-6f)
                return false;

            reflected.Normalize();
            ContactPoint contact = collision.GetContact(0);
            RecordDebugReflection(contact.point, normal, reflected);
            float speed = _currentSpeed > 1e-6f ? _currentSpeed : intendedVelocity.magnitude;
            if (speed <= 1e-6f)
                speed = _launchVelocity.magnitude;

            _bounceRemaining--;
            _orbitEnabled = false;
            Vector3 newVelocity = reflected * speed;
            _rb.position += normal * 0.03f;
            _rb.linearVelocity = newVelocity;
            _launchVelocity = newVelocity;

            transform.rotation = Quaternion.LookRotation(reflected) * Quaternion.Euler(_modelForwardOffsetEuler);
            return true;
        }

        private void TickOrbit(float deltaTime)
        {
            if (!_orbitEnabled || _rb == null)
                return;

            float safeDeltaTime = Mathf.Max(deltaTime, 1e-5f);
            _orbitCenter += _orbitForward * _orbitCenterSpeed * safeDeltaTime;
            _orbitAngleDegrees += _orbitAngularSpeedDegrees * safeDeltaTime;

            float radians = _orbitAngleDegrees * Mathf.Deg2Rad;
            Vector3 offset = (_orbitRight * Mathf.Cos(radians) + _orbitUp * Mathf.Sin(radians)) * _orbitRadius;
            Vector3 targetPosition = _orbitCenter + offset;
            Vector3 newVelocity = (targetPosition - _rb.position) / safeDeltaTime;

            _rb.linearVelocity = newVelocity;
            _launchVelocity = newVelocity;
            _currentSpeed = newVelocity.magnitude;
        }

        private void TickHoming(float deltaTime)
        {
            if (!_homingEnabled)
                return;

            if (!_homingAcquired)
            {
                _homingScanTimer -= deltaTime;
                if (_homingScanTimer > 0f)
                    return;

                _homingScanTimer = Mathf.Max(0.02f, _homingScanInterval);
                TryAcquireHomingTarget();
                return;
            }

            if (_homingRemaining <= 0f)
                return;

            if (_homingTargetCollider == null ||
                !_homingTargetCollider.enabled ||
                !_homingTargetCollider.gameObject.activeInHierarchy ||
                _homingTargetDamageable == null ||
                !_homingTargetDamageable.IsAlive)
            {
                _homingRemaining = 0f;
                return;
            }

            _homingRemaining -= deltaTime;

            Vector3 velocity = _rb.linearVelocity;
            float speed = velocity.magnitude;
            if (speed <= 1e-6f)
                return;

            // Character Root 通常位于脚底。持续读取实际命中 Collider 的 Bounds Center，
            // 才能在不同身高、不同 Collider 类型的目标之间保持通用且可命中的追踪点。
            Vector3 toTarget =
                ResolveHomingTargetPoint(_homingTargetCollider) -
                transform.position;
            if (toTarget.sqrMagnitude <= 1e-6f)
                return;

            float maxRadians = _homingTurnRateDegrees * Mathf.Deg2Rad * deltaTime;
            Vector3 newDir = Vector3.RotateTowards(velocity.normalized, toTarget.normalized, maxRadians, 0f);
            Vector3 newVelocity = newDir * speed;
            _rb.linearVelocity = newVelocity;
            _launchVelocity = newVelocity;
        }

        private void TryAcquireHomingTarget()
        {
            int hitCount = Physics.OverlapSphereNonAlloc(transform.position, _homingRadius, s_homingHits);
            float bestSqrDistance = float.MaxValue;
            Collider bestTargetCollider = null;
            IDamageable bestDamageable = null;

            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = s_homingHits[i];
                if (hit == null || hit == _collider)
                    continue;

                IDamageable damageable = hit.GetComponentInParent<IDamageable>();
                if (damageable == null || !damageable.IsAlive || damageable.TeamId == _attackerTeam)
                    continue;

                Vector3 targetPoint =
                    ResolveHomingTargetPoint(hit);
                float sqrDistance = (targetPoint - transform.position).sqrMagnitude;
                if (sqrDistance >= bestSqrDistance)
                    continue;

                bestSqrDistance = sqrDistance;
                bestTargetCollider = hit;
                bestDamageable = damageable;
            }

            if (bestTargetCollider == null)
                return;

            _homingTargetCollider = bestTargetCollider;
            _homingTargetDamageable = bestDamageable;
            _homingAcquired = true;
            _homingRemaining = _homingDuration;
        }

        /// <summary>
        /// Homing 的命中目标必须来自真实 Physics 体积，而不是通常位于角色脚底的 Root。
        /// Bounds 是 struct，逐个 FixedUpdate 读取不会产生 GC Alloc，并会随 Collider 移动更新。
        /// </summary>
        protected static Vector3 ResolveHomingTargetPoint(
            Collider targetCollider)
        {
            return targetCollider != null
                ? targetCollider.bounds.center
                : Vector3.zero;
        }

        public ProjectileDebugSnapshot GetDebugSnapshot()
        {
            if (_rb == null && !_debugMissingRigidbodyWarned)
            {
                _debugMissingRigidbodyWarned = true;
                GameLog.Warn(
                    $"Projectile {name} 缺少 Rigidbody，Debug Snapshot 使用零速度",
                    "ProjectileDebug");
            }

            Vector3 velocity = _rb != null ? _rb.linearVelocity : Vector3.zero;
            bool hasTarget = _homingTargetCollider != null;

            return new ProjectileDebugSnapshot(
                transform.position,
                velocity,
                _rb != null && _rb.useGravity,
                _homingEnabled,
                _homingRadius,
                hasTarget,
                hasTarget
                    ? ResolveHomingTargetPoint(
                        _homingTargetCollider)
                    : Vector3.zero,
                _orbitEnabled,
                _orbitCenter,
                _orbitForward,
                _orbitRight,
                _orbitUp,
                _orbitRadius,
                _hasDebugReflection,
                _lastDebugCollisionPoint,
                _lastDebugCollisionNormal,
                _lastDebugReflectedDirection,
                _lastDebugCollisionTime);
        }

        private void RecordDebugReflection(
            Vector3 point, Vector3 normal, Vector3 reflectedDirection)
        {
            _hasDebugReflection = true;
            _lastDebugCollisionPoint = point;
            _lastDebugCollisionNormal = normal;
            _lastDebugReflectedDirection = reflectedDirection;
            _lastDebugCollisionTime = Time.time;
        }

        private void ClearDebugReflectionFacts()
        {
            _hasDebugReflection = false;
            _lastDebugCollisionPoint = Vector3.zero;
            _lastDebugCollisionNormal = Vector3.zero;
            _lastDebugReflectedDirection = Vector3.zero;
            _lastDebugCollisionTime = 0f;
        }

        private Vector3 ResolveCurrentDirection()
        {
            Vector3 v = _rb != null ? _rb.linearVelocity : Vector3.zero;
            if (v.sqrMagnitude > 1e-6f)
                return v.normalized;
            return transform.forward;
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

            if (!s_active.Contains(this))
                s_active.Add(this);
        }

        private void Unregister()
        {
            s_active.Remove(this);
        }

        private void RefreshSameTeamProjectileIgnores()
        {
            if (_collider == null)
                return;

            for (int i = 0; i < s_active.Count; i++)
            {
                ProjectileBase other = s_active[i];
                if (other == null || other == this || other._collider == null)
                    continue;

                if (other._attackerTeam == _attackerTeam)
                    Physics.IgnoreCollision(_collider, other._collider);
            }
        }
    }
}

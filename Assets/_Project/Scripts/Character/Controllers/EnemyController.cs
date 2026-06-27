using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 敌人控制器（总装 + 行动层）。类比 PlayerControllerBase：Awake 取组件、预实例化感知与状态机；
    /// Update 跑 感知→状态机→同步 Animator。对状态暴露"行动能力"(MoveTo/StayGrounded/FaceTarget)。
    /// 受击/扣血/死亡/流血复用 HealthComponent + CharacterCombatFeedback；近战命中复用 MeleeHitDetector。
    /// 决策层(状态)只决定"做什么"，怎么做在本类实现——将来换行为树只换决策层。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(HealthComponent))]
    public class EnemyController : MonoBehaviour
    {
        [Header("数据")]
        [SerializeField] private EnemyDefinition _definition;

        [Header("引用")]
        [Tooltip("近战命中判定；其 TeamId 必须=本敌人 HealthComponent.TeamId(敌方)。攻击窗口由攻击状态开关")]
        [SerializeField] private MeleeHitDetector _hitDetector;

        [Header("调参")]
        [SerializeField] private float _rotationSpeed = 10f;
        [SerializeField] private float _gravity = -20f;

        private CharacterController _cc;
        private Animator _animator;

        private EnemyPerception _perception;
        private EnemyStateMachine _stateMachine;
        private EnemyIdleState _idleState;
        private EnemyChaseState _chaseState;
        private EnemyAttackState _attackState;
        private int _attackStateHash;
        private EnemyHurtState _hurtState;
        private int _hurtStateHash;
        private int _id;
        private bool _dead;

        private float _verticalVelocity;

        private static readonly int SpeedHash = Animator.StringToHash("speed");

        public EnemyDefinition Definition => _definition;
        public EnemyPerception Perception => _perception;
        public EnemyStateMachine StateMachine => _stateMachine;
        public EnemyIdleState IdleState => _idleState;
        public EnemyChaseState ChaseState => _chaseState;
        public EnemyAttackState AttackState => _attackState;
        public int AttackStateHash => _attackStateHash;
        public float AttackCooldownCounter { get; set; }
        public EnemyHurtState HurtState => _hurtState;
        public int HurtStateHash => _hurtStateHash;
        public bool IsDead => _dead;
        public Animator Animator => _animator;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _animator = GetComponentInChildren<Animator>();

            _perception = new EnemyPerception(this);
            _stateMachine = new EnemyStateMachine();
            _idleState = new EnemyIdleState(this);
            _chaseState = new EnemyChaseState(this);
            _attackState = new EnemyAttackState(this);
            // 攻击动画状态名取自攻击数据(数据驱动)；空 → 0 → CrossFade 不切动画
            string atkStateName = (_definition != null && _definition.Attack != null)
                ? _definition.Attack.AnimationStateName : null;
            _attackStateHash = string.IsNullOrEmpty(atkStateName) ? 0 : Animator.StringToHash(atkStateName);
            if (_attackStateHash == 0)
                GameLog.Warn("敌人攻击动画状态名为空，攻击 CrossFade 无法切换动画", "Enemy");
            _hurtState = new EnemyHurtState(this);
            _hurtStateHash = (_definition != null && !string.IsNullOrEmpty(_definition.HurtStateName))
                ? Animator.StringToHash(_definition.HurtStateName) : 0;
            _id = gameObject.GetInstanceID();

            if (_definition == null)
                GameLog.Warn("EnemyController 未配置 EnemyDefinition", "Enemy");
            // 把 SO 的攻击数据注入命中判定器，保证两者一致
            if (_hitDetector != null && _definition != null && _definition.Attack != null)
                _hitDetector.SetAttack(_definition.Attack);
        }

        private void Start()
        {
            _stateMachine.ChangeState(_idleState);
        }

        private void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        private void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        private void OnDamageReceived(DamageReceivedEvent e)
        {
            if (_dead || e.TargetId != _id) return;
            if (e.RemainingHp <= 0f) return; // 致死那一击交给 OnDeath 处理，不进硬直
            if (!e.TriggerHitReaction) return; // DoT/环境跳伤(燃烧/火场)：不进硬直，避免被持续伤害永久锁死
            _stateMachine.ChangeState(_hurtState);
        }

        private void OnDeath(DeathEvent e)
        {
            if (_dead || e.TargetId != _id) return;
            _dead = true;
            if (_hitDetector != null) _hitDetector.CloseHitWindow();
            // 死亡动画 + 延时销毁由 CharacterCombatFeedback 负责；本控制器只停 AI
        }

        private void Update()
        {
            if (_dead) return;
            if (AttackCooldownCounter > 0f) AttackCooldownCounter -= Time.deltaTime;
            _perception.Tick();
            _stateMachine.Update();
            SyncAnimator();
        }

        private void SyncAnimator()
        {
            if (_animator == null) return;
            Vector3 v = _cc.velocity; v.y = 0f;
            _animator.SetFloat(SpeedHash, v.magnitude);
        }

        #region 行动能力（供状态调用）

        /// <summary>朝目标水平移动(含重力)。</summary>
        public void MoveTo(Vector3 targetPos)
        {
            Vector3 dir = targetPos - transform.position; dir.y = 0f;
            Vector3 horizontal = dir.sqrMagnitude > 1e-6f
                ? dir.normalized * _definition.MoveSpeed : Vector3.zero;
            ApplyGravity();
            Vector3 velocity = horizontal;
            velocity.y = _verticalVelocity;
            _cc.Move(velocity * Time.deltaTime);
        }

        /// <summary>原地不动，仅施加重力贴地。</summary>
        public void StayGrounded()
        {
            ApplyGravity();
            _cc.Move(Vector3.up * _verticalVelocity * Time.deltaTime);
        }

        /// <summary>平滑转向目标的水平方向(只 yaw)。</summary>
        public void FaceTarget(Vector3 targetPos)
        {
            Vector3 dir = targetPos - transform.position; dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) return;
            Quaternion rot = Quaternion.LookRotation(dir);
            transform.rotation = Quaternion.Slerp(transform.rotation, rot, _rotationSpeed * Time.deltaTime);
        }

        /// <summary>开启近战命中窗口（攻击状态在 HitActiveStart 调用）。</summary>
        public void OpenAttackWindow() { if (_hitDetector != null) _hitDetector.OpenHitWindow(); }

        /// <summary>关闭近战命中窗口。</summary>
        public void CloseAttackWindow() { if (_hitDetector != null) _hitDetector.CloseHitWindow(); }

        /// <summary>数据驱动 CrossFade 进入指定 Animator 状态（hash 为 0 静默跳过）。</summary>
        public void CrossFade(int stateHash)
        {
            if (_animator != null && stateHash != 0)
                _animator.CrossFadeInFixedTime(stateHash, _definition.CrossFadeDuration, 0);
        }

        private void ApplyGravity()
        {
            if (_cc.isGrounded && _verticalVelocity < 0f) _verticalVelocity = -2f;
            else _verticalVelocity += _gravity * Time.deltaTime;
        }

        #endregion
    }
}

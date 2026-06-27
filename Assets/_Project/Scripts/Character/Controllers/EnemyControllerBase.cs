using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 敌人控制器基类（总装 + 行动层）。对标 PlayerControllerBase：持有共享组件/感知/状态机/共享态(Idle+Hurt)，
    /// Update 跑 感知→状态机→同步 Animator，对状态暴露行动能力。具体"战斗走位 + 攻击"由子类经 EngageState 接缝提供：
    /// MeleeEnemyController(贴身近战) / RangedEnemyController(保距离施法)。决策层(状态)只决定做什么，怎么做在本类/子类。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(HealthComponent))]
    public abstract class EnemyControllerBase : MonoBehaviour
    {
        [Header("数据")]
        [SerializeField] protected EnemyDefinition _definition;

        [Header("调参")]
        [SerializeField] private float _rotationSpeed = 10f;
        [SerializeField] private float _gravity = -20f;

        protected CharacterController _cc;
        protected Animator _animator;

        private EnemyPerception _perception;
        private EnemyStateMachine _stateMachine;
        private EnemyIdleState _idleState;
        private EnemyHurtState _hurtState;
        private int _attackStateHash;
        private int _hurtStateHash;
        private int _id;
        private bool _dead;

        private float _verticalVelocity;

        private static readonly int SpeedHash = Animator.StringToHash("speed");

        public EnemyDefinition Definition => _definition;
        public EnemyPerception Perception => _perception;
        public EnemyStateMachine StateMachine => _stateMachine;
        public EnemyIdleState IdleState => _idleState;
        public EnemyHurtState HurtState => _hurtState;
        public int AttackStateHash => _attackStateHash;
        public int HurtStateHash => _hurtStateHash;
        public float AttackCooldownCounter { get; set; }
        public bool IsDead => _dead;
        public Animator Animator => _animator;

        /// <summary>进入战斗后使用的走位状态（多态接缝）：近战=ChaseState，远程=KiteState。</summary>
        public abstract EnemyStateBase EngageState { get; }

        protected virtual void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _animator = GetComponentInChildren<Animator>();

            _perception = new EnemyPerception(this);
            _stateMachine = new EnemyStateMachine();
            _idleState = new EnemyIdleState(this);
            _hurtState = new EnemyHurtState(this);

            // 攻击/施法动画状态名取自攻击数据(数据驱动)；空 → 0 → CrossFade 不切动画
            string atkStateName = (_definition != null && _definition.Attack != null)
                ? _definition.Attack.AnimationStateName : null;
            _attackStateHash = string.IsNullOrEmpty(atkStateName) ? 0 : Animator.StringToHash(atkStateName);
            if (_attackStateHash == 0)
                GameLog.Warn("敌人攻击动画状态名为空，攻击 CrossFade 无法切换动画", "Enemy");

            _hurtStateHash = (_definition != null && !string.IsNullOrEmpty(_definition.HurtStateName))
                ? Animator.StringToHash(_definition.HurtStateName) : 0;
            _id = gameObject.GetInstanceID();

            if (_definition == null)
                GameLog.Warn("EnemyControllerBase 未配置 EnemyDefinition", "Enemy");
        }

        private void Start()
        {
            _stateMachine.ChangeState(_idleState);
        }

        protected virtual void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        protected virtual void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        private void OnDamageReceived(DamageReceivedEvent e)
        {
            if (_dead || e.TargetId != _id) return;
            if (e.RemainingHp <= 0f) return;        // 致死那一击交给 OnDeath，不进硬直
            if (!e.TriggerHitReaction) return;       // DoT/环境跳伤：不进硬直，避免被持续伤害锁死
            _stateMachine.ChangeState(_hurtState);
        }

        private void OnDeath(DeathEvent e)
        {
            if (_dead || e.TargetId != _id) return;
            _dead = true;
            OnDied(); // 子类收尾(如关近战命中窗口)；死亡动画+销毁由 CharacterCombatFeedback 负责
        }

        /// <summary>死亡时子类收尾钩子（默认空）。</summary>
        protected virtual void OnDied() { }

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
        public void MoveTo(Vector3 targetPos) => MoveHorizontal(targetPos - transform.position);

        /// <summary>远离目标水平移动(后撤，含重力)。</summary>
        public void MoveAway(Vector3 targetPos) => MoveHorizontal(transform.position - targetPos);

        private void MoveHorizontal(Vector3 dir)
        {
            dir.y = 0f;
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

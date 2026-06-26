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

        private float _verticalVelocity;

        private static readonly int SpeedHash = Animator.StringToHash("speed");

        public EnemyDefinition Definition => _definition;
        public EnemyPerception Perception => _perception;
        public EnemyStateMachine StateMachine => _stateMachine;
        public Animator Animator => _animator;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _animator = GetComponentInChildren<Animator>();

            _perception = new EnemyPerception(this);
            _stateMachine = new EnemyStateMachine();

            if (_definition == null)
                GameLog.Warn("EnemyController 未配置 EnemyDefinition", "Enemy");
            // 把 SO 的攻击数据注入命中判定器，保证两者一致
            if (_hitDetector != null && _definition != null && _definition.Attack != null)
                _hitDetector.SetAttack(_definition.Attack);
        }

        private void Update()
        {
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

        private void ApplyGravity()
        {
            if (_cc.isGrounded && _verticalVelocity < 0f) _verticalVelocity = -2f;
            else _verticalVelocity += _gravity * Time.deltaTime;
        }

        #endregion
    }
}

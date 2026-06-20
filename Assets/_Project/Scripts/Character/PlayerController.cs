using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(GroundChecker))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Movement")] [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _rotationSpeed = 10f;

        [Header("Jump")] [SerializeField] private float _gravityMultiplier = 2f; // 跳跃上升阶段的重力加速度倍数
        [SerializeField] private float _jumpForce = 6f; // 跳跃初速度
        [SerializeField] private float _fallGravityMultiplier = 3.5f; // 跳跃下降阶段重力加速度倍数
        [SerializeField] private float _coyoteTime = 0.15f; // 离地后允许跳跃的宽限期（Coyote Time）
        [SerializeField] private float _jumpBufferTime = 0.15f; // 空中按下跳跃后，在地面的缓冲期（Jump Buffer）

        [Header("Slope")] [SerializeField] private float _slideSpeed = 6f; // 滑落速度

        [Header("Interaction")] [SerializeField]
        private float _pushForce = 3f;

        [Header("Camera")] [SerializeField] private Transform _cameraRoot; // 相机枢轴，Inspector 里拖 CameraRoot
        [SerializeField] private float _lookSensitivity = 0.12f; // 灵敏度：度/像素
        [SerializeField] private float _pitchMin = -30f; // 最低俯角（抬头看天的限制）
        [SerializeField] private float _pitchMax = 70f; // 最高俯角（低头看地的限制）

        [Header("Combat")] [SerializeField] private MeleeHitDetector _meleeHitDetector; // 在Inspector里拖入武器上的组件
        [SerializeField] private float _attackBufferTime = 0.15f; // 攻击缓冲时间
        [SerializeField] private ComboDefinition _combo; // 当前武器的连段表（拖入 SingleTwoHandSword）

        [Header("Dash")] [SerializeField] private float _dashSpeed = 14f;      // 冲刺水平速度
        [SerializeField] private float _dashDuration = 0.2f;                    // 冲刺持续时间（秒）
        [SerializeField] private float _dashCooldown = 1f;                      // 冲刺冷却（从 Exit 起算）
        [SerializeField] private float _dashBufferTime = 0.15f;                 // 冲刺输入缓冲（与攻击/跳跃同惯例）

        // 组件引用
        private CharacterController _characterController;
        private Animator _animator;
        private InputSystem_Actions _inputActions;
        private GroundChecker _groundChecker;
        private Camera _mainCamera;

        // ── 状态机（普通 C# 类，由本类持有并驱动）────────────────────────
        // 放在 Awake 里而不是这儿声明时直接 = new，是为了保证初始化顺序可控——Awake 是 Unity 生命周期的第一步，所有初始化集中在这里
        private PlayerStateMachine _stateMachine;

        // 所有可能的状态实例，在 Awake 创建好，避免运行时 new 产生 GC
        // State 通过 _player.GroundedState / AirborneState 拿到这些引用来切换
        private PlayerGroundedState _groundedState;
        private PlayerAirborneState _airborneState;
        private PlayerSlidingState _slidingState;
        private PlayerAttackState _attackState;
        private PlayerDashState _dashState;

        // 运行时数据
        private Vector2 _moveInput;
        private Vector3 _moveDirection;
        private Vector2 _lookInput;
        private float _cameraYaw; // 累积的水平角
        private float _cameraPitch; // 累积的俯仰角

        // 连段各段 Animator 状态名的预 hash 结果（Awake 算一次，避免每次切段 StringToHash）
        private int[] _comboStateHashes;

        // ── Animator Parameter Hashes ──────────────────────────────────────
        // 集中定义在 Controller 里，原因：
        // speed 和 isGrounded 是"持续状态型"参数，由 Controller 每帧统一同步
        // JumpHash 是"事件型"参数（Trigger），仍由 State 在起跳时触发，保留在 PlayerStateBase
        private static readonly int SpeedHash = Animator.StringToHash("speed");
        private static readonly int IsGroundedHash = Animator.StringToHash("isGrounded");

        // ── 对外暴露给 State 的属性 ──────────────────────────────────────
        public CharacterController CharacterController => _characterController;
        public GroundChecker GroundChecker => _groundChecker;
        public Animator Animator => _animator;
        public Vector2 MoveInput => _moveInput;
        public Vector3 MoveDirection => _moveDirection;
        public float MoveSpeed => _moveSpeed;
        public float RotationSpeed => _rotationSpeed;
        public float VerticalVelocity { get; set; }
        public float JumpForce => _jumpForce;
        public float GravityMultiplier => _gravityMultiplier;
        public float FallGravityMultiplier => _fallGravityMultiplier;
        public float CoyoteTime => _coyoteTime;
        public float JumpBufferTime => _jumpBufferTime;
        public float SlideSpeed => _slideSpeed;
        public float CoyoteTimeCounter { get; set; }
        public float JumpBufferCounter { get; set; }

        public PlayerStateMachine StateMachine => _stateMachine;
        public PlayerGroundedState GroundedState => _groundedState;
        public PlayerAirborneState AirborneState => _airborneState;
        public PlayerSlidingState SlidingState => _slidingState;
        public PlayerAttackState AttackState => _attackState;
        public PlayerDashState DashState => _dashState;
        public float AttackBufferCounter { get; set; }
        public float AttackBufferTime => _attackBufferTime;
        public MeleeHitDetector MeleeHitDetector => _meleeHitDetector;
        public ComboDefinition Combo => _combo;

        public float DashSpeed => _dashSpeed;
        public float DashDuration => _dashDuration;
        public float DashCooldown => _dashCooldown;
        public float DashBufferTime => _dashBufferTime;
        public float DashCooldownCounter { get; set; }
        public float DashBufferCounter { get; set; }

        #region Unity事件函数

        private void Awake()
        {
            // 获取并缓存所有组件引用
            _characterController = GetComponent<CharacterController>();
            _animator =
                GetComponentInChildren<Animator>(); // Animator 挂在子节点 BasicFemale01 上，不在 Player GO 上，所以不能用 GetComponent，要用 GetComponentInChildren 往子树里搜索。
            _groundChecker = GetComponent<GroundChecker>();
            _inputActions = new InputSystem_Actions();
            _mainCamera = Camera.main;

            // 创建状态机和所有状态实例
            // 所有 State 在这里 new 好，运行时切换状态只是改引用，不产生 GC
            _stateMachine = new PlayerStateMachine();
            _groundedState = new PlayerGroundedState(this);
            _airborneState = new PlayerAirborneState(this);
            _slidingState = new PlayerSlidingState(this);
            _attackState = new PlayerAttackState(this);
            _dashState = new PlayerDashState(this);

            // 把连段表各段的 AnimationStateName 预 hash 成 int[]
            BuildComboStateHashes();
        }

        private void Start()
        {
            // 在 Start 而非 Awake 进入初始状态
            // 原因：ChangeState 会调用 Enter()，Enter 里可能读取其他组件的数据
            // Start 保证场景内所有 GameObject 的 Awake 都执行完毕，更安全
            _stateMachine.ChangeState(_groundedState);
            Cursor.lockState = CursorLockMode.Locked; // 锁定并隐藏鼠标（编辑器按 Esc 临时解锁）
        }

        private void OnEnable()
        {
            _inputActions.Player.Enable();
            _inputActions.Player.Jump.performed += OnJumpPerformed; // 注册 Jump 回调
            _inputActions.Player.Attack.performed += OnAttackPerformed;
            _inputActions.Player.Dash.performed += OnDashPerformed;
        }

        private void OnDisable()
        {
            _inputActions.Player.Jump.performed -= OnJumpPerformed;
            _inputActions.Player.Attack.performed -= OnAttackPerformed;
            _inputActions.Player.Dash.performed -= OnDashPerformed;
            _inputActions.Player.Disable();
        }

        /*FixedUpdate 是给 Rigidbody 物理模拟用的固定步长循环。
         CharacterController 不走物理管线，在 Update 里调用 Move() 才能保证每帧都响应输入，
         放在 FixedUpdate 反而会导致输入延迟。*/
        private void Update()
        {
            // PlayerController.Update 精简为三件事：
            // 1. 读输入
            // 2. 算移动方向（所有 State 都需要，统一在这里算一次）
            // 3. 驱动状态机（状态机再驱动当前 State）
            _moveInput = _inputActions.Player.Move.ReadValue<Vector2>();
            _lookInput = _inputActions.Player.Look.ReadValue<Vector2>();
            _moveDirection = CalculateMoveDirection();

            // 两个计时器统一在 Controller 里递减，放在这里而不是 State 里，原因：
            // 计时器是全局数据，无论当前是哪个 State 都应该持续倒计时
            // State 只负责读取计时器的值来做判断，不负责维护它
            if (JumpBufferCounter > 0f) JumpBufferCounter -= Time.deltaTime;
            if (CoyoteTimeCounter > 0f) CoyoteTimeCounter -= Time.deltaTime;
            if (AttackBufferCounter > 0f) AttackBufferCounter -= Time.deltaTime;
            if (DashCooldownCounter > 0f) DashCooldownCounter -= Time.deltaTime;
            if (DashBufferCounter > 0f) DashBufferCounter -= Time.deltaTime;

            // ① 先驱动状态机：状态机可能在本帧改变 VerticalVelocity（如起跳设为 JumpForce）
            _stateMachine.Update();

            // ② 后同步 Animator：拿到本帧状态机执行完后的最终数据再喂给 Animator
            // 顺序不能反——如果先同步，起跳帧 VerticalVelocity 还是旧值(-2f)，
            // animIsGrounded 会错误地为 true，导致 JumpStart→JumpEnd 在起跳瞬间误触发
            SyncAnimatorParameters();
        }

        private void LateUpdate()
        {
            /* 为什么在 LateUpdate 旋转枢轴：
               保证发生在所有 Update（角色移动）之后、CinemachineBrain 取位之前，
               相机用的是本帧最终的枢轴状态，避免一帧延迟的抖动。*/
            HandleCameraRotation();
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            Rigidbody rb = hit.rigidbody;

            // 没有 Rigidbody → 静态物体（墙、地面），不推
            if (rb == null) return;
            // isKinematic → 对方也是 Kinematic（比如另一个角色），不推
            if (rb.isKinematic) return;
            // 只推侧面的物件，不推脚底下的物件
            // hit.moveDirection.y < -0.3 表示角色是向下运动时碰到的（踩在物件上）
            // 这种情况推力会往下，导致物件被压进地面，不符合预期
            if (hit.moveDirection.y < -0.3f) return;

            // 推力方向：角色水平移动方向（去掉 y，只水平推）
            // 不用 hit.normal 的原因：法线方向会把物件往斜方向推，不直觉
            Vector3 pushDir = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);

            // AddForce ForceMode.VelocityChange：直接改速度（不受质量影响）
            // 比 Impulse 更可控——不会因为箱子很轻就飞得很远
            // 用 linearVelocity 赋值也可以，但 AddForce 更不容易叠加过头
            rb.AddForce(pushDir * _pushForce, ForceMode.VelocityChange);
        }

        #endregion

        /// <summary>取第 index 段的 Animator 状态 hash；越界或未配置返回 0。</summary>
        public int GetComboStateHash(int index)
        {
            if (_comboStateHashes == null || index < 0 || index >= _comboStateHashes.Length)
                return 0;
            return _comboStateHashes[index];
        }

        /// <summary>
        /// 把连段表各段的 AnimationStateName 预 hash 成 int[]，运行期切段直接用 hash CrossFade，
        /// 不做每帧/每次切段的 StringToHash。状态名为空时记 0 并告警（CrossFade 0 不会切动画）。
        /// </summary>
        private void BuildComboStateHashes()
        {
            int count = _combo != null ? _combo.SegmentCount : 0;
            _comboStateHashes = new int[count];
            for (int i = 0; i < count; i++)
            {
                AttackDefinition seg = _combo.Segments[i];
                string stateName = seg != null ? seg.AnimationStateName : null;
                if (string.IsNullOrEmpty(stateName))
                {
                    _comboStateHashes[i] = 0;
                    GameLog.Warn($"连段第 {i} 段 AnimationStateName 为空，CrossFade 将无法切换动画", "Combat");
                }
                else
                {
                    _comboStateHashes[i] = Animator.StringToHash(stateName);
                }
            }
        }


        /// <summary>
        /// 每帧将"持续状态型"Animator 参数同步为当前真实数据。
        ///
        /// 持续状态型 vs 事件型 的区分：
        ///   持续状态型（speed / isGrounded）：每帧反映当前真相，任何时候值都应该是对的
        ///     → 在这里每帧统一同步，不在各 State 零散 Set
        ///   事件型（jump Trigger）：某个瞬间发生一次，消耗后自动清除
        ///     → 在 ExecuteJump() 里触发，不在这里处理
        ///
        /// 如果用"事件式"维护持续状态（只在特定转移点 Set），就必须穷举所有改变路径，
        /// 漏掉任何一条（如走下悬崖离地）就出 bug，而且路径越多越容易漏。
        /// </summary>
        private void SyncAnimatorParameters()
        {
            // speed：直接用输入强度，Clamp01 防止对角线输入(magnitude≈1.41)让 Blend Tree 越界
            _animator.SetFloat(SpeedHash, Mathf.Clamp01(_moveInput.magnitude));

            // isGrounded：两个条件都要满足
            // 条件① GroundChecker.IsGrounded —— SphereCast 检测到地面
            // 条件② VerticalVelocity <= 0f    —— 非上升阶段
            bool animIsGrounded = _groundChecker.IsGrounded && VerticalVelocity <= 0f;
            _animator.SetBool(IsGroundedHash, animIsGrounded);
        }

        private Vector3 CalculateMoveDirection()
        {
            if (_moveInput.sqrMagnitude < 0.01f) return Vector3.zero;

            // 取相机水平方向，清除俯仰角影响
            Vector3 cameraForward = _mainCamera.transform.forward;
            Vector3 cameraRight = _mainCamera.transform.right;
            cameraForward.y = 0f;
            cameraRight.y = 0f;
            cameraForward.Normalize();
            cameraRight.Normalize();

            // 输入向量投影到相机水平坐标系
            return (cameraForward * _moveInput.y + cameraRight * _moveInput.x).normalized;
        }

        private void HandleCameraRotation()
        {
            // 鼠标 delta 不乘 deltaTime：delta 本身是帧间增量，天然帧率无关
            // （手柄摇杆是速率信号才需要乘 deltaTime，未来支持手柄时再区分）
            _cameraYaw += _lookInput.x * _lookSensitivity;
            _cameraPitch -= _lookInput.y * _lookSensitivity;
            // 符号说明：鼠标上移 lookInput.y 为正，抬头 = Unity 里 X 轴负角度，所以用减

            // Clamp 俯仰：防止翻过头顶（视角颠倒）或钻进地面
            _cameraPitch = Mathf.Clamp(_cameraPitch, _pitchMin, _pitchMax);

            // 直接写世界旋转（rotation 而非 localRotation）：
            // 父节点 Player 的转身不会影响枢轴朝向 → 旋转彻底解耦
            _cameraRoot.rotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
        }

        private void OnJumpPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            // 按下跳跃键的那一帧触发一次
            // 不在这里执行跳跃，只记录"有一个待消耗的跳跃输入"
            // 由 State 在合适的时机去消耗（接地时：立刻跳；Coyote 期间：也能跳）
            JumpBufferCounter = _jumpBufferTime;
        }

        private void OnAttackPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            // 不在这里执行攻击，只记录"有一个待消耗的攻击输入"
            // 由 GroundedState 在合适时机消耗（和 JumpBuffer 同理）
            AttackBufferCounter = _attackBufferTime;
        }

        private void OnDashPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            // 与 Jump/Attack 同惯例：只记录"有一个待消耗的冲刺输入"，由 GroundedState 在闸门处消费。
            // 冷却与缓冲正交：本回调只管缓冲；能力锁由 GroundedState 用 DashCooldownCounter 单独把关。
            DashBufferCounter = _dashBufferTime;
        }
    }
}

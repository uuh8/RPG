using UnityEngine;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 角色控制器基类（抽象）。承载 Warrior / Archer 共享的能力：
    /// 移动 / 跳跃 / 冲刺 / 相机 / 输入计时器 / 状态机基础设施 / 接地三态 + Dash 态。
    /// 不含任何角色专属攻击逻辑——那在 WarriorController / ArcherController 子类。
    /// 共享逻辑只通过 Animator 参数名 / 状态节点名跟各自的 Animator Controller 对话，不绑定具体动画资源。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(GroundChecker))]
    public abstract class PlayerControllerBase : MonoBehaviour
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
        [SerializeField] private float _pitchMin = -30f; // 最低俯角
        [SerializeField] private float _pitchMax = 70f; // 最高俯角

        [Header("Attack Input")] [SerializeField]
        private float _attackBufferTime = 0.15f; // 攻击缓冲时间（通用：按了攻击键的缓冲）

        [Header("Dash")] [SerializeField] private float _dashSpeed = 20f; // 冲刺水平速度
        [SerializeField] private float _dashDuration = 0.2f; // 冲刺位移持续时间（秒）
        [SerializeField] private float _dashCooldown = 1f; // 冲刺冷却（从 Exit 起算）
        [SerializeField] private float _dashBufferTime = 0.15f; // 冲刺输入缓冲（与攻击/跳跃同惯例）

        [SerializeField] private float _dashMoveDelay = 0.1f; // 冲刺位移启动延迟（秒）：等翻滚动画起势后再位移，避免"先闪后翻"。设 0 = 进入即位移

        [SerializeField] private string
            _dashStateName = "DashForward_SingleTwohandSword"; // Dash 目标 Animator 状态名（数据驱动，各角色填自己 Controller 的节点名）

        // 组件引用
        private CharacterController _characterController;
        private Animator _animator;
        private InputSystem_Actions _inputActions;
        private GroundChecker _groundChecker;
        private Camera _mainCamera;

        // 状态机与共享状态（Awake 创建一次，运行时切换只改引用，不产生 GC）
        private PlayerStateMachine _stateMachine;
        private PlayerGroundedState _groundedState;
        private PlayerAirborneState _airborneState;
        private PlayerSlidingState _slidingState;
        private PlayerDashState _dashState;

        // 运行时数据
        private Vector2 _moveInput;
        private Vector3 _moveDirection;
        private Vector2 _lookInput;
        private float _cameraYaw;
        private float _cameraPitch;

        // 持续状态型 Animator 参数 hash（Controller 每帧统一同步）
        private static readonly int SpeedHash = Animator.StringToHash("speed");
        private static readonly int IsGroundedHash = Animator.StringToHash("isGrounded");

        // Dash 目标状态名预 hash（Awake 算一次，绝不每帧/每次触发 StringToHash）
        private int _dashStateHash;

        // ── 对外暴露给 State 的属性 ──
        public CharacterController CharacterController => _characterController;
        public GroundChecker GroundChecker => _groundChecker;
        public Animator Animator => _animator;
        public Camera MainCamera => _mainCamera; // 暴露给瞄准（屏幕中心射线 + 转向相机朝向）
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
        public PlayerDashState DashState => _dashState;

        public float AttackBufferCounter { get; set; }
        public float AttackBufferTime => _attackBufferTime;
        public float AttackCooldownCounter { get; set; } // 攻击冷却剩余(秒)：>0 表示射速锁定中，时长来自连段数据 AttackCooldown

        public float DashSpeed => _dashSpeed;
        public float DashDuration => _dashDuration;
        public float DashCooldown => _dashCooldown;
        public float DashBufferTime => _dashBufferTime;
        public float DashMoveDelay => _dashMoveDelay;
        public float DashCooldownCounter { get; set; }
        public float DashBufferCounter { get; set; }
        public int DashStateHash => _dashStateHash;

        public bool IsAttackHeld => _inputActions.Player.Attack.IsPressed(); // 攻击键当前是否按住（蓄力轮询用）
        public bool AttackPressedThisFrame => _inputActions.Player.Attack.WasPressedThisFrame(); // 攻击键本帧上升沿（tap/hold 边沿门控用）

        /// <summary>
        /// 攻击触发 seam：共享的 GroundedState 在攻击优先级位调用本钩子。
        /// 基类默认不攻击（返回 false）；具体角色在子类重写：消耗攻击输入并切到自己的攻击状态，
        /// </summary>
        public virtual bool TryStartAttack() => false;  // 基类：安全的空实现

        #region Unity 事件函数

        protected virtual void Awake()
        {
            _characterController = GetComponent<CharacterController>();
            _animator = GetComponentInChildren<Animator>();
            _groundChecker = GetComponent<GroundChecker>();
            _inputActions = new InputSystem_Actions();
            _mainCamera = Camera.main;

            // 初始化状态机（这是共有的状态，私有的状态在各自的Controller中单独初始化）
            _stateMachine = new PlayerStateMachine();
            _groundedState = new PlayerGroundedState(this);
            _airborneState = new PlayerAirborneState(this);
            _slidingState = new PlayerSlidingState(this);
            _dashState = new PlayerDashState(this);

            // Dash 目标状态名预 hash（数据驱动；空串记 0 并告警，CrossFade 0 不切动画——与连段空状态名同款防御）
            if (string.IsNullOrEmpty(_dashStateName))
            {
                _dashStateHash = 0;
                GameLog.Warn("_dashStateName 为空，冲刺 CrossFade 将无法切换动画", "Character");
            }
            else
            {
                _dashStateHash = Animator.StringToHash(_dashStateName);
            }
        }

        private void Start()
        {
            // Start 而非 Awake 进入初始状态：保证所有 GameObject 的 Awake 已执行完
            _stateMachine.ChangeState(_groundedState);
            Cursor.lockState = CursorLockMode.Locked; // 锁定并隐藏鼠标
        }

        private void OnEnable()
        {
            _inputActions.Player.Enable();
            _inputActions.Player.Jump.performed += OnJumpPerformed;
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

        // CharacterController 不走物理管线，在 Update 里 Move 才能每帧响应输入
        private void Update()
        {
            _moveInput = _inputActions.Player.Move.ReadValue<Vector2>();
            _lookInput = _inputActions.Player.Look.ReadValue<Vector2>();
            _moveDirection = CalculateMoveDirection();

            // 计时器统一在 Controller 递减（全局数据，与当前 State 无关）
            if (JumpBufferCounter > 0f) JumpBufferCounter -= Time.deltaTime;
            if (CoyoteTimeCounter > 0f) CoyoteTimeCounter -= Time.deltaTime;
            if (AttackBufferCounter > 0f) AttackBufferCounter -= Time.deltaTime;
            if (AttackCooldownCounter > 0f) AttackCooldownCounter -= Time.deltaTime;
            if (DashCooldownCounter > 0f) DashCooldownCounter -= Time.deltaTime;
            if (DashBufferCounter > 0f) DashBufferCounter -= Time.deltaTime;

            _stateMachine.Update();     // ① 先驱动状态机（可能改 VerticalVelocity）

            SyncAnimatorParameters();   // ② 后同步 Animator（拿最终数据）
        }

        private void LateUpdate()
        {
            // 在 LateUpdate 旋转相机枢轴：发生在所有 Update（角色移动）之后
            HandleCameraRotation();
        }

        /// <summary>
        /// 当 CharacterController（角色控制器）在移动过程中与其他带有碰撞体的物体发生碰撞时，引擎会自动调用此函数
        /// 并传入 ControllerColliderHit 类型的参数 hit，其中包含了碰撞的详细信息。
        /// </summary>
        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            Rigidbody rb = hit.rigidbody;
            if (rb == null) return; // 静态物体不推
            if (rb.isKinematic) return; // Kinematic 不推
            if (hit.moveDirection.y < -0.3f) return; // 踩在物件上不推
            Vector3 pushDir = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
            rb.AddForce(pushDir * _pushForce, ForceMode.VelocityChange);    // ForceMode.VelocityChange 表示直接改变物体的速度，忽略其质量（即无论物体轻重，都能被推开相同的初始速度）。
        }

        #endregion


        #region 事件回调

        private void OnJumpPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            JumpBufferCounter = _jumpBufferTime;
        }

        private void OnAttackPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            AttackBufferCounter = _attackBufferTime;
        }

        private void OnDashPerformed(UnityEngine.InputSystem.InputAction.CallbackContext ctx)
        {
            DashBufferCounter = _dashBufferTime;
        }

        #endregion

        private void SyncAnimatorParameters()
        {
            // 设置速度参数，确保值在0-1之间
            _animator.SetFloat(SpeedHash, Mathf.Clamp01(_moveInput.magnitude));
            // 检查角色是否处于地面状态且垂直速度小于等于0
            bool animIsGrounded = _groundChecker.IsGrounded && VerticalVelocity <= 0f;
            // 更新地面状态参数
            _animator.SetBool(IsGroundedHash, animIsGrounded);
        }

        private Vector3 CalculateMoveDirection()
        {
            if (_moveInput.sqrMagnitude < 0.01f) return Vector3.zero;

            Vector3 cameraForward = _mainCamera.transform.forward;
            Vector3 cameraRight = _mainCamera.transform.right;
            cameraForward.y = 0f;
            cameraRight.y = 0f;
            cameraForward.Normalize();
            cameraRight.Normalize();

            return (cameraForward * _moveInput.y + cameraRight * _moveInput.x).normalized;
        }

        private void HandleCameraRotation()
        {
            _cameraYaw += _lookInput.x * _lookSensitivity;
            _cameraPitch -= _lookInput.y * _lookSensitivity;
            _cameraPitch = Mathf.Clamp(_cameraPitch, _pitchMin, _pitchMax);
            _cameraRoot.rotation = Quaternion.Euler(_cameraPitch, _cameraYaw, 0f);
        }

    }
}

using UnityEngine;

namespace Game.Character
{
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _rotationSpeed = 10f;

        [Header("Gravity")]
        [SerializeField] private float _gravityMultiplier = 2f;

        private Camera _mainCamera;
        private Vector3 _moveDirection;

        // 组件引用
        private CharacterController _characterController;
        private Animator _animator;
        private InputSystem_Actions _inputActions;

        // 运行时状态
        private Vector2 _moveInput;
        private float _verticalVelocity;

        // Animator 参数 Hash，避免每帧字符串查找产生 GC Alloc
        private static readonly int SpeedHash = Animator.StringToHash("speed");

        private void Awake()
        {
            _characterController = GetComponent<CharacterController>();
            _animator = GetComponentInChildren<Animator>(); // Animator 挂在子节点 BasicFemale01 上，不在 Player GO 上，所以不能用 GetComponent，要用 GetComponentInChildren 往子树里搜索。
            _inputActions = new InputSystem_Actions();
            _mainCamera = Camera.main;
        }

        private void OnEnable()
        {
            _inputActions.Player.Enable();
        }

        private void OnDisable()
        {
            _inputActions.Player.Disable();
        }

        /*FixedUpdate 是给 Rigidbody 物理模拟用的固定步长循环。
         CharacterController 不走物理管线，在 Update 里调用 Move() 才能保证每帧都响应输入，
         放在 FixedUpdate 反而会导致输入延迟。*/
        private void Update()
        {
            _moveInput = _inputActions.Player.Move.ReadValue<Vector2>();
            _moveDirection = CalculateMoveDirection();

            HandleGravity();
            HandleMovement();
            HandleRotation();
            HandleAnimation();
        }

        private void HandleGravity()
        {
            if (_characterController.isGrounded && _verticalVelocity < 0f)
            {
                // 接地时设 -2f 而非 0：防止 isGrounded 在 true/false 间闪烁
                // （设0则下一帧无下压力，可能判离地，重力又累加，反复抖动）
                _verticalVelocity = -2f;
            }
            else
            {
                // 速度积分：v(t+1) = v(t) + a * dt，这是半隐式欧拉积分的速度更新步：v(t+1) = v(t) + a·dt。
                // 然后把 _verticalVelocity 打包进 Move() 的 Y 分量，由 Move 统一做碰撞检测（防止穿地）。
                _verticalVelocity += Physics.gravity.y * _gravityMultiplier * Time.deltaTime;
            }
        }

        private void HandleMovement()
        {
            // 水平速度来自输入，垂直速度来自重力积分
            Vector3 velocity = _moveDirection * _moveSpeed;
            velocity.y = _verticalVelocity;

            // 位移 = 速度 * 时间，Move() 内部会做碰撞检测
            _characterController.Move(velocity * Time.deltaTime);
        }

        private void HandleRotation()
        {
            // sqrMagnitude 避免 sqrt 计算，仅用于判断是否有输入
            if (_moveInput.y < 0 && Mathf.Abs(_moveInput.x) < 0.1f) return;
            if (_moveInput.sqrMagnitude < 0.01f) return;

            Quaternion targetRotation = Quaternion.LookRotation(_moveDirection);

            // Slerp 球面线性插值，保证转向速度与帧率无关
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRotation,
                _rotationSpeed * Time.deltaTime
            );
        }

        private void HandleAnimation()
        {
            _animator.SetFloat(SpeedHash, _moveInput.magnitude);
        }

        private Vector3 CalculateMoveDirection()
        {
            if(_moveInput.sqrMagnitude < 0.01f) return Vector3.zero;

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
    }
}

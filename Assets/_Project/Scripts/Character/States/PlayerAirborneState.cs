using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 空中状态。负责重力累加和空中移动。
    /// 每帧检测 GroundChecker.IsGrounded，落地时切换回 GroundedState。
    /// M2-2 跳跃逻辑将在此状态基础上扩展。
    /// </summary>
    public class PlayerAirborneState : PlayerStateBase
    {
        public PlayerAirborneState(PlayerControllerBase player) : base(player) { }

        public override void Enter() { }
        public override void Update()
        {
            // Coyote Time 期间检测跳跃输入
            // 条件：JumpBufferCounter > 0（有待消耗的跳跃输入）
            //       CoyoteTimeCounter > 0（仍在宽限期内）
            // 两个条件都满足才能 Coyote 跳，防止随便在空中乱跳
            if (_player.JumpBufferCounter > 0f && _player.CoyoteTimeCounter > 0f)
            {
                ExecuteCoyoteJump();
                return;
            }

            // 空中攻击（角色子类决定是否支持，如法师空中火球普攻）。放在落地检测之前，
            // Coyote 跳之后：保留跳跃宽限优先级，攻击则替代本帧的重力/移动/落地处理。
            if (_player.TryStartAirAttack())
                return;

            HandleGravity();
            HandleMovement();
            base.HandleRotation();
            CheckTransition();
        }
        public override void Exit() { }

        private void HandleGravity()
        {
            // 分段重力：上升和下落用不同的重力倍率
            // VerticalVelocity < 0 = 下落阶段，用更大的 FallGravityMultiplier
            // VerticalVelocity >= 0 = 上升阶段，用普通 GravityMultiplier
            // 效果：上升慢悠悠，下落干净利落，跳跃手感更"脆"
            float multiplier = _player.VerticalVelocity < 0f
                ? _player.FallGravityMultiplier
                : _player.GravityMultiplier;

            // 半隐式欧拉积分：v(t+1) = v(t) + a * dt
            // 先更新速度，再用新速度更新位置（在 HandleMovement 里）
            // _gravityMultiplier 让下落比物理重力更快，手感更"脆"
            _player.VerticalVelocity += Physics.gravity.y
                                        * multiplier
                                        * Time.deltaTime;
        }

        private void HandleMovement()
        {
            // 空中依然保留水平移动控制（常见设计，让玩家在空中能微调方向）
            // 垂直方向由重力积分的 VerticalVelocity 控制
            Vector3 velocity = _player.MoveDirection * _player.MoveSpeed;
            velocity.y = _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        private void CheckTransition()
        {
            // 上升阶段（VerticalVelocity > 0）不检测落地
            // 否则起跳瞬间角色仍在 GroundChecker 检测范围内，会被立刻切回 Grounded
            // 导致起跳初速度被 Enter() 重置，跳跃失效
            if (_player.VerticalVelocity > 0f) return;

            // 只有下落阶段才检测落地
            // GroundedState.Enter() 会重置 VerticalVelocity = -2f，清除空中积累的速度
            if (_player.GroundChecker.IsGrounded)
            {
                // isGrounded 由 SyncAnimatorParameters 自动同步，无需手动 Set
                // 落地时判断坡度：超坡 → 滑落状态，普坡 → 地面状态
                if (_player.GroundChecker.GroundAngle > _player.CharacterController.slopeLimit)
                    _player.StateMachine.ChangeState(_player.SlidingState);
                else
                    _player.StateMachine.ChangeState(_player.GroundedState);
            }
        }
        private void ExecuteCoyoteJump()
        {
            // Coyote 跳和普通跳一样，只是在空中触发
            // 已经在 AirborneState，不需要 ChangeState
            _player.VerticalVelocity = _player.JumpForce;
            _player.JumpBufferCounter = 0f;
            _player.CoyoteTimeCounter = 0f; // 消耗掉 Coyote 机会，不能再跳第二次
        }
    }
}

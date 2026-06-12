using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 接地状态。负责地面移动、转向、动画。
    /// 每帧检测 GroundChecker.IsGrounded，离地时切换到 AirborneState。
    /// </summary>
    public class PlayerGroundedState : PlayerStateBase
    {
        public PlayerGroundedState(PlayerController player) : base(player) { }

        public override void Enter()
        {
            // isGrounded 由 SyncAnimatorParameters 自动同步，无需手动 Set

            // 进入接地状态时，重置垂直速度为 -2f
            _player.VerticalVelocity = -2f;
            if (_player.JumpBufferCounter > 0f)
                ExecuteJump();  // Jump Buffer：如果空中按了跳跃键、buffer 还没过期，落地立刻起跳
        }
        public override void Update()
        {
            // 顺序固定：
            HandleGravity();    // 1. 先算重力（更新垂直速度）
            HandleMovement();   // 2. 再移动（把垂直速度打包进 Move）
            base.HandleRotation();   // 3. 再转向（不影响位移计算）
            CheckTransition();  // 4. 最后检测是否要切状态
        }
        public override void Exit() { }

        private void HandleGravity()
        {
            // 接地时持续施加微小向下压力，保持 CC 与地面接触
            // 如果垂直速度已经是正值（比如即将跳跃），不覆盖
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            // 水平方向由 MoveDirection 决定，垂直方向由 VerticalVelocity 决定
            // 两者合并成一个 Vector3 交给 Move()，由 CC 统一做碰撞检测
            Vector3 velocity = _player.MoveDirection * _player.MoveSpeed;
            velocity.y = _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }



        private void CheckTransition()
        {
            // 在地面上检测到有跳跃输入（JumpBufferCounter > 0）→ 执行起跳
            if (_player.JumpBufferCounter > 0f)
            {
                ExecuteJump();
                return; // 已经切状态，不再往下走
            }

            // 自然离地（走下台阶、走出平台边缘）→ 切换到空中状态
            // 此时开启 Coyote Time，给玩家一小段宽限期仍然可以跳
            if (!_player.GroundChecker.IsGrounded)
            {
                _player.CoyoteTimeCounter = _player.CoyoteTime;
                _player.StateMachine.ChangeState(_player.AirborneState);
                return;
            }
            // 站上超坡 → 进入滑落状态
            if (_player.GroundChecker.GroundAngle > _player.CharacterController.slopeLimit)
            {
                _player.StateMachine.ChangeState(_player.SlidingState);
            }
        }
        private void ExecuteJump()
        {
            _player.Animator.SetTrigger(JumpHash);
            // 给垂直速度一个正的初速度，后续由 AirborneState 的重力积分把它往下拉
            _player.VerticalVelocity = _player.JumpForce;
            // 消耗掉这次跳跃输入，防止重复触发
            _player.JumpBufferCounter = 0f;
            // 跳跃是主动起跳，不给 Coyote Time
            // （Coyote 是"走出边缘后的宽限"，主动跳跃不需要）
            _player.CoyoteTimeCounter = 0f;
            _player.StateMachine.ChangeState(_player.AirborneState);
        }
    }
}

using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 接地状态。负责地面移动、转向，以及 Dash -> Attack -> Jump -> 离地 -> 超坡的转换优先级。
    /// 攻击分支只调用 PlayerControllerBase.TryStartAttack；共享 State 不依赖具体角色或法术模块。
    /// </summary>
    public class PlayerGroundedState : PlayerStateBase
    {
        public PlayerGroundedState(PlayerControllerBase player) : base(player)
        {
        }

        public override void Enter()
        {
            // isGrounded 由 SyncAnimatorParameters 自动同步，无需手动 Set

            // 清除任何未被旧 Animator 转移消费的 jump Trigger。若存在落地 Jump Buffer，
            // 下方 ExecuteJump 会在清理之后重新 SetTrigger，因此不会吞掉真正的新起跳。
            _player.ClearPendingJumpAnimation();

            // 进入接地状态时，重置垂直速度为 -2f
            _player.VerticalVelocity = -2f;
            // “落地”可以重置空中能力：二段跳和空中 Dash 各恢复一次。
            _player.ResetAirActionBudget();
            _player.IsAirborneForDash = false;
            if (_player.JumpBufferCounter > 0f)
                ExecuteJump(); // Jump Buffer：如果空中按了跳跃键、buffer 还没过期，落地立刻起跳
        }

        public override void Update()
        {
            // 顺序固定：
            HandleGravity(); // 1. 先算重力（更新垂直速度）
            HandleMovement(); // 2. 再移动（把垂直速度打包进 Move）
            base.HandleRotation(); // 3. 再转向（不影响位移计算）
            CheckTransition(); // 4. 最后检测是否要切状态
        }

        public override void Exit()
        {
        }

        #region 处理流程函数

        private void HandleGravity()
        {
            // 这行判断代码相当于把接地状态下的重力累积给“截断”了。
            // 它告诉系统：“只要角色在往下掉（速度为负），就只允许它以 -2f 的极小速度往下压，不要累积重力。”
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            // _player.MoveDirection 是一个 Vector3 方向向量，MoveSpeed是标量
            // StatusMoveSpeedMultiplier 也是标量，是一个比例，比如减速30%就是0.7（比如被粘稠状态了）
            // 这里体现了一个很常见的 Gameplay 设计：方向、基础属性和状态修正分开保存。
            Vector3 velocity = _player.MoveDirection * _player.MoveSpeed * _player.StatusMoveSpeedMultiplier;   // 水平速度
            velocity.y = _player.VerticalVelocity;  // velocity.y 替换为独立维护的垂直速度
            // CharacterController.Move 接收“本帧位移”而不是速度，所以速度必须乘 deltaTime。
            // Move 会执行胶囊体碰撞约束，但不会像 Rigidbody 一样自动施加重力；垂直速度由 Gameplay 自己维护。
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        private void CheckTransition()
        {
            // 冲刺输入（最高优先级：Dash → Attack → Jump）。
            // 缓冲与冷却正交：DashBufferCounter 给亚帧容错；DashCooldownCounter 把关能力锁。
            if (_player.DashBufferCounter > 0f && _player.DashCooldownCounter <= 0f)
            {
                _player.IsAirborneForDash = false;
                _player.StateMachine.ChangeState(_player.DashState);
                return;
            }

            // 攻击输入（优先于跳跃检测）。具体攻击逻辑由角色子类经 TryStartAttack() 决定——
            // 共享 GroundedState 不知道也不关心是连段近战还是弓箭，只负责保留 Dash→Attack→Jump 优先级。
            if (_player.TryStartAttack())
                return;

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

        #endregion

        #region 功能函数

        private void ExecuteJump()
        {
            _player.PlayJumpStartedFeedback();
            // 给垂直速度一个正的初速度，后续由 AirborneState 的重力积分把它往下拉
            _player.VerticalVelocity = _player.JumpForce;
            _player.ArmJumpCut();
            // 消耗掉这次跳跃输入，防止重复触发
            _player.JumpBufferCounter = 0f;
            // 跳跃是主动起跳，不给 Coyote Time
            _player.CoyoteTimeCounter = 0f;
            _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }
}

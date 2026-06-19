using UnityEngine;
using Game.Combat;

namespace Game.Character
{
    /// <summary>
    /// 攻击状态。负责：
    /// 1. 触发攻击动画
    /// 2. 根据 AttackDefinition 的激活窗口区间，每帧开/关 MeleeHitDetector 的命中窗口
    /// 3. 动画播到 85% 时切回移动状态
    /// </summary>
    public class PlayerAttackState : PlayerStateBase
    {
        // 攻击动画的 Animator 状态名 hash，用于判断当前是否在攻击状态
        // 必须和 Animator Controller 里的状态名完全一致
        private static readonly int NormalAttackStateHash =
            Animator.StringToHash("NormalAttack01_SingleTwohandSword");

        public PlayerAttackState(PlayerController player) : base(player) { }

        public override void Enter()
        {
            // 触发攻击动画（事件型 Trigger，和 jump 同理）
            _player.Animator.SetTrigger(AttackHash);
            // 消耗掉这次攻击输入，防止重复触发
            _player.AttackBufferCounter = 0f;
        }
        public override void Update()
        {
            // 攻击时施加向下压力，保持与地面接触（防止在空中攻击时飘起来）
            HandleGravity();
            // 攻击时不移动（根据设计决策：挥砍时角色原地，可后续改为保留移动）
            HandleMovement();
            // 每帧根据动画进度开/关命中窗口——这是数据驱动窗口的核心
            HandleAttackWindow();
            CheckTransition();
        }
        public override void Exit()
        {
            // 离开攻击状态时强制关闭命中窗口，防止残留
            _player.MeleeHitDetector?.CloseHitWindow();
        }

        private void HandleGravity()
        {
            // 攻击时保持-2f的向下压力，和 GroundedState 逻辑一致
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            // 攻击时锁定水平移动，只保留垂直速度（重力）
            // 直接调用 Move 是必须的：CC 每帧必须被 Move，否则 isGrounded 检测会失效
            Vector3 velocity = Vector3.up * _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        private void HandleAttackWindow()
        {
            // 检查玩家的近战检测器和攻击定义数据是否存在
            if (_player.MeleeHitDetector == null) return;
            AttackDefinition def = _player.MeleeHitDetector.Attack;
            if (def == null) return;

            // 判断当前动画是否处于过渡期（即两个动画混合的淡入淡出阶段）。如果是，强制关闭伤害判定窗口并返回。
            // 原因：过渡时 normalizedTime 读到的是源状态的值，不代表攻击动画的进度
            if (_player.Animator.IsInTransition(0))
            {
                _player.MeleeHitDetector.CloseHitWindow();
                return;
            }

            // 获取第 0 层动画的归一化时间 normalizedTime。
            // AnimatorStateInfo.normalizedTime:动画的归一化时间。当动画播放到起点时为 0，播放到终点时为 1。
            // 特殊情况：如果动画状态被设置为循环，那么播放第二圈时 normalizedTime 会变成 1 到 2 之间的值，第三圈为 2 到 3，以此类推
            // normalizedTime % 1f：即只保留小数部分，得到当前动画循环内的进度（0=开始，1=结束）
            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;

            // 在激活帧区间内开窗，否则关窗
            // ActiveStart/ActiveEnd 在 AttackDefinition SO 里配置，调整不需要改代码
            if (t >= def.ActiveStart && t <= def.ActiveEnd)
                _player.MeleeHitDetector.OpenHitWindow();
            else
                _player.MeleeHitDetector.CloseHitWindow();
        }

        private void CheckTransition()
        {
            if (_player.Animator.IsInTransition(0))
            {
                // 过渡期：检查是否正在"离开"攻击状态（下一个状态不是攻击状态）
                // 当 Animator 的 ExitTime(0.85) 触发、开始向 Idle/Run 过渡时，C# 状态机跟着切回
                int nextHash = _player.Animator.GetNextAnimatorStateInfo(0).shortNameHash;
                if (nextHash != NormalAttackStateHash)
                    TransitionToMovement();
                return;
            }

            // 非过渡期保底：normalizedTime 达到 85% 时主动切回
            // 防止 Animator Transition 没有正确触发的极端情况
            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= 0.85f)
                TransitionToMovement();
        }

        private void TransitionToMovement()
        {
            // 根据接地状态决定切到哪个状态（攻击时跌落悬崖的情况也能正确处理）
            if (_player.GroundChecker.IsGrounded)
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                _player.StateMachine.ChangeState(_player.AirborneState);
        }
    }
}

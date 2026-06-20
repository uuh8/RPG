using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 攻击状态 —— 连段驱动。同一个实例承载整套连段：
    /// 1. Enter 起手段 0：CrossFade 到该段动画 + 把该段 AttackDefinition 推给命中判定器
    /// 2. Update 每帧：按段 ActiveStart/End 开/关命中窗口；用 ComboResolver 判定维持/推进/结束
    ///    - Advance：comboIndex++，消耗缓冲输入，CrossFade 下一段并换段
    ///    - End：动画播完未接 → 切回移动状态
    /// 3. Exit：comboIndex 归零 + 清攻击缓冲（防泄漏）+ 关命中窗口
    ///    —— 任何离开攻击态的路径（自然结束/未来受击打断）都经 Exit，是连段归零的唯一责任点。
    /// </summary>
    public class PlayerAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f; // 动画进度达到此值且本段未推进 → 结束连段
        private const float CrossFadeDuration = 0.1f; // 段切换 CrossFade 固定时长（秒）
        private int _comboIndex; // 当前打到第几段（0 起）。仅在本状态内维护，唯一归零点是 Exit()。

        public PlayerAttackState(PlayerController player) : base(player)
        {
        }

        #region 状态机函数

        public override void Enter()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 消耗起手输入，防重复触发

            // _combo 未配置或无段落时安全退出，避免 StartSegment→Combo.Segments[0] 直接 NRE
            if (_player.Combo == null || _player.Combo.SegmentCount == 0)
            {
                GameLog.Warn("ComboDefinition 未配置或无段落，无法攻击", "Combat");
                TransitionToMovement();
                return;
            }

            StartSegment(0);
        }

        public override void Update()
        {
            // 攻击时施加向下压力，保持与地面接触（防止飘起来）
            HandleGravity();
            // 攻击时锁定水平移动，只保留垂直速度（重力）
            HandleMovement();
            // 每帧按当前段进度开/关命中窗口
            HandleAttackWindow();
            // 连段判定：维持/推进/结束
            CheckCombo();
        }

        public override void Exit()
        {
            _comboIndex = 0; // 归零（唯一责任点）
            _player.AttackBufferCounter = 0f; // 清残留，防连段结束后误触发新普攻
            _player.MeleeHitDetector?.CloseHitWindow(); // 关窗，防残留
        }

        #endregion


        #region 处理流程函数

        /// <summary>切到第 index 段：换命中数据 + 关窗（让新段在 ActiveStart 重新开窗清去重）+ CrossFade 动画。</summary>
        private void StartSegment(int index)
        {
            // 从玩家（_player）的连击配置（Combo）中，根据索引取出对应的攻击定义（AttackDefinition）。
            AttackDefinition seg = _player.Combo.Segments[index];

            if (_player.MeleeHitDetector != null)
            {
                _player.MeleeHitDetector.SetAttack(seg); // 武器现在"知道"自己这一下打多少伤害、命中盒多大
                _player.MeleeHitDetector.CloseHitWindow(); // 先确保命中窗口是关着的（防止上一次攻击的窗口状态残留）
            }

            int hash = _player.GetComboStateHash(index); // 获取该连击段落对应的动画状态机的哈希值
            // CrossFadeInFixedTime 保证过渡时间严格固定为你指定的秒数，不受动画播放速度影响。
            // 在连击系统中，这非常重要，因为它确保了无论当前攻击动画的播放速度如何，连招的衔接手感（过渡时间）始终稳定一致
            _player.Animator.CrossFadeInFixedTime(hash, CrossFadeDuration, 0);
        }

        private void HandleGravity()
        {
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            // 锁定水平移动；CC 每帧必须被 Move，否则 isGrounded 检测失效
            Vector3 velocity = Vector3.up * _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        /// <summary>
        /// 处理攻击窗口的开启和关闭，根据动画的状态和时间来控制攻击判定窗口
        /// </summary>
        private void HandleAttackWindow()
        {
            if (_player.MeleeHitDetector == null) return;
            AttackDefinition def = _player.MeleeHitDetector.Attack;
            if (def == null) return;

            // 过渡期：normalizedTime 读到的是源状态的值，不代表当前段进度 → 强制关窗
            // 检查动画是否处于过渡状态，如果是则关闭攻击窗口
            if (_player.Animator.IsInTransition(0))
            {
                _player.MeleeHitDetector.CloseHitWindow();
                return;
            }

            // 获取当前动画状态的标准化时间（0-1之间）
            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;

            // 检查当前时间是否在攻击判定窗口的激活范围内
            if (t >= def.HitActiveStart && t <= def.HitActiveEnd)
                _player.MeleeHitDetector.OpenHitWindow();
            else
                _player.MeleeHitDetector.CloseHitWindow();
        }

        /// <summary>
        /// 检查连段状态，根据当前动画状态和输入决定是否进入下一连段或结束连段
        /// </summary>
        private void CheckCombo()
        {
            // 过渡期不做连段判定：normalizedTime 还是上一段的值（也防同帧切段后立刻又判一次）
            if (_player.Animator.IsInTransition(0)) return;

            AttackDefinition seg = _player.Combo.Segments[_comboIndex];
            // Inspector 里 Segments 该格未赋值时 seg 为 null，安全退出而不是每帧 NRE
            if (seg == null)
            {
                GameLog.Warn($"连段第 {_comboIndex} 段 AttackDefinition 未赋值，连段中断", "Combat");
                TransitionToMovement();
                return;
            }

            // 获取当前动画状态的标准化时间（0-1之间）
            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            // 检查是否有缓冲输入
            bool hasBuffer = _player.AttackBufferCounter > 0f;

            // 使用连段解析器决定连段状态
            ComboDecision decision = ComboResolver.Resolve(
                _comboIndex,
                _player.Combo.SegmentCount,
                t,
                hasBuffer,
                seg.ComboInputStart,
                seg.ComboInputEnd,
                EndThreshold
            );

            // 根据连段决策执行相应操作
            switch (decision)
            {
                case ComboDecision.Advance:
                    // 进入下一连段，并消耗缓冲输入
                    _comboIndex++;
                    _player.AttackBufferCounter = 0f; // 消耗输入：防同帧重复推进 + 防泄漏
                    StartSegment(_comboIndex);
                    break;
                case ComboDecision.End:
                    // 结束连段，转为移动状态
                    TransitionToMovement();
                    break;
                // ComboDecision.Continue: 维持当前段，无操作
            }
        }

        #endregion

        #region 功能函数

        /// <summary>
        /// 切换到移动状态的私有方法
        /// 根据玩家是否在地面上来决定切换到地面状态还是空中状态
        /// </summary>
        private void TransitionToMovement()
        {
            // 检查玩家是否接触地面
            if (_player.GroundChecker.IsGrounded)
                // 如果玩家在地面，则切换到地面状态
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                // 如果玩家不在地面，则切换到空中状态
                _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }
}

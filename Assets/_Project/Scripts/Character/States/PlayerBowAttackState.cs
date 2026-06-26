using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 弓箭手普通攻击状态（远程）。与 PlayerAttackState 同骨架（CrossFade 单段 + normalizedTime +
    /// ComboResolver 走向 End + 回移动），但"活动期"是在 ArrowSpawnTime 单点生成一支箭矢，
    /// 而非 OverlapBox 命中窗口；无连段推进（1 段）、无刀光。平行实现，不复用 Warrior 的 PlayerAttackState。
    /// </summary>
    public class PlayerBowAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f;     // 动画进度达此值 → 结束（1 段永不 Advance）
        private const float CrossFadeDuration = 0.1f; // 进入攻击段 CrossFade 固定时长

        private readonly ArcherController _archer; // typed 子类引用，拿弓箭手专属成员

        private int _comboIndex;
        private bool _arrowSpawned; // 本次播放是否已生成过箭矢（单点越阈触发一次的去重位）

        public PlayerBowAttackState(ArcherController player) : base(player)
        {
            _archer = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 消耗起手输入
            _arrowSpawned = false;

            if (_archer.Combo == null || _archer.Combo.SegmentCount == 0)
            {
                GameLog.Warn("弓箭手 ComboDefinition 未配置或无段落，无法攻击", "Combat");
                TransitionToMovement();
                return;
            }

            StartSegment(0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleMovement();      // 边走边射：保留完整水平移动（不锁脚）
            base.HandleRotation(); // 随移动方向转向；箭在 ArrowSpawnTime 沿当前朝向射出
            HandleArrowSpawn();    // 单点：normalizedTime 越过 ArrowSpawnTime 生成一次
            CheckEnd();            // 射出即交还控制权（节奏交给射速冷却，与动画长度解耦）
        }

        public override void Exit()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 清残留，防攻击结束后误触发
        }

        #endregion

        #region 处理流程函数

        private void StartSegment(int index)
        {
            _arrowSpawned = false; // 新段重置去重位
            int hash = _archer.GetComboStateHash(index);
            _player.Animator.CrossFadeInFixedTime(hash, CrossFadeDuration, 0);
        }

        private void HandleGravity()
        {
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            // 边走边射：与接地态相同的完整移动（水平 MoveDirection*MoveSpeed + 垂直 VerticalVelocity）
            Vector3 velocity = _player.MoveDirection * _player.MoveSpeed;
            velocity.y = _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        private void HandleArrowSpawn()
        {
            if (_arrowSpawned) return;
            if (_player.Animator.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信

            AttackDefinition seg = _archer.Combo.Segments[_comboIndex];
            if (seg == null) return;

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= seg.ArrowSpawnTime)
            {
                SpawnArrow(seg);
                _arrowSpawned = true;
            }
        }

        private void SpawnArrow(AttackDefinition seg)
        {
            if (_archer.ArrowPrefab == null || _archer.ArrowSpawnPoint == null)
            {
                GameLog.Warn("弓箭手 ArrowPrefab/ArrowSpawnPoint 未配置，无法生成箭矢", "Combat");
                return;
            }

            // 沿角色当前朝向发射（轨道相机风格，无准星瞄准——本阶段简化）
            Vector3 dir = _player.transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f) dir = _player.transform.forward; // 退化兜底（直立角色不会发生）
            dir.Normalize();

            Transform sp = _archer.ArrowSpawnPoint;
            GameObject go = Object.Instantiate(_archer.ArrowPrefab, sp.position, Quaternion.LookRotation(dir));

            Arrow arrow = go.GetComponent<Arrow>();
            if (arrow == null)
            {
                GameLog.Warn("ArrowPrefab 上没有 Arrow 组件", "Combat");
                return;
            }

            byte team = _archer.Health != null ? _archer.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();
            Vector3 velocity = dir * _archer.ProjectileSpeed;
            arrow.Init(team, attackerId, seg.BaseAmount, seg.Type, velocity, _player.CharacterController);
        }

        /// <summary>
        /// 提前结束：箭一旦射出就立刻交还控制权回到移动态，不再干等到动画 EndThreshold(0.85)——
        /// 让两发的最小间隔由 ArcherController 的射速冷却(AttackCooldown)决定，而非动画长度。
        /// 动画本身仍由 Animator 的退出连线自然播完/淡出；下一发到来时 CrossFade 盖过其尾巴。
        /// 兜底：若因配置问题始终没射出，动画接近播完(EndThreshold)也强制结束，避免卡死在攻击态。
        /// </summary>
        private void CheckEnd()
        {
            if (_arrowSpawned)
            {
                TransitionToMovement();
                return;
            }
            if (_player.Animator.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信
            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= EndThreshold)
                TransitionToMovement();
        }

        #endregion

        #region 功能函数

        private void TransitionToMovement()
        {
            if (_player.GroundChecker.IsGrounded)
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }
}

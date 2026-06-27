using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师普通攻击状态（远程）。与 PlayerBowAttackState 同骨架：CrossFade 单段 + normalizedTime 单点生成发射物 +
    /// ComboResolver 走向 End + 边走边射；区别仅在发射物是 Fireball（命中爆炸 + 引燃）而非 Arrow。1 段、无蓄力、无刀光。
    /// </summary>
    public class PlayerWizardAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f;     // 动画进度达此值 → 结束（1 段永不 Advance）
        private const float CrossFadeDuration = 0.1f; // 进入攻击段 CrossFade 固定时长

        private const int MaxAimHits = 16; // 屏幕中心射线一次最多记录的命中数（预分配，零 GC）

        private readonly WizardController _wizard;    // typed 子类引用，拿法师专属成员

        private int _comboIndex;
        private bool _fireballSpawned;                // 本次播放是否已生成过火球（单点越阈触发一次的去重位）

        // 瞄准射线命中缓冲（生成时一次性用，状态对象只在 Awake 建一次 → 零每帧 GC）
        private readonly RaycastHit[] _aimHits = new RaycastHit[MaxAimHits];

        public PlayerWizardAttackState(WizardController player) : base(player)
        {
            _wizard = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 消耗起手输入
            _fireballSpawned = false;

            if (_wizard.Combo == null || _wizard.Combo.SegmentCount == 0)
            {
                GameLog.Warn("法师 ComboDefinition 未配置或无段落，无法攻击", "Combat");
                TransitionToMovement();
                return;
            }

            StartSegment(0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleMovement();      // 边走边射：保留完整水平移动（不锁脚）
            HandleAimRotation();   // 身体转向相机水平朝向（朝准心方向，不再随移动乱转）
            HandleFireballSpawn(); // 单点：normalizedTime 越过 ArrowSpawnTime 生成一次
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
            _fireballSpawned = false; // 新段重置去重位
            int hash = _wizard.GetComboStateHash(index);
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

        private void HandleFireballSpawn()
        {
            if (_fireballSpawned) return;
            if (_player.Animator.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信

            AttackDefinition seg = _wizard.Combo.Segments[_comboIndex];
            if (seg == null) return;

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= seg.ArrowSpawnTime)
            {
                SpawnFireball(seg);
                _fireballSpawned = true;
            }
        }

        private void SpawnFireball(AttackDefinition seg)
        {
            if (_wizard.FireballPrefab == null || _wizard.FireballSpawnPoint == null)
            {
                GameLog.Warn("法师 FireballPrefab/FireballSpawnPoint 未配置，无法生成火球", "Combat");
                return;
            }

            Transform sp = _wizard.FireballSpawnPoint;

            // 朝屏幕中心（轨道相机）瞄准：从生成点直线指向准心命中点（含俯仰），不再用角色朝向。
            // 修复"出手前 0.x 秒里转身导致火球飞向转身中途朝向"的瞄准漂移。
            Vector3 targetPoint = ResolveAimTargetPoint(_wizard.FireballAimMask, _wizard.AimMaxDistance, _aimHits);
            Vector3 dir = targetPoint - sp.position;
            if (dir.sqrMagnitude < 1e-6f) dir = _player.transform.forward; // 退化兜底
            dir.Normalize();

            GameObject go = Object.Instantiate(_wizard.FireballPrefab, sp.position, Quaternion.LookRotation(dir));

            Fireball fireball = go.GetComponent<Fireball>();
            if (fireball == null)
            {
                GameLog.Warn("FireballPrefab 上没有 Fireball 组件", "Combat");
                return;
            }

            byte team = _wizard.Health != null ? _wizard.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();
            Vector3 velocity = dir * _wizard.ProjectileSpeed;
            // 直线飞行：基类 Init 默认 useGravity=true（抛物线），火球须显式关重力
            fireball.Init(team, attackerId, seg.BaseAmount, seg.Type, velocity, _player.CharacterController, useGravity: false);
        }

        /// <summary>
        /// 提前结束：火球一旦射出就立刻交还控制权回到移动态，不再干等到动画 EndThreshold(0.85)——
        /// 让两发的最小间隔由 WizardController 的射速冷却(AttackCooldown)决定，而非动画长度。
        /// 动画本身仍由 Animator 的退出连线自然播完/淡出；下一发到来时 CrossFade 盖过其尾巴。
        /// 兜底：若因配置问题始终没射出，动画接近播完(EndThreshold)也强制结束，避免卡死在攻击态。
        /// </summary>
        private void CheckEnd()
        {
            if (_fireballSpawned)
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

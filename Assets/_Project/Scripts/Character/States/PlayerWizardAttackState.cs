using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 法师施法状态：播放施法动画（来自 ComboDefinition 段 0），在 normalizedTime 越过 ArrowSpawnTime 的单点
    /// 调 SpellCaster 运行当前法杖 → 生成对应投射物。1 段、无蓄力、边走边施。
    /// 投射物数据来自法杖（法术编程系统）；动画/时机来自 Combo 段。
    /// </summary>
    public class PlayerWizardAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f;     // 动画进度达此值 → 结束
        private const float CrossFadeDuration = 0.1f; // 进入施法段 CrossFade 固定时长

        private readonly WizardController _wizard;

        private int _comboIndex;
        private bool _castReleased;                    // 本次播放是否已运行过法杖（单点越阈触发一次的去重位）
        private bool _airborne;                        // 本次起手是否在空中：决定播空中动画 + 用真实重力

        public PlayerWizardAttackState(WizardController player) : base(player)
        {
            _wizard = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f;
            _castReleased = false;
            _airborne = !_player.GroundChecker.IsGrounded;

            if (_wizard.Combo == null || _wizard.Combo.SegmentCount == 0)
            {
                GameLog.Warn("法师 ComboDefinition 未配置或无段落（施法动画/时机来自 Combo 段），无法施法", "Combat");
                TransitionToMovement();
                return;
            }

            StartSegment(0);
        }

        public override void Update()
        {
            HandleGravity();
            HandleMovement();      // 边走边施：保留完整水平移动
            HandleAimRotation();   // 身体转向相机水平朝向
            HandleCastRelease();   // 单点：normalizedTime 越过 ArrowSpawnTime 运行一次法杖
            CheckEnd();            // 释放即交还控制权（节奏交给射速冷却）
        }

        public override void Exit()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f;
        }

        #endregion

        #region 处理流程函数

        private void StartSegment(int index)
        {
            _castReleased = false;
            int hash = _airborne && _wizard.AirAttackStateHash != 0
                ? _wizard.AirAttackStateHash
                : _wizard.GetComboStateHash(index);
            _player.Animator.CrossFadeInFixedTime(hash, CrossFadeDuration, 0);
        }

        private void HandleGravity()
        {
            if (_airborne)
            {
                float multiplier = _player.VerticalVelocity < 0f
                    ? _player.FallGravityMultiplier
                    : _player.GravityMultiplier;
                _player.VerticalVelocity += Physics.gravity.y * multiplier * Time.deltaTime;
            }
            else if (_player.VerticalVelocity < 0f)
            {
                _player.VerticalVelocity = -2f;
            }
        }

        private void HandleMovement()
        {
            Vector3 velocity = _player.MoveDirection * _player.MoveSpeed;
            velocity.y = _player.VerticalVelocity;
            _player.CharacterController.Move(velocity * Time.deltaTime);
        }

        private void HandleCastRelease()
        {
            if (_castReleased) return;
            if (_player.Animator.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信

            AttackDefinition seg = _wizard.Combo.Segments[_comboIndex];
            if (seg == null) return;

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            if (t >= seg.ArrowSpawnTime)
            {
                ReleaseCast();
                _castReleased = true;
            }
        }

        /// <summary>运行当前法杖：朝按下瞬间锁存的 ClickAimPoint，从法杖前端施放所有 EmitCommand（由 SpellCaster 落地）。</summary>
        private void ReleaseCast()
        {
            if (_wizard.SpellCaster == null || _wizard.FireballSpawnPoint == null)
            {
                GameLog.Warn("法师 SpellCaster/FireballSpawnPoint 未配置，无法施法", "Skills");
                return;
            }

            Vector3 spawnPos = _wizard.FireballSpawnPoint.position;
            Vector3 aimPoint = _wizard.HasClickAim
                ? _wizard.ClickAimPoint
                : spawnPos + _player.transform.forward * 10f; // 未锁存（理论上不会）才退回前向远点
            byte team = _wizard.Health != null ? _wizard.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();

            _wizard.SpellCaster.CastWand(spawnPos, aimPoint, team, attackerId, _player.CharacterController);
        }

        /// <summary>释放即结束回到移动态；兜底：动画接近播完(EndThreshold)也强制结束，避免卡死。</summary>
        private void CheckEnd()
        {
            if (_castReleased)
            {
                TransitionToMovement();
                return;
            }
            if (_player.Animator.IsInTransition(0)) return;
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

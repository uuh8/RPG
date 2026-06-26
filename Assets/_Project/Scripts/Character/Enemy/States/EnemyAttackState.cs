using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 近战出招：原地播攻击动画；按 normalizedTime 在 [HitActiveStart, HitActiveEnd] 开/关命中窗口(复用 MeleeHitDetector)。
    /// 前摇(命中窗口开启前)= 给玩家的 telegraph 预警。
    /// 结束判定与 Animator 退出连线协作：一旦"进入过攻击态后又离开了攻击态"即结束（不和 Has Exit Time 抢 normalizedTime≥0.9）；
    /// EndThreshold 仅作动画循环/无退出连线时的兜底；MaxStateTime 兜底动画名配错(CrossFade 静默失败)导致永不进态的情况。
    /// </summary>
    public class EnemyAttackState : EnemyStateBase
    {
        private const float EndThreshold = 0.95f; // 动画在攻击态内播到此进度即结束（用于循环/无退出连线）
        private const float MaxStateTime = 3f;    // 安全上限：动画名配错时也不永久卡死

        private bool _windowOpen;
        private bool _enteredAnimState; // 是否已确认进入过攻击动画态
        private float _elapsed;

        public EnemyAttackState(EnemyController enemy) : base(enemy) { }

        public override void Enter()
        {
            _windowOpen = false;
            _enteredAnimState = false;
            _elapsed = 0f;
            if (_enemy.Perception.Target != null)
                _enemy.FaceTarget(_enemy.Perception.Target.position); // 出招瞬间对准
            _enemy.CrossFade(_enemy.AttackStateHash);
        }

        public override void Update()
        {
            _enemy.StayGrounded(); // 出招原地不动
            _elapsed += Time.deltaTime;

            Animator anim = _enemy.Animator;
            AttackDefinition atk = _enemy.Definition != null ? _enemy.Definition.Attack : null;
            if (anim == null || atk == null) { Finish(); return; }

            if (!anim.IsInTransition(0))
            {
                AnimatorStateInfo info = anim.GetCurrentAnimatorStateInfo(0);
                if (info.shortNameHash == _enemy.AttackStateHash)
                {
                    _enteredAnimState = true;
                    float t = info.normalizedTime % 1f;

                    if (!_windowOpen && t >= atk.HitActiveStart) { _enemy.OpenAttackWindow(); _windowOpen = true; }
                    if (_windowOpen && t >= atk.HitActiveEnd) { _enemy.CloseAttackWindow(); _windowOpen = false; }

                    if (t >= EndThreshold) Finish(); // 兜底：动画循环/无退出连线时仍能结束
                    return;
                }

                // 不在攻击态：若已进入过 → 动画已自然退出(Has Exit Time) → 结束
                if (_enteredAnimState) { Finish(); return; }
            }

            // 兜底：仅当"从未进入过攻击态"(动画名配错、CrossFade 静默失败)时才超时结束，避免永久卡死。
            // 已进入过则交给上面的"离开攻击态"/EndThreshold 判定，避免误伤超过 MaxStateTime 的长动画。
            if (!_enteredAnimState && _elapsed >= MaxStateTime)
            {
                GameLog.Warn("敌人攻击迟迟未进入攻击动画态(检查 AttackDefinition.AnimationStateName 是否与 Animator 节点名精确一致)，超时结束", "Enemy");
                Finish();
            }
        }

        private void Finish()
        {
            EnemyPerception p = _enemy.Perception;
            _enemy.StateMachine.ChangeState(p.HasTarget ? _enemy.ChaseState : _enemy.IdleState);
        }

        public override void Exit()
        {
            // Exit 是唯一必经出口：保证关窗 + 启动冷却（即使被受击/死亡打断也成立）
            if (_windowOpen) { _enemy.CloseAttackWindow(); _windowOpen = false; }
            _enemy.AttackCooldownCounter = _enemy.Definition != null ? _enemy.Definition.AttackCooldown : 0f;
        }
    }
}

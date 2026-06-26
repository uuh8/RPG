using UnityEngine;
using Game.Combat;

namespace Game.Character
{
    /// <summary>
    /// 近战出招：原地播攻击动画；按 normalizedTime 在 [HitActiveStart, HitActiveEnd] 开/关命中窗口
    /// (复用 MeleeHitDetector)；动画接近播完(EndThreshold)结束并启动冷却。
    /// 前摇(命中窗口开启前)= 给玩家的 telegraph 预警，为后续闪避/格挡博弈铺路。
    /// 时序读法与玩家攻击一致：排除过渡帧 + 校验 shortNameHash 确认确在攻击态。
    /// </summary>
    public class EnemyAttackState : EnemyStateBase
    {
        private const float EndThreshold = 0.9f;
        private bool _windowOpen;

        public EnemyAttackState(EnemyController enemy) : base(enemy) { }

        public override void Enter()
        {
            _windowOpen = false;
            if (_enemy.Perception.Target != null)
                _enemy.FaceTarget(_enemy.Perception.Target.position); // 出招瞬间对准
            _enemy.CrossFade(_enemy.AttackStateHash);
        }

        public override void Update()
        {
            _enemy.StayGrounded(); // 出招原地不动

            Animator anim = _enemy.Animator;
            AttackDefinition atk = _enemy.Definition != null ? _enemy.Definition.Attack : null;
            if (anim == null || atk == null) { Finish(); return; }
            if (anim.IsInTransition(0)) return; // 过渡期 normalizedTime 不可信

            AnimatorStateInfo info = anim.GetCurrentAnimatorStateInfo(0);
            if (info.shortNameHash != _enemy.AttackStateHash) return; // 确认确在攻击态

            float t = info.normalizedTime % 1f;

            if (!_windowOpen && t >= atk.HitActiveStart) { _enemy.OpenAttackWindow(); _windowOpen = true; }
            if (_windowOpen && t >= atk.HitActiveEnd) { _enemy.CloseAttackWindow(); _windowOpen = false; }

            if (t >= EndThreshold) Finish();
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

using UnityEngine;

namespace Game.Character
{
    /// <summary>近战追击：朝玩家移动并转向；进入攻击距离且冷却就绪 → 出招，冷却中则停下等待；丢失目标回待机。</summary>
    public class EnemyChaseState : EnemyStateBase
    {
        private readonly MeleeEnemyController _melee; // typed 子类引用，取近战专属(AttackState)

        public EnemyChaseState(MeleeEnemyController enemy) : base(enemy) { _melee = enemy; }

        public override void Enter() { }

        public override void Update()
        {
            EnemyPerception p = _enemy.Perception;
            if (!p.HasTarget)
            {
                _enemy.StateMachine.ChangeState(_enemy.IdleState);
                return;
            }

            Vector3 targetPos = p.Target.position;
            _enemy.FaceTarget(targetPos);

            if (p.DistanceToTarget <= _enemy.Definition.AttackRange)
            {
                if (_enemy.AttackCooldownCounter <= 0f)
                {
                    _enemy.StateMachine.ChangeState(_melee.AttackState);
                    return;
                }
                _enemy.StayGrounded(); // 在攻击距离但冷却中：停下等待
                return;
            }

            _enemy.MoveTo(targetPos);
        }

        public override void Exit() { }
    }
}

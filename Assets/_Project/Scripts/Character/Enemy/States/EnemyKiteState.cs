using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 远程走位：远追 / 近退 / 中间站档输出。始终面向玩家。
    ///   距离 > AttackRange      → 接近(MoveTo)
    ///   距离 < RetreatDistance  → 后撤(MoveAway，被贴脸时拉开)
    ///   在 [RetreatDistance, AttackRange] 档内且冷却就绪 → 切施法态；冷却中 → 站定等待
    /// 丢失目标 → 回待机。
    /// </summary>
    public class EnemyKiteState : EnemyStateBase
    {
        private readonly RangedEnemyController _ranged;

        public EnemyKiteState(RangedEnemyController enemy) : base(enemy) { _ranged = enemy; }

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
            _enemy.FaceTarget(targetPos); // 始终朝向玩家(便于施法/后撤朝向正确)

            EnemyDefinition def = _enemy.Definition;
            float dist = p.DistanceToTarget;

            if (dist > def.AttackRange)         // 太远 → 接近
            {
                _enemy.MoveTo(targetPos);
                return;
            }
            if (dist < def.RetreatDistance)     // 太近 → 后撤
            {
                _enemy.MoveAway(targetPos);
                return;
            }

            // 档内：冷却就绪 → 施法；否则站定等待
            if (_enemy.AttackCooldownCounter <= 0f)
            {
                _enemy.StateMachine.ChangeState(_ranged.RangedAttackState);
                return;
            }
            _enemy.StayGrounded();
        }

        public override void Exit() { }
    }
}

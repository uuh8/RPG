using UnityEngine;
using Game.Combat;

namespace Game.Character
{
    /// <summary>
    /// 远程走位：远追 / 近退 / 中间站档输出。移动时朝实际路径方向，攻击前才面向玩家。
    ///   距离 > AttackRange      → 沿 NavMesh 接近
    ///   距离 < RetreatDistance  → 从后方/左后/右后候选中选择完整路径后撤
    ///   在 [RetreatDistance, AttackRange] 档内且冷却就绪 → 切施法态；冷却中 → 站定等待
    /// 丢失目标 → 回待机。
    /// </summary>
    public class EnemyKiteState : EnemyStateBase
    {
        private readonly RangedEnemyController _ranged;

        public EnemyKiteState(RangedEnemyController enemy) : base(enemy) { _ranged = enemy; }

        public override void Enter()
        {
            _enemy.BeginNavigation();
        }

        public override void Update()
        {
            EnemyPerception p = _enemy.Perception;
            if (!p.HasTarget)
            {
                _enemy.StateMachine.ChangeState(_enemy.IdleState);
                return;
            }

            Vector3 targetPos = p.Target.position;

            EnemyDefinition def = _enemy.Definition;
            float dist = p.DistanceToTarget;

            if (dist < def.RetreatDistance)
            {
                _enemy.PlanNavigationAwayFrom(targetPos);
                _enemy.FaceNavigationOrTarget(targetPos);
                _enemy.MoveAlongNavigation();
                return;
            }

            // 不做视野判定；路径仍在绕障碍时继续走，进入最终直达段后才允许站定施法。
            _enemy.PlanNavigationTo(targetPos, 0.05f);

            bool inAttackBand = dist >= def.RetreatDistance && dist <= def.AttackRange;
            if (inAttackBand && _enemy.CanAttackThroughNavigation(targetPos, def.AttackRange))
            {
                _enemy.FaceTarget(targetPos);
                if (_enemy.AttackCooldownCounter <= 0f)
                {
                    _enemy.StateMachine.ChangeState(_ranged.RangedAttackState);
                    return;
                }

                _enemy.StayGrounded();
                return;
            }

            _enemy.FaceNavigationOrTarget(targetPos);
            _enemy.MoveAlongNavigation();
        }

        public override void Exit()
        {
            _enemy.StopNavigation(true);
        }
    }
}

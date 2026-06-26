using UnityEngine;

namespace Game.Character
{
    /// <summary>追击：朝玩家移动并转向；进入攻击距离则停下(Task 5 接入出招)；丢失目标回待机。</summary>
    public class EnemyChaseState : EnemyStateBase
    {
        public EnemyChaseState(EnemyController enemy) : base(enemy) { }

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
                _enemy.StayGrounded(); // 到攻击距离：停下（攻击在 Task 5 接入）
                return;
            }

            _enemy.MoveTo(targetPos);
        }

        public override void Exit() { }
    }
}

using UnityEngine;
using Game.Core;
using Game.Combat;

namespace Game.Character
{
    /// <summary>
    /// 感知（Sense）：每帧用 sqrMagnitude 比侦测半径判断玩家是否在范围内；用滞回(进用 DetectRadius、
    /// 已锁定用更大的 LoseRadius)防止边界抖动。MVP 只用半径(360°)；视野锥/视线 Raycast 留作扩展。
    /// 玩家来源 = PlayerControllerBase.Current（零查找）。
    /// </summary>
    public class EnemyPerception
    {
        private readonly EnemyController _enemy;

        public bool HasTarget { get; private set; }
        public Transform Target { get; private set; }
        public float DistanceToTarget { get; private set; }

        public EnemyPerception(EnemyController enemy) { _enemy = enemy; }

        public void Tick()
        {
            EnemyDefinition def = _enemy.Definition;
            PlayerControllerBase player = PlayerControllerBase.Current;
            if (def == null || player == null)
            {
                if (HasTarget) GameLog.Info("丢失目标(玩家不存在)", "Enemy");
                HasTarget = false; Target = null; return;
            }

            Vector3 to = player.transform.position - _enemy.transform.position;
            to.y = 0f;
            float sqr = to.sqrMagnitude;
            float radius = HasTarget ? def.LoseRadius : def.DetectRadius;

            if (sqr <= radius * radius)
            {
                if (!HasTarget) GameLog.Info("发现玩家 → 进入战斗", "Enemy");
                HasTarget = true;
                Target = player.transform;
                DistanceToTarget = Mathf.Sqrt(sqr);
            }
            else
            {
                if (HasTarget) GameLog.Info("玩家脱离 → 脱战", "Enemy");
                HasTarget = false; Target = null;
            }
        }
    }
}

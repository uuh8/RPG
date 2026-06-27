using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的近战敌人定义：AI 感知 / 移动 / 攻击 / 受击 的全部可调参数。
    /// 纯数据，仅引用 AttackDefinition（同模块），不引用 Animator/Character。新怪 = 新建一份本资产。
    /// HP/阵营仍在 HealthComponent 上配置（避免重复），本 SO 不含 HP。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Enemy Definition", fileName = "EnemyDefinition")]
    public class EnemyDefinition : ScriptableObject
    {
        [Header("移动")]
        public float MoveSpeed = 3.5f;

        [Header("感知 (半径，带滞回防抖)")]
        [Tooltip("进入战斗的侦测半径")]
        public float DetectRadius = 12f;
        [Tooltip("脱战半径；须 > DetectRadius，避免在边界反复进出战")]
        public float LoseRadius = 16f;

        [Header("攻击")]
        [Tooltip("玩家进入此距离且冷却就绪 → 出招")]
        public float AttackRange = 2.2f;
        [Tooltip("两次攻击的最小间隔(秒)")]
        public float AttackCooldown = 1.5f;
        [Tooltip("挥击数据：命中盒 HalfExtents / 命中窗口 HitActiveStart-End / 伤害 / 动画状态名")]
        public AttackDefinition Attack;

        [Header("受击")]
        [Tooltip("受击硬直时长(秒)：被打中后这段时间定身播受击动画，不追不打")]
        public float HurtDuration = 0.4f;
        [Tooltip("受击动画状态名(可空则不 CrossFade)；须与敌人 Animator 节点名精确一致")]
        public string HurtStateName = "";

        [Header("远程 (仅远程敌人 RangedEnemyController 使用)")]
        [Tooltip("玩家比此距离更近时后撤；与 AttackRange 一起构成站档输出的范围带 [RetreatDistance, AttackRange]")]
        public float RetreatDistance = 4f;
        [Tooltip("火球飞行速度")]
        public float ProjectileSpeed = 18f;
        [Tooltip("发射的投射物预制体(拖 Fireball)；其上需有 Fireball 组件")]
        public GameObject ProjectilePrefab;

        [Header("CrossFade")]
        public float CrossFadeDuration = 0.1f;
    }
}

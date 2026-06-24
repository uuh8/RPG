using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的法师陨石重击定义。引导(channel)→松手(release)→陨石坠落 的全部可调参数。
    /// 仿 ChargeAttackDefinition：纯数据，不引用 Animator/Character。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Meteor Attack Definition", fileName = "MeteorAttackDefinition")]
    public class MeteorAttackDefinition : ScriptableObject
    {
        [Header("动画状态名 (可留空；非空才 CrossFade。须与 Animator 节点名精确一致)")]
        public string ChannelStateName = "";  // 引导循环姿势（代码 CrossFade 进入；可空）
        public string ReleaseStateName = "";  // 松手施法（代码 CrossFade 进入；可空）

        [Header("输入")]
        [Tooltip("按住超过此秒数才进入引导；短于此为点按普攻（火球）")]
        public float TapThreshold = 0.15f;

        [Header("瞄准 (引导期间屏幕中心射线打地面求落点)")]
        [Tooltip("射线最大距离；同时作为落点距玩家的最大水平距离上限")]
        public float AimMaxDistance = 30f;

        [Header("松手节奏 (秒)")]
        [Tooltip("松手到陨石生成的延迟（给施法动作留前摇）")]
        public float MeteorSpawnDelay = 0.3f;
        [Tooltip("松手到状态结束(回到移动)的总时长；应 ≥ MeteorSpawnDelay")]
        public float ReleaseDuration = 0.6f;

        [Header("陨石")]
        public float MeteorSpeed = 40f;          // 陨石飞行速度
        public float SpawnHeight = 25f;          // 落点正上方的生成高度
        public float SpawnHorizontalBack = 10f;  // 生成点相对落点的水平后退量（制造"斜上方"入射角）

        [Header("伤害")]
        public float Damage = 60f;
        public DamageType Type = DamageType.Magical;

        [Header("落点圆框")]
        [Tooltip("落点圆框旋转(欧拉角)。多数竖直朝向的环形特效填 (90,0,0) 可平铺到地面；若仍歪改这里")]
        public Vector3 IndicatorRotationEuler = new Vector3(90f, 0f, 0f);

        [Header("CrossFade")]
        public float CrossFadeDuration = 0.1f;
    }
}

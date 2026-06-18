using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的近战攻击定义。MeleeHitDetector 读取几何与数值；
    /// Character 侧窗口驱动读取 ActiveStart/ActiveEnd（归一化动画时间，本轮不实现驱动）。
    /// 连段/技能系统将复用本资产。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Attack Definition", fileName = "AttackDefinition")]
    public class AttackDefinition : ScriptableObject
    {
        [Header("伤害")]
        public float BaseAmount = 10f;
        public DamageType Type = DamageType.Physical;

        [Header("命中体积 (OverlapBox 半尺寸)")]
        public Vector3 HalfExtents = new Vector3(0.5f, 0.5f, 0.5f);

        [Header("激活窗口 (攻击判定的有效时间区间)")]
        // 0.30 到 0.55 意味着当攻击动画播放到 30% 到 55% 的进度时，才会开启伤害判定
        [Range(0f, 1f)] public float ActiveStart = 0.30f;
        [Range(0f, 1f)] public float ActiveEnd   = 0.55f;
    }
}

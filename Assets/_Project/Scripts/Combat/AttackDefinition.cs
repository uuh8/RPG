using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的近战攻击定义。MeleeHitDetector 读取几何与数值；
    /// Character 侧窗口驱动读取 ActiveStart/ActiveEnd（归一化动画时间）。
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

        [Header("激活窗口 (归一化动画时间 0~1，供 Character 侧驱动)")]
        [Range(0f, 1f)] public float ActiveStart = 0.30f;
        [Range(0f, 1f)] public float ActiveEnd   = 0.55f;

        [Header("连段输入窗口 (归一化动画时间 0~1，此区间内按攻击键可接下一段)")]
        [Range(0f, 1f)] public float ComboInputStart = 0.40f;
        [Range(0f, 1f)] public float ComboInputEnd   = 0.70f;

        [Header("动画")]
        [Tooltip("本段对应的 Animator 状态名，必须与 Animator Controller 中的 state 名完全一致；" +
                 "Character 侧在 Awake 预 hash，本字段是纯字符串，不引用 Animator。")]
        public string AnimationStateName = "";
    }
}

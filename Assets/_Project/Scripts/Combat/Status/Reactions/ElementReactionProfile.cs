using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 角色元素反应的集中调参资产。StatusController 只在创建 Runtime 时读取快照，
    /// 避免连续 Process 在运行中受到 Inspector 数据变化影响。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Element Reaction Profile", fileName = "ElementReactionProfile")]
    public sealed class ElementReactionProfile : ScriptableObject
    {
        public ExtinguishTuning Extinguish;
        public WetCleanseTuning WetCleanse;
        public ToxicCombustionTuning ToxicCombustion;
        public IgniteGooTuning IgniteGoo;

        public ElementReactionTuningSnapshot CreateSnapshot(
            float poisonWetCleanseMultiplier,
            float gooWetCleanseMultiplier)
        {
            return new ElementReactionTuningSnapshot(
                Extinguish,
                WetCleanse,
                ToxicCombustion,
                IgniteGoo,
                poisonWetCleanseMultiplier,
                gooWetCleanseMultiplier);
        }
    }
}

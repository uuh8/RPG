using System;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// Fire + Water 的连续灭火参数。所有 Rate 的单位都是“强度点/秒”，
    /// Runtime 会再乘 deltaTime，得到本帧应消耗的强度点数。
    /// </summary>
    [Serializable]
    public struct ExtinguishTuning
    {
        // Fire 与 Water 都达到该值时进入 Formal 模式；低于阈值仍可按 LowRate 缓慢中和。
        [Min(0f)] public float FormalThreshold;
        [Min(0f)] public float LowRatePerSecond;
        [Min(0f)] public float FormalRatePerSecond;
    }

    /// <summary>
    /// Wet 的通用负面状态清洗预算。满 Wet 时每秒获得完整 Budget，
    /// 实际预算公式为 BudgetPerSecondAtFullWet × Wet/100 × deltaTime。
    /// </summary>
    [Serializable]
    public struct WetCleanseTuning
    {
        [Min(0f)] public float BudgetPerSecondAtFullWet;
    }

    /// <summary>
    /// Fire + Poison 的离散毒爆参数。Threshold 决定能否启动，WindUp 提供可读的前摇，
    /// Consume 决定反应成本，Damage/Radius 决定最终 DamageCommand。
    /// </summary>
    [Serializable]
    public struct ToxicCombustionTuning
    {
        [Min(0f)] public float FireThreshold;
        [Min(0f)] public float PoisonThreshold;
        [Min(0.01f)] public float WindUpSeconds;
        [Min(0f)] public float MaxPoisonConsume;
        [Min(0f)] public float MinEffectiveConsume;
        // 实际伤害公式：BaseDamage + 实际消耗的 Poison × DamagePerPoison。
        [Min(0f)] public float BaseDamage;
        [Min(0f)] public float DamagePerPoison;
        [Min(0f)] public float Radius;
        [Min(0f)] public float CooldownSeconds;
    }

    /// <summary>
    /// Fire + Goo 的连续转化参数。它不是瞬间把 Sticky 清空，而是按秒消费 Goo，
    /// 再用 FirePerGoo 把消费量转换为 Fire；Wet 达阈值时禁止该反应。
    /// </summary>
    [Serializable]
    public struct IgniteGooTuning
    {
        [Min(0f)] public float FireThreshold;
        [Min(0f)] public float GooThreshold;
        [Min(0f)] public float WetInhibitThreshold;
        [Min(0f)] public float RequiredFireCapacity;
        [Min(0f)] public float GooConsumePerSecond;
        [Min(0f)] public float FirePerGoo;
        [Min(0f)] public float CooldownSeconds;
    }

    /// <summary>
    /// Wet 的一帧有限清洗预算分配结果。先 Poison 后 Goo，因此两者不会重复使用同一份预算。
    /// </summary>
    public readonly struct WetCleanseResult
    {
        public readonly float PoisonRemoved;
        public readonly float GooRemoved;

        public WetCleanseResult(float poisonRemoved, float gooRemoved)
        {
            // Result 只描述“计划移除多少”，真正写回 StatusInstance 由上层 Adapter 完成。
            PoisonRemoved = poisonRemoved;
            GooRemoved = gooRemoved;
        }
    }

    /// <summary>
    /// Runtime 使用的完整只读调参快照。构造一次后传入普通 C# 状态机，
    /// 避免持续反应每帧回查 ScriptableObject 或场景组件。
    /// </summary>
    public readonly struct ElementReactionTuningSnapshot
    {
        // 将多个 Inspector/ScriptableObject 参数压成一个只读值快照，
        // Runtime 的 Process 就不需要持有 UnityEngine.Object 引用。
        public readonly ExtinguishTuning Extinguish;
        public readonly WetCleanseTuning WetCleanse;
        public readonly ToxicCombustionTuning ToxicCombustion;
        public readonly IgniteGooTuning IgniteGoo;
        public readonly float PoisonWetCleanseMultiplier;
        public readonly float GooWetCleanseMultiplier;

        public ElementReactionTuningSnapshot(
            ExtinguishTuning extinguish,
            WetCleanseTuning wetCleanse,
            ToxicCombustionTuning toxicCombustion,
            IgniteGooTuning igniteGoo,
            float poisonWetCleanseMultiplier,
            float gooWetCleanseMultiplier)
        {
            // Multiplier 来自各 StatusDefinition：同一份 Wet 预算可以对不同材料有不同效率。
            Extinguish = extinguish;
            WetCleanse = wetCleanse;
            ToxicCombustion = toxicCombustion;
            IgniteGoo = igniteGoo;
            PoisonWetCleanseMultiplier = poisonWetCleanseMultiplier;
            GooWetCleanseMultiplier = gooWetCleanseMultiplier;
        }
    }
}

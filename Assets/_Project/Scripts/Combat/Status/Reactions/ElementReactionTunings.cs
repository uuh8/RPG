using System;
using UnityEngine;

namespace Game.Combat
{
    [Serializable]
    public struct ExtinguishTuning
    {
        [Min(0f)] public float FormalThreshold;
        [Min(0f)] public float LowRatePerSecond;
        [Min(0f)] public float FormalRatePerSecond;
    }

    [Serializable]
    public struct WetCleanseTuning
    {
        [Min(0f)] public float BudgetPerSecondAtFullWet;
    }

    [Serializable]
    public struct ToxicCombustionTuning
    {
        [Min(0f)] public float FireThreshold;
        [Min(0f)] public float PoisonThreshold;
        [Min(0.01f)] public float WindUpSeconds;
        [Min(0f)] public float MaxPoisonConsume;
        [Min(0f)] public float MinEffectiveConsume;
        [Min(0f)] public float BaseDamage;
        [Min(0f)] public float DamagePerPoison;
        [Min(0f)] public float Radius;
        [Min(0f)] public float CooldownSeconds;
    }

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
            Extinguish = extinguish;
            WetCleanse = wetCleanse;
            ToxicCombustion = toxicCombustion;
            IgniteGoo = igniteGoo;
            PoisonWetCleanseMultiplier = poisonWetCleanseMultiplier;
            GooWetCleanseMultiplier = gooWetCleanseMultiplier;
        }
    }
}

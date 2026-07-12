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
    }

    [Serializable]
    public struct IgniteGooTuning
    {
        [Min(0f)] public float FireThreshold;
        [Min(0f)] public float GooThreshold;
        [Min(0f)] public float WetInhibitThreshold;
        [Min(0f)] public float RequiredFireCapacity;
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
}

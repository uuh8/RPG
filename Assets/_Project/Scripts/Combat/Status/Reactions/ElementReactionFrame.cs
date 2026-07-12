using System;

namespace Game.Combat
{
    [Flags]
    public enum StatusMask : byte
    {
        None = 0,
        Fire = 1 << 0,
        Water = 1 << 1,
        Poison = 1 << 2,
        Goo = 1 << 3,
    }

    public readonly struct ReactionSignal
    {
        public readonly ElementReactionId Reaction;
        public readonly ElementReactionPhase Phase;
        public readonly float NormalizedStrength;
        public readonly float ExpectedDuration;

        public ReactionSignal(
            ElementReactionId reaction,
            ElementReactionPhase phase,
            float normalizedStrength,
            float expectedDuration)
        {
            Reaction = reaction;
            Phase = phase;
            NormalizedStrength = normalizedStrength;
            ExpectedDuration = expectedDuration;
        }
    }

    public readonly struct ReactionDamageCommand
    {
        public readonly StatusSource Source;
        public readonly float Amount;
        public readonly float Radius;

        public ReactionDamageCommand(StatusSource source, float amount, float radius)
        {
            Source = source;
            Amount = amount;
            Radius = radius;
        }
    }

    /// <summary>
    /// 一个模拟步的纯输出。最多三个固定 Signal 字段，避免每帧创建数组或 List。
    /// </summary>
    public struct ElementReactionFrame
    {
        public float FireDelta;
        public float WaterDelta;
        public float PoisonDelta;
        public float GooDelta;
        public StatusMask SuppressNaturalDecay;
        public bool HasAreaDamage;
        public ReactionDamageCommand AreaDamage;
        public int SignalCount;
        public ReactionSignal Signal0;
        public ReactionSignal Signal1;
        public ReactionSignal Signal2;

        public void AddSignal(in ReactionSignal signal)
        {
            if (SignalCount == 0)
                Signal0 = signal;
            else if (SignalCount == 1)
                Signal1 = signal;
            else if (SignalCount == 2)
                Signal2 = signal;
            else
                return;

            SignalCount++;
        }
    }
}

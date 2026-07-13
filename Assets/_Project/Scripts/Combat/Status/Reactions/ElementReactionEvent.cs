using Game.Core;

namespace Game.Combat
{
    /// <summary>
    /// Reaction Runtime 的离散生命周期事件。Rendering 只消费 Started/Paused/Resumed/Resolved/Cancelled，
    /// 不接收逐帧 Progress，避免 EventBus 成为高频状态同步通道。
    /// </summary>
    public struct ElementReactionEvent : IGameEvent
    {
        public int TargetId;
        public ElementReactionId Reaction;
        public ElementReactionPhase Phase;
        public float NormalizedStrength;
        public float ExpectedDuration;
    }
}

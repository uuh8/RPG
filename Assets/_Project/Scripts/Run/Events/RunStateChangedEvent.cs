using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// 整局在 NotStarted、Running、Completed、Failed 之间发生有效转换后发布。
    /// 同时携带前后状态，便于 UI 区分首次开局、胜利和失败，而不反查 Scene。
    /// </summary>
    public struct RunStateChangedEvent : IGameEvent
    {
        public RunState PreviousState;
        public RunState CurrentState;
        public int CurrentEncounterIndex;
    }
}

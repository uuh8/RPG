namespace Game.Run
{
    /// <summary>
    /// 整局游戏的高层状态。Completed 与 Failed 都是 Terminal State，
    /// 进入后只能通过创建新 Run（后续由 Restart Scene 完成）重新开始。
    /// </summary>
    public enum RunState : byte
    {
        NotStarted = 0,
        Running = 1,
        Completed = 2,
        Failed = 3
    }
}

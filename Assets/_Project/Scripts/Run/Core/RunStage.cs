namespace Game.Run
{
    /// <summary>
    /// 当前 Run 所处的关卡阶段。它描述跨 Scene 的轻量进度，
    /// 不保存 Scene Object；Player、Boss 与 ElementWorld 仍由各自 Scene 创建。
    /// </summary>
    public enum RunStage : byte
    {
        MapOne = 0,
        BossField = 1,
        Finished = 2
    }
}

namespace Game.Run
{
    /// <summary>
    /// 新 Scene 应如何恢复本局数据。Scene Reload 负责重建 Gameplay Object，
    /// Entry Mode 只告诉 Bootstrap 是新局、首次进入 BossField，还是从 Checkpoint 重试。
    /// </summary>
    public enum RunEntryMode : byte
    {
        NewRun = 0,
        EnterBossField = 1,
        RetryBoss = 2
    }
}

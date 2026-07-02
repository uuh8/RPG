namespace Game.Skills
{
    /// <summary>
    /// Emit 法术携带 payload 时，payload 的释放条件。
    /// None = 普通投射物；OnImpact = 命中触发；AfterDelay = 存活满指定时间触发。
    /// </summary>
    public enum PayloadTriggerMode : byte
    {
        None = 0,
        OnImpact = 1,
        AfterDelay = 2
    }
}

namespace Game.Combat
{
    /// <summary>
    /// 稳定的反应标识。显式数值会被 ScriptableObject 和 EventBus 事件持久化，禁止随意重排。
    /// </summary>
    public enum ElementReactionId : byte
    {
        Extinguish = 0,
        ToxicCombustion = 1,
        IgniteGoo = 2,
    }
}

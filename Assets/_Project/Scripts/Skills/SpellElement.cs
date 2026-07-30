namespace Game.Skills
{
    /// <summary>
    /// 法术的内容元素分类。它服务于 UI、掉落与后续克制规则，
    /// 不等同于 Combat 的 DamageType（当前多数法术伤害仍是 Magical）。
    /// </summary>
    public enum SpellElement : byte
    {
        None = 0,
        Fire = 1,
        Water = 2,
        Arcane = 3,
    }
}

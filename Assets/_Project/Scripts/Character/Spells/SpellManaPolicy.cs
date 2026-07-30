namespace Game.Character
{
    /// <summary>
    /// Mana Policy 只决定施法前是否扣除施法者资源，不改变 Wand Interpreter 的任何语义。
    /// </summary>
    public enum SpellManaPolicy : byte
    {
        SpendCasterMana = 0,
        IgnoreMana = 1,
    }
}

namespace Game.Combat
{
    /// <summary>伤害类型。True 无视防御与抗性。</summary>
    public enum DamageType : byte
    {
        Physical = 0,
        Magical  = 1,
        True     = 2,
    }
}

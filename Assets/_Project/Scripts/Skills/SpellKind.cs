namespace Game.Skills
{
    /// <summary>法术在求值器中的语义类别。</summary>
    public enum SpellKind : byte
    {
        Emit = 0,
        Modify = 1,
        Multicast = 2,
        StaticProjectile = 3,
    }

    /// <summary>产出类法术在运行时的生成方式。</summary>
    public enum SpellSpawnMode : byte
    {
        ForwardProjectile = 0,
        SkyfallAtPoint = 1,
    }
}

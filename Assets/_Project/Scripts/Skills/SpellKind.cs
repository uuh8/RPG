namespace Game.Skills
{
    /// <summary>法术在"求值器眼里"的语义类别。玩家可见的丰富分类（投射物/静态投射物/修正/多重…）收敛成这三种内核语义。</summary>
    public enum SpellKind : byte
    {
        Emit = 0,       // 产出一个投射物/存在物（携带当前修正快照）
        Modify = 1,     // 修改"当前修正状态"，影响其后产出的实体
        Multicast = 2,  // 扩大本次施法的投射物预算（多产出几发）
    }
}

namespace Game.Combat
{
    /// <summary>
    /// Reaction Kernel 使用的抽象元素通道，不等同于角色 StatusKind 或未来 Cell MaterialKind。
    /// Adapter 负责把不同载体的数据映射到这四个稳定通道。
    /// </summary>
    public enum ElementChannel : byte
    {
        Fire = 0,
        Water = 1,
        Poison = 2,
        Goo = 3,
    }
}

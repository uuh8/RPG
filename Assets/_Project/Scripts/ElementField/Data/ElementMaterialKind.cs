namespace Game.ElementField
{
    /// <summary>
    /// Cell 当前保存的主要元素种类。显式指定 byte 可把枚举本身压缩到 1 byte，
    /// 并固定未来序列化、存档或 GPU Buffer 解释这些值时使用的协议。
    /// </summary>
    public enum ElementMaterialKind : byte
    {
        Empty = 0,
        Water = 1,
        Fire = 2,

        // P6-A 只实现 Water/Fire；先保留编号，避免后续插入枚举值破坏已有数据。
        Poison = 3,
        Sticky = 4,
    }
}

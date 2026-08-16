using System.Collections.Generic;

namespace Game.Skills
{
    /// <summary>
    /// 对有序法术引用列表执行插入、删除和移动，供编辑 UI 调用。
    /// 方法直接修改调用方传入的同一个 List&lt;SpellDefinition&gt;，但不访问 GameObject、Canvas 或拖拽事件，
    /// 因而编辑规则可以脱离 Unity Scene 做 EditMode Test。
    /// List.Insert/RemoveAt 可能移动后方元素，时间复杂度为 O(n)；这里只在离散 UI 操作时执行，不是逐帧 Hot Path。
    /// </summary>
    public static class WandEditOps
    {
        /// <summary>
        /// 在 index 处插入一条法术引用。允许 index == Count，表示追加到末尾；
        /// 输入越界时钳制到首/尾，列表已满或引用无效时保持原状。
        /// </summary>
        public static void InsertAt(List<SpellDefinition> spells, int index, SpellDefinition spell, int capacity)
        {
            if (spells == null || spell == null) return;
            if (spells.Count >= capacity) return;
            if (index < 0) index = 0;
            if (index > spells.Count) index = spells.Count;
            // List.Insert 会把 index 及其后的元素整体向右移动；容量不足时 List 内部数组可能扩容。
            spells.Insert(index, spell);
        }

        /// <summary>移除 index 处元素；null 或越界时不抛异常、保持原列表。</summary>
        public static void RemoveAt(List<SpellDefinition> spells, int index)
        {
            if (spells == null) return;
            if (index < 0 || index >= spells.Count) return;
            // RemoveAt 删除指定槽位，并把后续元素整体向左补位，因此序列始终保持连续。
            spells.RemoveAt(index);
        }

        /// <summary>
        /// 把 from 位置的引用移动到 to 位置。先保存引用再 RemoveAt，最后 Insert 到目标槽位，
        /// 表达的是重排而不是复制，因此列表元素数量不会改变。
        /// </summary>
        public static void Move(List<SpellDefinition> spells, int from, int to)
        {
            if (spells == null) return;
            int count = spells.Count;
            if (count <= 1) return;
            if (from < 0 || from >= count) return;
            if (to < 0) to = 0;
            if (to >= count) to = count - 1;
            if (from == to) return;
            // SpellDefinition 是引用类型，这里只暂存资产引用，不会克隆 ScriptableObject。
            SpellDefinition s = spells[from];
            spells.RemoveAt(from);
            spells.Insert(to, s);
        }
    }
}

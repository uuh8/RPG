using System.Collections.Generic;

namespace Game.Skills
{
    /// <summary>
    /// 对法杖法术序列的纯编辑操作（插/删/移），供编程框 UI 调用。纯逻辑、无 Unity 依赖、可 EditMode 单测。
    /// </summary>
    public static class WandEditOps
    {
        /// <summary>在 index 处插入 spell；index 钳到 [0, Count]；已达 capacity 则不插入（满）。spell/spells 为 null 忽略。</summary>
        public static void InsertAt(List<SpellDefinition> spells, int index, SpellDefinition spell, int capacity)
        {
            if (spells == null || spell == null) return;
            if (spells.Count >= capacity) return;
            if (index < 0) index = 0;
            if (index > spells.Count) index = spells.Count;
            spells.Insert(index, spell);
        }

        /// <summary>移除 index 处元素；越界忽略。</summary>
        public static void RemoveAt(List<SpellDefinition> spells, int index)
        {
            if (spells == null) return;
            if (index < 0 || index >= spells.Count) return;
            spells.RemoveAt(index);
        }

        /// <summary>把 from 处元素移动到 to 处；from/to 钳到合法范围；from==to 或元素不足 2 时无操作。</summary>
        public static void Move(List<SpellDefinition> spells, int from, int to)
        {
            if (spells == null) return;
            int count = spells.Count;
            if (count <= 1) return;
            if (from < 0 || from >= count) return;
            if (to < 0) to = 0;
            if (to >= count) to = count - 1;
            if (from == to) return;
            SpellDefinition s = spells[from];
            spells.RemoveAt(from);
            spells.Insert(to, s);
        }
    }
}

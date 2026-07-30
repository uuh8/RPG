using System;
using Game.Skills;

namespace Game.Run
{
    /// <summary>
    /// 本局法术库存的纯数据规则层。
    ///
    /// 这里故意不继承 MonoBehaviour，也不读取 Scene：这样“复制模板”和“奖励去重”可以用
    /// EditMode Test 独立验证。数组扩容只发生在拾取奖励这一低频离散事件，不进入 Update 热路径。
    /// </summary>
    public static class RunSpellInventoryOps
    {
        /// <summary>
        /// 为 Runtime State 创建独立数组容器，但继续共享只读的 SpellDefinition 资源引用。
        /// 这是浅拷贝（shallow copy）：隔离的是“排列与增删”，不是复制每一张法术资产。
        /// </summary>
        public static SpellDefinition[] CopyEntries(SpellDefinition[] source)
        {
            if (source == null || source.Length == 0)
                return Array.Empty<SpellDefinition>();

            var copy = new SpellDefinition[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        /// <summary>
        /// 尝试把奖励加入本局库存。成功时返回新数组；重复时复用原数组，避免无意义分配。
        /// </summary>
        public static bool TryAppendUnique(
            SpellDefinition[] current,
            SpellDefinition reward,
            out SpellDefinition[] result)
        {
            if (reward == null)
                throw new ArgumentNullException(nameof(reward));

            current ??= Array.Empty<SpellDefinition>();
            for (int i = 0; i < current.Length; i++)
            {
                // 奖励指向同一个 SpellDefinition Asset 就视为重复，不按名称做脆弱的字符串比较。
                if (ReferenceEquals(current[i], reward))
                {
                    result = current;
                    return false;
                }
            }

            result = new SpellDefinition[current.Length + 1];
            Array.Copy(current, result, current.Length);
            result[current.Length] = reward;
            return true;
        }

        /// <summary>
        /// 把完整法术库中尚未拥有的条目，按 Authoring 顺序追加到 Runtime Library。
        /// 这里使用两次顺序扫描而不是临时 List/HashSet：全解锁是低频离散操作，
        /// 15 张左右法术的 O(n²) 成本很小，同时可以避免额外容器与引用相等语义不一致。
        /// </summary>
        public static bool TryMergeUniqueInOrder(
            SpellDefinition[] current,
            SpellDefinition[] fullLibrary,
            out SpellDefinition[] result)
        {
            if (fullLibrary == null)
                throw new ArgumentNullException(nameof(fullLibrary));

            current ??= Array.Empty<SpellDefinition>();

            int missingCount = 0;
            for (int i = 0; i < fullLibrary.Length; i++)
            {
                SpellDefinition candidate = fullLibrary[i];
                if (ContainsReference(current, current.Length, candidate) ||
                    ContainsReference(fullLibrary, i, candidate))
                {
                    continue;
                }

                missingCount++;
            }

            if (missingCount == 0)
            {
                result = current;
                return false;
            }

            result = new SpellDefinition[current.Length + missingCount];
            Array.Copy(current, result, current.Length);

            int writeIndex = current.Length;
            for (int i = 0; i < fullLibrary.Length; i++)
            {
                SpellDefinition candidate = fullLibrary[i];
                if (ContainsReference(current, current.Length, candidate) ||
                    ContainsReference(fullLibrary, i, candidate))
                {
                    continue;
                }

                result[writeIndex++] = candidate;
            }

            return true;
        }

        private static bool ContainsReference(
            SpellDefinition[] entries,
            int count,
            SpellDefinition candidate)
        {
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(entries[i], candidate))
                    return true;
            }

            return false;
        }
    }
}

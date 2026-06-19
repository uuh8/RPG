using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 数据驱动的线性连段定义：按出招顺序排列的段落，每段引用一个 AttackDefinition。
    /// 纯数据，不引用 Animator/InputSystem。新武器 = 新建一份本资产，无需改代码。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Combat/Combo Definition", fileName = "ComboDefinition")]
    public class ComboDefinition : ScriptableObject
    {
        [Tooltip("按出招顺序排列的连段段落；索引 0 为起手段。")]
        public AttackDefinition[] Segments;

        /// <summary>段数；Segments 为空时返回 0。</summary>
        public int SegmentCount => Segments != null ? Segments.Length : 0;
    }
}

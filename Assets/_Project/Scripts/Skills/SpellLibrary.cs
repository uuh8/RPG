using UnityEngine;

namespace Game.Skills
{
    /// <summary>玩家拥有、可拖进法杖的全部法术（调色板/背包的数据源）。MVP：Inspector 配固定起始集。</summary>
    [CreateAssetMenu(menuName = "Game/Skills/Spell Library", fileName = "SpellLibrary")]
    public class SpellLibrary : ScriptableObject
    {
        [Tooltip("调色板里可拖的全部法术")]
        public SpellDefinition[] Available;
    }
}

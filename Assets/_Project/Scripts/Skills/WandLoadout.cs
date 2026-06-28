using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 法杖 = 一段"法术程序"：从左到右的法术序列 + 基础投射物预算。运行时由 SpellCaster 喂给 CastEvaluator。
    /// 早期写死在此资产里；后续阶段 C 由拖拽编程框 UI 编辑。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Skills/Wand Loadout", fileName = "WandLoadout")]
    public class WandLoadout : ScriptableObject
    {
        [Tooltip("法杖里从左到右的法术序列（求值器据此运行）")]
        public SpellDefinition[] Spells;

        [Tooltip("基础投射物预算（施放数）。多重法术在此之上叠加")]
        public int BaseDraws = 1;
    }
}

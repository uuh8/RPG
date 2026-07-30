using Game.Skills;
using UnityEngine;

namespace Game.UI
{
    /// <summary>SpellSlot 与单实例 Tooltip Presenter 之间的窄接口。</summary>
    public interface ISpellTooltipHandler
    {
        void ShowSpellTooltip(SpellDefinition spell, Vector2 screenPosition);
        void MoveSpellTooltip(Vector2 screenPosition);
        void HideSpellTooltip();
    }
}

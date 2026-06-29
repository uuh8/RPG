using UnityEngine.EventSystems;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 法术格与总控之间的拖放契约。SpellSlotView 只依赖此接口（不直接依赖 WandEditorController），
    /// 以便各自独立编译；拖放裁决集中在实现方（WandEditorController）。
    /// </summary>
    public interface IWandDragHandler
    {
        void BeginSpellDrag(SpellDefinition spell, SlotKind origin, int index, PointerEventData eventData);
        void UpdateSpellDrag(PointerEventData eventData);
        void EndSpellDrag(PointerEventData eventData);
        void DropOnFrameSlot(int frameIndex);
    }
}

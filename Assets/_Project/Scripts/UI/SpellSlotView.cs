using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 一个法术格（调色板格 / 编程框格通用）。显示图标（空则用名字兜底）；
    /// 有法术时可拖；编程框格可作为放置目标（IDropHandler）。拖放结果交给 IWandDragHandler（总控）裁决。
    /// 需要格子上有 raycastTarget=true 的图形（如本 _icon 的 Image）才能接收拖拽/放置事件。
    /// </summary>
    public class SpellSlotView : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
    {
        [SerializeField] private Image _icon;
        [SerializeField] private Text _nameLabel; // 图标为空时兜底显示名字

        private SpellDefinition _spell;
        private SlotKind _kind;
        private int _index;
        private IWandDragHandler _handler;

        public SpellDefinition Spell => _spell;
        public SlotKind Kind => _kind;
        public int Index => _index;

        /// <summary>绑定数据并刷新显示。spell 为 null = 空格（编程框里的空槽）。</summary>
        public void Bind(SpellDefinition spell, SlotKind kind, int index, IWandDragHandler handler)
        {
            _spell = spell;
            _kind = kind;
            _index = index;
            _handler = handler;
            Refresh();
        }

        private void Refresh()
        {
            bool hasSpell = _spell != null;
            bool hasIcon = hasSpell && _spell.Icon != null;

            if (_icon != null)
            {
                _icon.enabled = hasIcon;
                if (hasIcon) _icon.sprite = _spell.Icon;
            }
            if (_nameLabel != null)
            {
                bool showName = hasSpell && !hasIcon; // 有法术但无图标 → 名字兜底
                _nameLabel.enabled = showName;
                if (showName)
                    _nameLabel.text = string.IsNullOrEmpty(_spell.DisplayName) ? _spell.name : _spell.DisplayName;
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_spell == null || _handler == null) return; // 空格不可拖
            _handler.BeginSpellDrag(_spell, _kind, _index, eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_handler == null) return;
            _handler.UpdateSpellDrag(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_handler == null) return;
            _handler.EndSpellDrag(eventData);
        }

        public void OnDrop(PointerEventData eventData)
        {
            if (_kind != SlotKind.Frame || _handler == null) return; // 只有编程框格是放置目标
            _handler.DropOnFrameSlot(_index);
        }
    }
}

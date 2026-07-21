using UnityEngine;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 调色板（背包）：从 SpellLibrary 铺出可拖的法术格。无限调色板——拖出只是复制引用进编程框，调色板本身不变。
    /// </summary>
    public class SpellPaletteView : MonoBehaviour
    {
        [SerializeField] private SpellLibrary _library;
        [SerializeField] private Transform _container;     // 法术格父节点（建议挂 Layout Group）
        [SerializeField] private SpellSlotView _slotPrefab;

        public SpellLibrary Library => _library;

        /// <summary>
        /// 由 WandEditorController 注入本局 Runtime Library。
        /// View 不负责寻找或创建数据，避免 UI 同时成为数据所有者。
        /// </summary>
        public void BindLibrary(SpellLibrary library)
        {
            _library = library;
        }

        public void Rebuild(IWandDragHandler handler)
        {
            if (_container == null || _slotPrefab == null) return;

            for (int i = _container.childCount - 1; i >= 0; i--)
                Object.Destroy(_container.GetChild(i).gameObject);

            if (_library == null || _library.Available == null) return;

            for (int i = 0; i < _library.Available.Length; i++)
            {
                SpellDefinition spell = _library.Available[i];
                if (spell == null) continue;
                SpellSlotView slot = Object.Instantiate(_slotPrefab, _container);
                slot.Bind(spell, SlotKind.Palette, i, handler);
            }
        }
    }
}

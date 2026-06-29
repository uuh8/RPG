using System.Collections.Generic;
using UnityEngine;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 编程框：渲染 Capacity 个槽位（前 N 个 = WandLoadout.Spells，其余空格）。
    /// 编辑（插/移/删）经 WandEditOps 改一个工作 List → 写回 WandLoadout.Spells；视觉重建由 Rebuild 单独触发
    /// （在一次拖放结束时由总控统一调用，避免拖拽中销毁正在拖的格子）。
    /// </summary>
    public class WandFrameView : MonoBehaviour
    {
        [SerializeField] private WandLoadout _wand;
        [SerializeField] private Transform _container;
        [SerializeField] private SpellSlotView _slotPrefab;
        [SerializeField] private int _capacity = 8;

        private readonly List<SpellDefinition> _work = new List<SpellDefinition>(16);

        public int Capacity => _capacity;

        public void Rebuild(IWandDragHandler handler)
        {
            if (_container == null || _slotPrefab == null) return;

            for (int i = _container.childCount - 1; i >= 0; i--)
                Object.Destroy(_container.GetChild(i).gameObject);

            int count = (_wand != null && _wand.Spells != null) ? _wand.Spells.Length : 0;
            for (int i = 0; i < _capacity; i++)
            {
                SpellDefinition spell = i < count ? _wand.Spells[i] : null;
                SpellSlotView slot = Object.Instantiate(_slotPrefab, _container);
                slot.Bind(spell, SlotKind.Frame, i, handler);
            }
        }

        // ── 编辑操作（仅改数据、写回 SO，不重建视图）──
        public void ApplyInsert(int index, SpellDefinition spell)
        {
            LoadWork();
            WandEditOps.InsertAt(_work, index, spell, _capacity);
            WriteBack();
        }

        public void ApplyMove(int from, int to)
        {
            LoadWork();
            WandEditOps.Move(_work, from, to);
            WriteBack();
        }

        public void ApplyRemove(int index)
        {
            LoadWork();
            WandEditOps.RemoveAt(_work, index);
            WriteBack();
        }

        private void LoadWork()
        {
            _work.Clear();
            if (_wand == null || _wand.Spells == null) return;
            for (int i = 0; i < _wand.Spells.Length; i++)
                if (_wand.Spells[i] != null) _work.Add(_wand.Spells[i]);
        }

        private void WriteBack()
        {
            if (_wand == null) return;
            _wand.Spells = _work.ToArray();
        }
    }
}

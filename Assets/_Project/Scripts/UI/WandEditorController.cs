using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 法杖编程界面总控：按键开关面板（Time.timeScale=0 暂停 + 解锁鼠标）；持有跟随光标的 ghost；
    /// 集中裁决拖放（调色板→框=插入；框内=移动；框→框外=移除）。实现 IWandDragHandler 供法术格回调。
    /// </summary>
    public class WandEditorController : MonoBehaviour, IWandDragHandler
    {
        [Header("References")]
        [SerializeField] private GameObject _panelRoot;        // 整个编程界面根（开关其 active）
        [SerializeField] private SpellPaletteView _palette;
        [SerializeField] private WandFrameView _frame;
        [SerializeField] private WandLoadout _wand;            // 仅用于 header 显示施放数
        [SerializeField] private Image _dragGhost;            // 跟随光标的拖拽影像（raycastTarget 关、置顶层、默认隐藏）

        [Header("Header (只读展示)")]
        [SerializeField] private Text _shuffleLabel;          // 显示 "乱序：否"
        [SerializeField] private Text _castCountLabel;        // 显示 "施放数：N"

        [Header("Input")]
        [SerializeField] private InputActionReference _toggleAction; // 开关界面动作（开发者在 Inspector 指派，如 Tab）

        private bool _open;

        // 拖拽状态
        private SpellDefinition _dragSpell;
        private SlotKind _dragOrigin;
        private int _dragFromIndex;
        private bool _dropHandled;

        private void OnEnable()
        {
            if (_toggleAction != null && _toggleAction.action != null)
            {
                _toggleAction.action.performed += OnTogglePerformed;
                _toggleAction.action.Enable();
            }
        }

        private void OnDisable()
        {
            if (_toggleAction != null && _toggleAction.action != null)
                _toggleAction.action.performed -= OnTogglePerformed;
            if (_open) RestoreGameState(); // 兜底：禁用时仍打开则恢复时间/鼠标，避免卡死
        }

        private void Start()
        {
            if (_panelRoot != null) _panelRoot.SetActive(false);
            _open = false;
        }

        private void OnTogglePerformed(InputAction.CallbackContext ctx) => Toggle();

        public void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        private void Open()
        {
            _open = true;
            if (_panelRoot != null) _panelRoot.SetActive(true);
            RebuildViews();
            RefreshHeader();
            Time.timeScale = 0f;                    // 暂停
            Cursor.lockState = CursorLockMode.None;  // 解锁鼠标用于拖拽
            Cursor.visible = true;
            if (_dragGhost != null) _dragGhost.enabled = false;
        }

        private void Close()
        {
            _open = false;
            if (_panelRoot != null) _panelRoot.SetActive(false);
            RestoreGameState();
        }

        private void RestoreGameState()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            if (_dragGhost != null) _dragGhost.enabled = false;
        }

        private void RebuildViews()
        {
            if (_palette != null) _palette.Rebuild(this);
            if (_frame != null) _frame.Rebuild(this);
        }

        private void RefreshHeader()
        {
            if (_shuffleLabel != null) _shuffleLabel.text = "乱序：否";
            if (_castCountLabel != null)
            {
                int baseDraws = _wand != null ? _wand.BaseDraws : 1;
                _castCountLabel.text = $"施放数：{baseDraws}";
            }
        }

        // ── IWandDragHandler ──
        public void BeginSpellDrag(SpellDefinition spell, SlotKind origin, int index, PointerEventData eventData)
        {
            _dragSpell = spell;
            _dragOrigin = origin;
            _dragFromIndex = index;
            _dropHandled = false;

            if (_dragGhost != null)
            {
                _dragGhost.enabled = true;
                _dragGhost.sprite = spell != null ? spell.Icon : null;
                _dragGhost.rectTransform.position = eventData.position;
            }
        }

        public void UpdateSpellDrag(PointerEventData eventData)
        {
            if (_dragGhost != null && _dragGhost.enabled)
                _dragGhost.rectTransform.position = eventData.position;
        }

        public void DropOnFrameSlot(int frameIndex)
        {
            if (_dragSpell == null || _frame == null) return;
            if (_dragOrigin == SlotKind.Palette)
                _frame.ApplyInsert(frameIndex, _dragSpell);
            else
                _frame.ApplyMove(_dragFromIndex, frameIndex);
            _dropHandled = true;
        }

        public void EndSpellDrag(PointerEventData eventData)
        {
            // 框内法术拖到框外（无 IDropHandler 接收）→ 移除
            if (!_dropHandled && _dragOrigin == SlotKind.Frame && _frame != null)
                _frame.ApplyRemove(_dragFromIndex);

            if (_frame != null) _frame.Rebuild(this); // 一次拖放结束统一重建（Destroy 延迟到帧末，安全）

            if (_dragGhost != null) _dragGhost.enabled = false;
            _dragSpell = null;
            _dropHandled = false;
        }
    }
}

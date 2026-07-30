using System.Collections.Generic;
using System.Text;
using Game.Combat;
using Game.Skills;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// 全 Wand Editor 共用一个 Tooltip。文字只在首次遇到某 Spell 时格式化并缓存；
    /// Pointer Move 只更新 RectTransform，避免鼠标移动热路径产生字符串 GC Alloc。
    /// </summary>
    public sealed class SpellTooltipPresenter : MonoBehaviour
    {
        [SerializeField] private Canvas _canvas;
        [SerializeField] private RectTransform _panel;
        [SerializeField] private Image _icon;
        [SerializeField] private Text _nameLabel;
        [SerializeField] private Text _detailLabel;
        [SerializeField] private Vector2 _pointerOffset = new Vector2(18f, -18f);
        [SerializeField, Min(0f)] private float _edgePadding = 12f;

        private readonly Dictionary<int, string> _detailCache =
            new Dictionary<int, string>(32);
        private readonly StringBuilder _builder = new StringBuilder(384);

        private void Awake()
        {
            if (_canvas == null)
                _canvas = GetComponentInParent<Canvas>();
            if (_panel == null)
                _panel = transform as RectTransform;
            Hide();
        }

        public void Show(SpellDefinition spell, Vector2 screenPosition)
        {
            if (spell == null || _panel == null)
                return;

            if (_icon != null)
            {
                _icon.sprite = spell.Icon;
                _icon.enabled = spell.Icon != null;
            }

            if (_nameLabel != null)
                _nameLabel.text = string.IsNullOrEmpty(spell.DisplayName)
                    ? spell.name
                    : spell.DisplayName;
            if (_detailLabel != null)
                _detailLabel.text = GetOrBuildDetails(spell);

            _panel.gameObject.SetActive(true);
            Canvas.ForceUpdateCanvases();
            Move(screenPosition);
        }

        public void Move(Vector2 screenPosition)
        {
            if (_panel == null || !_panel.gameObject.activeSelf || _canvas == null)
                return;

            RectTransform canvasRect = _canvas.transform as RectTransform;
            if (canvasRect == null)
                return;

            Camera eventCamera = _canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : _canvas.worldCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect,
                    screenPosition,
                    eventCamera,
                    out Vector2 localPoint))
            {
                return;
            }

            Vector2 desired = localPoint + _pointerOffset;
            Rect canvasBounds = canvasRect.rect;
            Rect panelBounds = _panel.rect;
            Vector2 pivot = _panel.pivot;
            float minX = canvasBounds.xMin + panelBounds.width * pivot.x + _edgePadding;
            float maxX = canvasBounds.xMax - panelBounds.width * (1f - pivot.x) - _edgePadding;
            float minY = canvasBounds.yMin + panelBounds.height * pivot.y + _edgePadding;
            float maxY = canvasBounds.yMax - panelBounds.height * (1f - pivot.y) - _edgePadding;
            desired.x = Mathf.Clamp(desired.x, minX, maxX);
            desired.y = Mathf.Clamp(desired.y, minY, maxY);
            _panel.anchoredPosition = desired;
        }

        public void Hide()
        {
            if (_panel != null)
                _panel.gameObject.SetActive(false);
        }

        private string GetOrBuildDetails(SpellDefinition spell)
        {
            int id = spell.GetInstanceID();
            if (_detailCache.TryGetValue(id, out string cached))
                return cached;

            _builder.Clear();
            _builder.Append("类型：").Append(spell.Kind)
                .Append("\n元素：").Append(spell.Element)
                .Append("\nMana：").Append(spell.ManaCost.ToString("0.##"));

            if (spell.Kind == SpellKind.Emit ||
                spell.Kind == SpellKind.StaticProjectile)
            {
                _builder.Append("\n伤害类型：").Append(spell.DamageType);
                if (spell.BaseDamage > 0f)
                    _builder.Append("\n直击伤害：").Append(spell.BaseDamage.ToString("0.##"));
                if (spell.ExplosionDamage > 0f)
                    _builder.Append("\n爆炸伤害：").Append(spell.ExplosionDamage.ToString("0.##"));
                if (spell.FireFieldDamagePerTick > 0f && spell.FireFieldDuration > 0f)
                {
                    float safeInterval = Mathf.Max(0.05f, spell.FireFieldTickInterval);
                    int tickCount = Mathf.CeilToInt(spell.FireFieldDuration / safeInterval);
                    float fieldTotal = spell.FireFieldDamagePerTick * tickCount;
                    _builder.Append("\n火场伤害：")
                        .Append(spell.FireFieldDamagePerTick.ToString("0.##"))
                        .Append(" / ")
                        .Append(safeInterval.ToString("0.##"))
                        .Append("秒，持续 ")
                        .Append(spell.FireFieldDuration.ToString("0.##"))
                        .Append("秒")
                        .Append("\n理论完整伤害：")
                        .Append((spell.BaseDamage + spell.ExplosionDamage + fieldTotal).ToString("0.##"));
                }
            }

            _builder.Append("\n特性：");
            AppendTraits(SpellTraitResolver.Resolve(spell));
            _builder.Append("\n\n");
            if (!string.IsNullOrWhiteSpace(spell.Description))
                _builder.Append(spell.Description);
            else
                AppendGeneratedDescription(spell);

            string result = _builder.ToString();
            _detailCache.Add(id, result);
            return result;
        }

        private void AppendTraits(SpellTraitFlags flags)
        {
            if (flags == SpellTraitFlags.None)
            {
                _builder.Append("基础");
                return;
            }

            bool first = true;
            AppendTrait(flags, SpellTraitFlags.ImpactPayload, "命中触发", ref first);
            AppendTrait(flags, SpellTraitFlags.TimedPayload, "定时触发", ref first);
            AppendTrait(flags, SpellTraitFlags.Multicast, "多重施放", ref first);
            AppendTrait(flags, SpellTraitFlags.StaticProjectile, "静态法术", ref first);
            AppendTrait(flags, SpellTraitFlags.Shield, "护盾", ref first);
            AppendTrait(flags, SpellTraitFlags.DamageModifier, "伤害修正", ref first);
            AppendTrait(flags, SpellTraitFlags.SpeedModifier, "速度修正", ref first);
            AppendTrait(flags, SpellTraitFlags.Spread, "散射", ref first);
            AppendTrait(flags, SpellTraitFlags.Bounce, "弹射", ref first);
            AppendTrait(flags, SpellTraitFlags.Gravity, "重力", ref first);
            AppendTrait(flags, SpellTraitFlags.Homing, "追踪", ref first);
            AppendTrait(flags, SpellTraitFlags.Orbit, "轨道", ref first);
        }

        private void AppendTrait(
            SpellTraitFlags flags,
            SpellTraitFlags flag,
            string label,
            ref bool first)
        {
            if ((flags & flag) == 0)
                return;
            if (!first)
                _builder.Append(" / ");
            _builder.Append(label);
            first = false;
        }

        private void AppendGeneratedDescription(SpellDefinition spell)
        {
            switch (spell.Kind)
            {
                case SpellKind.Multicast:
                    _builder.Append("让当前 Action 额外抽取 ")
                        .Append(Mathf.Max(0, spell.ExtraDraws))
                        .Append(" 个后续法术 Action。");
                    break;
                case SpellKind.Modify:
                    _builder.Append("修改同一 Action 中位于它之后的投射物参数。");
                    break;
                case SpellKind.StaticProjectile:
                    _builder.Append("在目标位置生成静态或天降式法术实体。");
                    break;
                default:
                    _builder.Append(spell.PayloadTrigger == PayloadTriggerMode.None
                        ? "生成一个投射物。"
                        : "生成一个 Trigger 投射物，并携带其后的一个完整 Action。");
                    break;
            }
        }
    }
}

using UnityEngine;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// 一个法术的数据定义（数据驱动：新增法术 = 新建一份本资产，不写代码）。
    /// 字段按 Kind 分组使用：求值器对 Emit 读 Base*/ProjectilePrefab；对 Modify 读 Mod*；对 Multicast 读 ExtraDraws。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Skills/Spell Definition", fileName = "SpellDefinition")]
    public class SpellDefinition : ScriptableObject
    {
        [Header("通用")]
        public SpellKind Kind = SpellKind.Emit;
        public string DisplayName = "";
        [Tooltip("技能图标（仿 Noita，背包/编程框里展示）。可留空，后续补图标资源。")]
        public Sprite Icon;
        [Tooltip("施放本法术消耗的法力")]
        public float ManaCost = 0f;

        [Header("Emit（投射物）—— 仅 Kind=Emit 用")]
        [Tooltip("要生成的投射物预制体（其上需有 ProjectileBase 派生组件，如 Fireball）")]
        public GameObject ProjectilePrefab;
        public float BaseDamage = 10f;
        public float BaseSpeed = 20f;
        public DamageType DamageType = DamageType.Magical;

        [Header("Modify（修正）—— 仅 Kind=Modify 用（默认值为恒等：不改变任何东西）")]
        public float ModDamageAddFlat = 0f;
        public float ModDamageMul = 1f;
        public float ModSpeedMul = 1f;
        public float ModSpreadAddDegrees = 0f;

        [Header("Multicast（多重）—— 仅 Kind=Multicast 用")]
        [Tooltip("本次施法额外增加的投射物预算。双重=1，三重=2")]
        public int ExtraDraws = 0;
    }
}

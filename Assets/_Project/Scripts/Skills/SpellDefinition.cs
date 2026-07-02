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
        [Tooltip("施放音效。一次施法里同一音效只播一次（多重/连发不会叠成多声）。可留空。")]
        public AudioClip CastSfx;
        [Tooltip("payload 释放条件：None=普通投射物；OnImpact=命中时释放后续法术；AfterDelay=存活满指定时间后释放后续法术。仅 Kind=Emit 有意义。")]
        public PayloadTriggerMode PayloadTrigger = PayloadTriggerMode.None;
        [Min(0f)]
        [Tooltip("定时触发延迟秒数。仅 PayloadTrigger=AfterDelay 时使用；应小于投射物 prefab 的 Max Lifetime。")]
        public float PayloadDelaySeconds = 1f;

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

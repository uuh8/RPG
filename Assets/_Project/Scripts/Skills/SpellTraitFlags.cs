using System;

namespace Game.Skills
{
    /// <summary>
    /// 从 SpellDefinition 的权威行为字段推导出的展示事实。
    /// UI 不手填第二套 Traits 文案，避免 Inspector 描述与 CastEvaluator 真实语义漂移。
    /// </summary>
    [Flags]
    public enum SpellTraitFlags : ushort
    {
        None = 0,
        ImpactPayload = 1 << 0,
        TimedPayload = 1 << 1,
        Multicast = 1 << 2,
        StaticProjectile = 1 << 3,
        Shield = 1 << 4,
        DamageModifier = 1 << 5,
        SpeedModifier = 1 << 6,
        Spread = 1 << 7,
        Bounce = 1 << 8,
        Gravity = 1 << 9,
        Homing = 1 << 10,
        Orbit = 1 << 11,
    }

    public static class SpellTraitResolver
    {
        public static SpellTraitFlags Resolve(SpellDefinition spell)
        {
            if (spell == null)
                return SpellTraitFlags.None;

            SpellTraitFlags flags = SpellTraitFlags.None;
            if (spell.PayloadTrigger == PayloadTriggerMode.OnImpact)
                flags |= SpellTraitFlags.ImpactPayload;
            else if (spell.PayloadTrigger == PayloadTriggerMode.AfterDelay)
                flags |= SpellTraitFlags.TimedPayload;

            if (spell.Kind == SpellKind.Multicast)
                flags |= SpellTraitFlags.Multicast;
            if (spell.Kind == SpellKind.StaticProjectile)
                flags |= SpellTraitFlags.StaticProjectile;
            if (spell.SpawnMode == SpellSpawnMode.StaticAtPoint)
                flags |= SpellTraitFlags.Shield;
            if (spell.ModDamageAddFlat != 0f || spell.ModDamageMul != 1f)
                flags |= SpellTraitFlags.DamageModifier;
            if (spell.ModSpeedMul != 1f)
                flags |= SpellTraitFlags.SpeedModifier;
            if (spell.ModSpreadAddDegrees != 0f)
                flags |= SpellTraitFlags.Spread;
            if (spell.ModBounceAdd > 0)
                flags |= SpellTraitFlags.Bounce;
            if (spell.ModUseGravity)
                flags |= SpellTraitFlags.Gravity;
            if (spell.ModHomingRadius > 0f)
                flags |= SpellTraitFlags.Homing;
            if (spell.ModOrbitRadius > 0f)
                flags |= SpellTraitFlags.Orbit;
            return flags;
        }
    }
}

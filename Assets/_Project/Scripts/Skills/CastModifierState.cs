namespace Game.Skills
{
    /// <summary>
    /// 求值过程中"当前累积的修正"。求值器从左到右读取修正法术时更新它；产出投射物时把它快照进 EmitCommand。
    /// readonly struct：按值传递、零 GC、可作为递归求值的"继承起点"（后期触发用）。
    /// </summary>
    public readonly struct CastModifierState
    {
        public readonly float DamageAddFlat; // 平铺加伤（先加）
        public readonly float DamageMul;     // 伤害倍率（后乘）
        public readonly float SpeedMul;      // 速度倍率
        public readonly float SpreadDegrees; // 散射角度（扇形半角，度）

        public CastModifierState(float damageAddFlat, float damageMul, float speedMul, float spreadDegrees)
        {
            DamageAddFlat = damageAddFlat;
            DamageMul = damageMul;
            SpeedMul = speedMul;
            SpreadDegrees = spreadDegrees;
        }

        /// <summary>初始（恒等）状态：加伤 0、倍率 1、散射 0。乘法用 1 作单位元，保证"只改伤害的修正"不影响速度。</summary>
        public static CastModifierState Default => new CastModifierState(0f, 1f, 1f, 0f);

        /// <summary>把一个 Modify 法术叠加到当前状态，返回新状态（不可变）。加法项相加、乘法项相乘、散射相加。</summary>
        public CastModifierState Apply(SpellDefinition modify)
        {
            return new CastModifierState(
                DamageAddFlat + modify.ModDamageAddFlat,
                DamageMul * modify.ModDamageMul,
                SpeedMul * modify.ModSpeedMul,
                SpreadDegrees + modify.ModSpreadAddDegrees);
        }
    }
}

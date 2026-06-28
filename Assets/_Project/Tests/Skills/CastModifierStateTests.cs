using NUnit.Framework;
using UnityEngine;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class CastModifierStateTests
    {
        private static SpellDefinition Modify(float dmgMul = 1f, float speedMul = 1f, float dmgAdd = 0f, float spread = 0f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Modify;
            s.ModDamageMul = dmgMul;
            s.ModSpeedMul = speedMul;
            s.ModDamageAddFlat = dmgAdd;
            s.ModSpreadAddDegrees = spread;
            return s;
        }

        [Test]
        public void Default_IsIdentity()
        {
            var d = CastModifierState.Default;
            Assert.AreEqual(0f, d.DamageAddFlat, 1e-4f);
            Assert.AreEqual(1f, d.DamageMul, 1e-4f);
            Assert.AreEqual(1f, d.SpeedMul, 1e-4f);
            Assert.AreEqual(0f, d.SpreadDegrees, 1e-4f);
        }

        [Test]
        public void Apply_DamageMul_Multiplies_LeavesSpeedUnchanged()
        {
            var s = CastModifierState.Default.Apply(Modify(dmgMul: 1.5f));
            Assert.AreEqual(1.5f, s.DamageMul, 1e-4f);
            Assert.AreEqual(1f, s.SpeedMul, 1e-4f);   // 恒等保持：只改伤害不影响速度
        }

        [Test]
        public void Apply_Twice_AccumulatesMultiplicatively()
        {
            var s = CastModifierState.Default.Apply(Modify(dmgMul: 2f)).Apply(Modify(dmgMul: 2f));
            Assert.AreEqual(4f, s.DamageMul, 1e-4f);
        }

        [Test]
        public void Apply_SpreadAndFlat_AreAdditive()
        {
            var s = CastModifierState.Default.Apply(Modify(dmgAdd: 5f, spread: 15f)).Apply(Modify(dmgAdd: 5f, spread: 15f));
            Assert.AreEqual(10f, s.DamageAddFlat, 1e-4f);
            Assert.AreEqual(30f, s.SpreadDegrees, 1e-4f);
        }
    }
}

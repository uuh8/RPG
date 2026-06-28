using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Game.Skills;
using Game.Combat;

namespace Game.Skills.Tests
{
    public class CastEvaluatorTests
    {
        private readonly List<EmitCommand> _out = new List<EmitCommand>();

        // ── 构造测试用法术 ──
        private static SpellDefinition Emit(float dmg = 10f, float speed = 20f, float mana = 0f)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Emit;
            s.BaseDamage = dmg; s.BaseSpeed = speed; s.DamageType = DamageType.Magical; s.ManaCost = mana;
            return s;
        }
        private static SpellDefinition DamageMod(float mul)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Modify; s.ModDamageMul = mul;
            return s;
        }
        private static SpellDefinition Multi(int extra)
        {
            var s = ScriptableObject.CreateInstance<SpellDefinition>();
            s.Kind = SpellKind.Multicast; s.ExtraDraws = extra;
            return s;
        }

        private CastSummary Run(int baseDraws, float mana, params SpellDefinition[] wand)
            => CastEvaluator.Evaluate(wand, baseDraws, mana, CastModifierState.Default, _out);

        [Test]
        public void EmptyWand_EmitsNothing()
        {
            Run(1, 999f);
            Assert.AreEqual(0, _out.Count);
        }

        [Test]
        public void SingleFireball_EmitsOne_WithBaseValues()
        {
            Run(1, 999f, Emit(dmg: 15f, speed: 20f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(15f, _out[0].Damage, 1e-4f);
            Assert.AreEqual(20f, _out[0].Speed, 1e-4f);
        }

        [Test]
        public void DamageMod_BeforeEmit_BoostsIt()
        {
            Run(1, 999f, DamageMod(1.5f), Emit(dmg: 15f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(22.5f, _out[0].Damage, 1e-4f);
        }

        [Test]
        public void DamageMod_AfterEmit_DoesNotBoostIt()
        {
            Run(1, 999f, Emit(dmg: 15f), DamageMod(1.5f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(15f, _out[0].Damage, 1e-4f); // 修正只影响其后
        }

        [Test]
        public void BaseDraws_LimitsEmits()
        {
            Run(1, 999f, Emit(), Emit());          // 预算 1 → 只产出第一发
            Assert.AreEqual(1, _out.Count);

            Run(2, 999f, Emit(), Emit());          // 预算 2 → 两发都产出
            Assert.AreEqual(2, _out.Count);
        }
    }
}

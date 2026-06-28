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

        [Test]
        public void Triple_WithThreeProjectiles_EmitsThree()
        {
            Run(1, 999f, Multi(2), Emit(), Emit(), Emit()); // 预算 1+2=3
            Assert.AreEqual(3, _out.Count);
        }

        [Test]
        public void Triple_DamageMod_AppliesToAllEmits()
        {
            Run(1, 999f, Multi(2), DamageMod(2f), Emit(dmg: 10f), Emit(dmg: 10f), Emit(dmg: 10f));
            Assert.AreEqual(3, _out.Count);
            foreach (var e in _out)
                Assert.AreEqual(20f, e.Damage, 1e-4f); // 多重内修正一次成本、作用于全部
        }

        [Test]
        public void Multicast_BudgetUnfilled_IsDiscarded_NoWrap()
        {
            Run(1, 999f, Multi(2), Emit()); // 预算 3 但只有 1 个投射物 → 产出 1（单遍不回绕，余量作废）
            Assert.AreEqual(1, _out.Count);
        }

        [Test]
        public void Multicast_AfterEmit_ReopensBudget()
        {
            Run(1, 999f, Emit(), Multi(2), Emit()); // 1→产出#1(预算0)→+2(预算2)→产出#2
            Assert.AreEqual(2, _out.Count);
        }

        [Test]
        public void EnoughMana_EmitsAll_ReportsSpent()
        {
            var summary = Run(1, 100f, Multi(2), Emit(mana: 6f), Emit(mana: 6f), Emit(mana: 6f));
            Assert.AreEqual(3, _out.Count);
            Assert.IsFalse(summary.Fizzled);
            Assert.AreEqual(18f, summary.ManaSpent, 1e-4f);
        }

        [Test]
        public void InsufficientMana_FizzlesMidCast()
        {
            // 可用 10，每发 6：第 1 发后剩 4，第 2 发 6>4 → fizzle
            var summary = Run(1, 10f, Multi(2), Emit(mana: 6f), Emit(mana: 6f), Emit(mana: 6f));
            Assert.AreEqual(1, _out.Count);
            Assert.IsTrue(summary.Fizzled);
            Assert.AreEqual(6f, summary.ManaSpent, 1e-4f);
        }

        [Test]
        public void ZeroCostSpells_NeverFizzle()
        {
            var summary = Run(1, 0f, Emit(mana: 0f));
            Assert.AreEqual(1, _out.Count);
            Assert.IsFalse(summary.Fizzled);
        }

        // 通用修正构造（覆盖 增伤/加速/平铺加伤/散射 四类），用于验证 BakeEmit 端到端写入
        private static SpellDefinition Mod(float dmgMul = 1f, float speedMul = 1f, float dmgAdd = 0f, float spread = 0f)
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
        public void Modifiers_BakeSpeedSpreadAndFlatDamage_IntoEmit()
        {
            // 伤害 = (10 + 5) * 2 = 30；速度 = 20 * 1.5 = 30；散射 = 15
            Run(1, 999f, Mod(dmgMul: 2f, speedMul: 1.5f, dmgAdd: 5f, spread: 15f), Emit(dmg: 10f, speed: 20f));
            Assert.AreEqual(1, _out.Count);
            Assert.AreEqual(30f, _out[0].Damage, 1e-4f);
            Assert.AreEqual(30f, _out[0].Speed, 1e-4f);
            Assert.AreEqual(15f, _out[0].SpreadDegrees, 1e-4f);
        }

        [Test]
        public void NullSpellEntry_IsSkipped()
        {
            Run(1, 999f, null, Emit(dmg: 10f));
            Assert.AreEqual(1, _out.Count);
        }
    }
}

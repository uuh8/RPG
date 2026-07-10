using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Game.Combat;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class CastTraceTests
    {
        [Test]
        public void Collector_RecordAndClear_ReusesLogicalBuffer()
        {
            var collector = new CastTraceCollector(4);
            var step = new CastTraceStep(
                CastTraceStepKind.CastStarted,
                -1,
                null,
                1,
                1,
                10f,
                10f,
                CastModifierState.Default,
                CastModifierState.Default);

            collector.Record(step);
            Assert.AreEqual(1, collector.Count);
            Assert.AreEqual(CastTraceStepKind.CastStarted, collector[0].Kind);

            collector.Clear();
            Assert.AreEqual(0, collector.Count);
            Assert.IsFalse(collector.ExpandedDuringLastCollection);
        }

        [Test]
        public void Collector_Constructor_ClampsNegativeCapacity()
        {
            Assert.DoesNotThrow(() => new CastTraceCollector(-10));
        }

        [Test]
        public void Collector_WhenCapacityIsExceeded_ReportsExpansion()
        {
            var collector = new CastTraceCollector(1);
            var step = new CastTraceStep(
                CastTraceStepKind.CastStarted, -1, null,
                1, 1, 10f, 10f,
                CastModifierState.Default, CastModifierState.Default);

            collector.Record(step);
            collector.Record(step);

            Assert.IsTrue(collector.ExpandedDuringLastCollection);
        }

        [Test]
        public void EvaluateWithTrace_SingleEmit_RecordsStartEmitAndComplete()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new[] { Emit(15f) }, 1, 100f,
                CastModifierState.Default, output, trace);

            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(3, trace.Count);
            Assert.AreEqual(CastTraceStepKind.CastStarted, trace[0].Kind);
            Assert.AreEqual(CastTraceStepKind.EmitProduced, trace[1].Kind);
            Assert.AreEqual(15f, trace[1].Emit.Damage, 1e-4f);
            Assert.AreEqual(CastTraceStepKind.CastCompleted, trace[2].Kind);
        }

        [Test]
        public void EvaluateWithTrace_NullSpell_RecordsSkippedSlot()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new SpellDefinition[] { null, Emit() }, 1, 100f,
                CastModifierState.Default, output, trace);

            Assert.AreEqual(4, trace.Count);
            Assert.AreEqual(CastTraceStepKind.NullSpellSkipped, trace[1].Kind);
            Assert.AreEqual(0, trace[1].SpellIndex);
        }

        [Test]
        public void EvaluateWithTrace_ModifyAndMulticast_RecordsBeforeAfterState()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new[] { Multicast(2), DamageMod(2f), Emit(10f) },
                1, 100f, CastModifierState.Default, output, trace);

            Assert.AreEqual(CastTraceStepKind.MulticastApplied, trace[1].Kind);
            Assert.AreEqual(1, trace[1].DrawBudgetBefore);
            Assert.AreEqual(3, trace[1].DrawBudgetAfter);
            Assert.AreEqual(CastTraceStepKind.ModifyApplied, trace[2].Kind);
            Assert.AreEqual(1f, trace[2].ModifiersBefore.DamageMul, 1e-4f);
            Assert.AreEqual(2f, trace[2].ModifiersAfter.DamageMul, 1e-4f);
        }

        [Test]
        public void EvaluateWithTrace_ManaFizzle_RecordsFailingIndex()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastSummary summary = CastEvaluator.EvaluateWithTrace(
                new[] { DamageMod(2f, 5f), Emit() },
                1, 3f, CastModifierState.Default, output, trace);

            Assert.IsTrue(summary.Fizzled);
            Assert.AreEqual(CastTraceStepKind.ManaFizzle, trace[1].Kind);
            Assert.AreEqual(0, trace[1].SpellIndex);
        }

        [Test]
        public void EvaluateWithTrace_ZeroBudget_RecordsBlockedEmit()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new[] { Emit() }, 0, 100f,
                CastModifierState.Default, output, trace);

            Assert.AreEqual(0, output.Count);
            Assert.AreEqual(CastTraceStepKind.DrawBudgetBlocked, trace[1].Kind);
        }

        [Test]
        public void EvaluateWithTrace_Trigger_RecordsPayloadCapture()
        {
            var output = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastEvaluator.EvaluateWithTrace(
                new[] { Emit(trigger: PayloadTriggerMode.OnImpact), Emit() },
                1, 100f, CastModifierState.Default, output, trace);

            Assert.AreEqual(CastTraceStepKind.PayloadCaptured, trace[2].Kind);
            Assert.AreEqual(1, trace[2].PayloadStartIndex);
            Assert.AreEqual(1, trace[2].PayloadCount);
        }

        [Test]
        public void EvaluateWithTrace_NullCollector_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() =>
                CastEvaluator.EvaluateWithTrace(
                    new[] { Emit() }, 1, 100f,
                    CastModifierState.Default, new List<EmitCommand>(), null));
        }

        [Test]
        public void EvaluateAndEvaluateWithTrace_ProduceEquivalentCommands()
        {
            SpellDefinition[] spells =
            {
                Multicast(2), DamageMod(1.5f),
                Emit(10f), Emit(12f), Emit(14f)
            };
            var normal = new List<EmitCommand>();
            var traced = new List<EmitCommand>();
            var trace = new CastTraceCollector();

            CastSummary normalSummary = CastEvaluator.Evaluate(
                spells, 1, 100f, CastModifierState.Default, normal);
            CastSummary tracedSummary = CastEvaluator.EvaluateWithTrace(
                spells, 1, 100f, CastModifierState.Default, traced, trace);

            Assert.AreEqual(normalSummary.ManaSpent, tracedSummary.ManaSpent, 1e-4f);
            Assert.AreEqual(normalSummary.Fizzled, tracedSummary.Fizzled);
            Assert.AreEqual(normal.Count, traced.Count);

            for (int i = 0; i < normal.Count; i++)
            {
                Assert.AreEqual(normal[i].Damage, traced[i].Damage, 1e-4f);
                Assert.AreEqual(normal[i].Speed, traced[i].Speed, 1e-4f);
                Assert.AreEqual(normal[i].SpawnMode, traced[i].SpawnMode);
                Assert.AreEqual(normal[i].MotionMode, traced[i].MotionMode);
                Assert.AreEqual(normal[i].PayloadTrigger, traced[i].PayloadTrigger);
            }
        }

        private static SpellDefinition Emit(
            float damage = 10f,
            float mana = 0f,
            PayloadTriggerMode trigger = PayloadTriggerMode.None)
        {
            var spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.Kind = SpellKind.Emit;
            spell.DisplayName = "Test Fireball";
            spell.BaseDamage = damage;
            spell.BaseSpeed = 20f;
            spell.DamageType = DamageType.Magical;
            spell.ManaCost = mana;
            spell.PayloadTrigger = trigger;
            return spell;
        }

        private static SpellDefinition DamageMod(float multiplier, float mana = 0f)
        {
            var spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.Kind = SpellKind.Modify;
            spell.DisplayName = "Test Damage Modifier";
            spell.ModDamageMul = multiplier;
            spell.ManaCost = mana;
            return spell;
        }

        private static SpellDefinition Multicast(int extraDraws)
        {
            var spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.Kind = SpellKind.Multicast;
            spell.DisplayName = "Test Multicast";
            spell.ExtraDraws = extraDraws;
            return spell;
        }
    }
}

using NUnit.Framework;
using UnityEngine;
using Game.Combat;

namespace Game.Combat.Tests
{
    public class StatusControllerTests
    {
        private static StatusDefinition Def(StatusKind kind, float decay = 0f)
        {
            StatusDefinition definition = ScriptableObject.CreateInstance<StatusDefinition>();
            definition.Kind = kind;
            definition.DisplayName = kind.ToString();
            definition.NaturalDecayPerSecond = decay;
            return definition;
        }

        private static StatusDefinition StickyDef()
        {
            StatusDefinition definition = Def(StatusKind.Sticky);
            definition.AffectsMoveSpeed = true;
            definition.MaxMoveSpeedSlowRatio = 0.4f;
            return definition;
        }

        private static StatusController Controller(params StatusDefinition[] definitions)
        {
            GameObject go = new GameObject("status-test");
            StatusController controller = go.AddComponent<StatusController>();
            controller.SetDefinitionsForTests(definitions);
            return controller;
        }

        [Test]
        public void ApplyStatus_AddsIntensityAndClampsTo100()
        {
            StatusController controller = Controller(Def(StatusKind.Burning));

            controller.ApplyStatus(StatusKind.Burning, 40f, 10, 1);
            controller.ApplyStatus(StatusKind.Burning, 70f, 10, 1);

            Assert.IsTrue(controller.HasStatus(StatusKind.Burning));
            Assert.AreEqual(100f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void Tick_ReducesIntensityByNaturalDecay()
        {
            StatusController controller = Controller(Def(StatusKind.Burning, 8f));

            controller.ApplyStatus(StatusKind.Burning, 100f, 10, 1);
            controller.TickForTests(1f);

            Assert.AreEqual(92f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void Tick_RemovesStatusWhenIntensityReachesZero()
        {
            StatusController controller = Controller(Def(StatusKind.Burning, 10f));

            controller.ApplyStatus(StatusKind.Burning, 5f, 10, 1);
            controller.TickForTests(1f);

            Assert.IsFalse(controller.HasStatus(StatusKind.Burning));
            Assert.AreEqual(0f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void Wet_AcceleratesBurningDecay()
        {
            StatusController controller = Controller(
                Def(StatusKind.Burning, 8f),
                Def(StatusKind.Wet));

            controller.ApplyStatus(StatusKind.Burning, 100f, 10, 1);
            controller.ApplyStatus(StatusKind.Wet, 100f, 10, 1);
            controller.TickForTests(1f);

            Assert.AreEqual(57f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void Sticky_ProducesMoveSpeedMultiplier()
        {
            StatusController controller = Controller(StickyDef());

            controller.ApplyStatus(StatusKind.Sticky, 100f, 10, 1);
            Assert.AreEqual(0.6f, controller.MoveSpeedMultiplier, 1e-4f);
            Object.DestroyImmediate(controller.gameObject);

            controller = Controller(StickyDef());
            controller.ApplyStatus(StatusKind.Sticky, 50f, 10, 1);
            Assert.AreEqual(0.8f, controller.MoveSpeedMultiplier, 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
        }

        [Test]
        public void Poisoned_DealsDotWithoutHitReaction()
        {
            GameObject go = new GameObject("poison-target");
            HealthComponent health = go.AddComponent<HealthComponent>();
            StatusController controller = go.AddComponent<StatusController>();

            StatusDefinition poison = Def(StatusKind.Poisoned);
            poison.DealsDamage = true;
            poison.DamageInterval = 1f;
            poison.BaseDamagePerTick = 4f;
            poison.DamageType = DamageType.Magical;
            poison.TriggerHitReaction = false;

            controller.SetDefinitionsForTests(poison);
            controller.ApplyStatus(StatusKind.Poisoned, 100f, 99, 2);

            float before = health.CurrentHp;
            controller.TickForTests(1f);

            Assert.AreEqual(before - 4f, health.CurrentHp, 1e-4f);
            Object.DestroyImmediate(go);
        }
    }
}

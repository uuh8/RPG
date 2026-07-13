using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Game.Combat;
using Game.Core;

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

        private static ElementReactionProfile ReactionProfile()
        {
            ElementReactionProfile profile = ScriptableObject.CreateInstance<ElementReactionProfile>();
            profile.Extinguish = new ExtinguishTuning
            {
                FormalThreshold = 10f,
                FormalRatePerSecond = 50f,
                LowRatePerSecond = 10f,
            };
            profile.WetCleanse = new WetCleanseTuning { BudgetPerSecondAtFullWet = 100f };
            profile.ToxicCombustion = new ToxicCombustionTuning
            {
                FireThreshold = 25f,
                PoisonThreshold = 20f,
                WindUpSeconds = 0.2f,
                MaxPoisonConsume = 40f,
                MinEffectiveConsume = 5f,
                BaseDamage = 8f,
                DamagePerPoison = 0.4f,
                Radius = 2.5f,
                CooldownSeconds = 1f,
            };
            profile.IgniteGoo = new IgniteGooTuning
            {
                FireThreshold = 20f,
                GooThreshold = 10f,
                WetInhibitThreshold = 10f,
                RequiredFireCapacity = 10f,
                GooConsumePerSecond = 40f,
                FirePerGoo = 1.25f,
                CooldownSeconds = 0.5f,
            };
            return profile;
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
        public void Reaction_ApplyOnlyStartsThenTickContinuouslyExtinguishes()
        {
            ElementReactionProfile profile = ReactionProfile();
            StatusController controller = Controller(
                Def(StatusKind.Burning),
                Def(StatusKind.Wet));
            controller.SetReactionProfileForTests(profile);

            controller.ApplyStatus(StatusKind.Burning, 80f, 10, 1);
            controller.ApplyStatus(StatusKind.Wet, 30f, 11, 2);

            Assert.AreEqual(80f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Assert.AreEqual(30f, controller.GetIntensity(StatusKind.Wet), 1e-4f);

            controller.TickForTests(0.6f);

            Assert.AreEqual(50f, controller.GetIntensity(StatusKind.Burning), 1e-4f);
            Assert.AreEqual(0f, controller.GetIntensity(StatusKind.Wet), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void WetCleanse_UsesDefinitionMultipliersAndPrioritizesPoison()
        {
            ElementReactionProfile profile = ReactionProfile();
            StatusDefinition poison = Def(StatusKind.Poisoned);
            poison.WetCleanseable = true;
            poison.WetCleanseMultiplier = 0.75f;
            StatusDefinition sticky = StickyDef();
            sticky.WetCleanseable = true;
            sticky.WetCleanseMultiplier = 1.25f;
            StatusController controller = Controller(Def(StatusKind.Wet), poison, sticky);
            controller.SetReactionProfileForTests(profile);
            controller.ApplyStatus(StatusKind.Wet, 40f, 1, 1);
            controller.ApplyStatus(StatusKind.Poisoned, 30f, 2, 2);
            controller.ApplyStatus(StatusKind.Sticky, 30f, 3, 3);

            controller.TickForTests(1f);

            Assert.AreEqual(0f, controller.GetIntensity(StatusKind.Poisoned), 1e-4f);
            Assert.AreEqual(30f, controller.GetIntensity(StatusKind.Sticky), 1e-4f);
            Object.DestroyImmediate(controller.gameObject);
            Object.DestroyImmediate(profile);
        }

        [Test]
        public void ReactionSignal_IsPublishedAsDiscreteEvent()
        {
            ElementReactionProfile profile = ReactionProfile();
            StatusController controller = Controller(Def(StatusKind.Burning), Def(StatusKind.Wet));
            controller.SetReactionProfileForTests(profile);
            int eventCount = 0;
            ElementReactionEvent received = default;
            void OnReaction(ElementReactionEvent e)
            {
                eventCount++;
                received = e;
            }

            EventBus<ElementReactionEvent>.Subscribe(OnReaction);
            controller.ApplyStatus(StatusKind.Burning, 80f, 10, 1);
            controller.ApplyStatus(StatusKind.Wet, 30f, 11, 2);
            controller.TickForTests(0.1f);
            controller.TickForTests(0.1f);
            EventBus<ElementReactionEvent>.Unsubscribe(OnReaction);

            Assert.AreEqual(1, eventCount, "应恰好收到一次离散 Started 事件");
            Assert.AreEqual(controller.gameObject.GetInstanceID(), received.TargetId);
            Assert.AreEqual(ElementReactionId.Extinguish, received.Reaction);
            Assert.AreEqual(ElementReactionPhase.Started, received.Phase);
            Object.DestroyImmediate(controller.gameObject);
            Object.DestroyImmediate(profile);
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

        [UnityTest]
        public IEnumerator Poisoned_DealsDotWithoutHitReaction()
        {
            yield return new EnterPlayMode();

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
            float after = health.CurrentHp;

            Object.Destroy(go);
            Object.Destroy(poison);
            yield return null;
            yield return new ExitPlayMode();

            Assert.AreEqual(before - 4f, after, 1e-4f);
        }
    }
}

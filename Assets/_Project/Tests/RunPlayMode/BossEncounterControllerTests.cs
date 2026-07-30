using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    public sealed class BossEncounterControllerTests
    {
        private readonly List<Object> _createdObjects = new List<Object>();
        private int _startedCount;
        private int _runStateChangedCount;
        private RunState _lastRunState;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            EventBus<DeathEvent>.Clear();
            EventBus<BossEncounterStartedEvent>.Clear();
            EventBus<RunStateChangedEvent>.Clear();
            _startedCount = 0;
            _runStateChangedCount = 0;
            EventBus<BossEncounterStartedEvent>.Subscribe(OnStarted);
            EventBus<RunStateChangedEvent>.Subscribe(OnRunStateChanged);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            EventBus<DeathEvent>.Clear();
            EventBus<BossEncounterStartedEvent>.Clear();
            EventBus<RunStateChangedEvent>.Clear();
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                {
                    Object.Destroy(_createdObjects[i]);
                }
            }

            _createdObjects.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator TryBegin_PublishesOnceAndIgnoresExternalDeath()
        {
            HealthComponent bossHealth = CreateHealth("Boss");
            HealthComponent playerHealth = CreateHealth("Player");
            BossEncounterController controller =
                CreateController(bossHealth, playerHealth);

            Assert.That(controller.TryBegin(), Is.True);
            Assert.That(controller.TryBegin(), Is.False);
            Assert.That(_startedCount, Is.EqualTo(1));
            Assert.That(controller.State, Is.EqualTo(BossEncounterState.Running));

            EventBus<DeathEvent>.Publish(new DeathEvent
            {
                TargetId = 999999
            });
            yield return null;

            Assert.That(controller.State, Is.EqualTo(BossEncounterState.Running));
            Assert.That(_runStateChangedCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator BossDeath_CompletesExactlyOnce()
        {
            HealthComponent bossHealth = CreateHealth("Boss");
            HealthComponent playerHealth = CreateHealth("Player");
            BossEncounterController controller =
                CreateController(bossHealth, playerHealth);
            controller.TryBegin();

            PublishDeath(bossHealth.Id);
            PublishDeath(bossHealth.Id);
            yield return null;

            Assert.That(
                controller.State,
                Is.EqualTo(BossEncounterState.Completed));
            Assert.That(_runStateChangedCount, Is.EqualTo(1));
            Assert.That(_lastRunState, Is.EqualTo(RunState.Completed));
        }

        [UnityTest]
        public IEnumerator PlayerDeath_FailsExactlyOnce()
        {
            HealthComponent bossHealth = CreateHealth("Boss");
            HealthComponent playerHealth = CreateHealth("Player");
            BossEncounterController controller =
                CreateController(bossHealth, playerHealth);
            controller.TryBegin();

            PublishDeath(playerHealth.Id);
            PublishDeath(bossHealth.Id);
            yield return null;

            Assert.That(
                controller.State,
                Is.EqualTo(BossEncounterState.Failed));
            Assert.That(_runStateChangedCount, Is.EqualTo(1));
            Assert.That(_lastRunState, Is.EqualTo(RunState.Failed));
        }

        [UnityTest]
        public IEnumerator SafeZoneEnter_OnlyArmsWithoutStartingEncounter()
        {
            BossEntryTrigger entryTrigger = CreateEntryTrigger(
                out BossEncounterController encounter,
                out Collider playerCollider,
                out Collider boundary,
                out _,
                out _);
            yield return null;

            entryTrigger.SendMessage(
                "OnTriggerEnter",
                playerCollider,
                SendMessageOptions.RequireReceiver);

            Assert.That(
                encounter.State,
                Is.EqualTo(BossEncounterState.Inactive),
                "玩家出生在 Safe Zone 内时只能完成布防，不能立刻启动 Boss 战。");
            Assert.That(boundary.enabled, Is.True);
        }

        [UnityTest]
        public IEnumerator SafeZoneExit_StartsOnceThenLetsParticlesExpire()
        {
            BossEntryTrigger entryTrigger = CreateEntryTrigger(
                out BossEncounterController encounter,
                out Collider playerCollider,
                out Collider boundary,
                out GameObject visualRoot,
                out ParticleSystem particles);
            yield return null;
            particles.Emit(1);

            entryTrigger.SendMessage(
                "OnTriggerEnter",
                playerCollider,
                SendMessageOptions.RequireReceiver);
            entryTrigger.SendMessage(
                "OnTriggerExit",
                playerCollider,
                SendMessageOptions.RequireReceiver);

            Assert.That(encounter.State, Is.EqualTo(BossEncounterState.Running));
            Assert.That(boundary.enabled, Is.False,
                "成功出圈后应立即关闭 Trigger，避免多个 Player Collider 重复启动。");
            Assert.That(particles.isEmitting, Is.False,
                "出圈后只停止发射新粒子，不能立即 Clear 已有粒子。");
            Assert.That(visualRoot.activeSelf, Is.True,
                "已有粒子尚未结束时，视觉根节点必须保持 active。");

            entryTrigger.SendMessage(
                "OnTriggerExit",
                playerCollider,
                SendMessageOptions.RequireReceiver);
            yield return new WaitForSeconds(0.15f);

            Assert.That(encounter.State, Is.EqualTo(BossEncounterState.Running));
            Assert.That(visualRoot.activeSelf, Is.False,
                "全部粒子自然死亡后才隐藏 Safe Zone 视觉。");
        }

        private BossEncounterController CreateController(
            HealthComponent bossHealth,
            HealthComponent playerHealth)
        {
            GameObject controllerObject =
                new GameObject("Boss Encounter Controller");
            controllerObject.SetActive(false);
            _createdObjects.Add(controllerObject);
            BossEncounterController controller =
                controllerObject.AddComponent<BossEncounterController>();
            SetPrivateField(controller, "_bossHealth", bossHealth);
            SetPrivateField(controller, "_playerHealth", playerHealth);
            controllerObject.SetActive(true);
            return controller;
        }

        private HealthComponent CreateHealth(string objectName)
        {
            GameObject target = new GameObject(objectName);
            _createdObjects.Add(target);
            return target.AddComponent<HealthComponent>();
        }

        private BossEntryTrigger CreateEntryTrigger(
            out BossEncounterController encounter,
            out Collider playerCollider,
            out Collider boundary,
            out GameObject visualRoot,
            out ParticleSystem particles)
        {
            HealthComponent bossHealth = CreateHealth("Boss");
            HealthComponent playerHealth = CreateHealth("Player Health");
            encounter = CreateController(bossHealth, playerHealth);

            SpellDefinition spell =
                ScriptableObject.CreateInstance<SpellDefinition>();
            spell.DisplayName = "Safe Zone Test Spell";
            _createdObjects.Add(spell);
            SpellLibrary library =
                ScriptableObject.CreateInstance<SpellLibrary>();
            library.Available = new[] { spell };
            _createdObjects.Add(library);
            WandLoadout wand =
                ScriptableObject.CreateInstance<WandLoadout>();
            wand.Spells = new[] { spell };
            wand.BaseDraws = 1;
            _createdObjects.Add(wand);

            GameObject sessionObject = new GameObject("P8 Run Session Root");
            sessionObject.SetActive(false);
            _createdObjects.Add(sessionObject);
            RunSpellSession session =
                sessionObject.AddComponent<RunSpellSession>();
            session.ConfigureAndInitialize(library, wand);
            RunSessionCoordinator coordinator =
                sessionObject.AddComponent<RunSessionCoordinator>();
            sessionObject.SetActive(true);

            GameObject playerObject = new GameObject("Player");
            playerObject.layer = 8;
            _createdObjects.Add(playerObject);
            playerCollider = playerObject.AddComponent<CapsuleCollider>();

            GameObject triggerObject = new GameObject("Boss Safe Zone Root");
            triggerObject.SetActive(false);
            _createdObjects.Add(triggerObject);
            boundary = triggerObject.AddComponent<CapsuleCollider>();
            boundary.isTrigger = true;
            BossEntryTrigger entryTrigger =
                triggerObject.AddComponent<BossEntryTrigger>();

            visualRoot = new GameObject("Safe Zone Visual");
            visualRoot.transform.SetParent(triggerObject.transform, false);
            particles = visualRoot.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = particles.main;
            main.loop = true;
            main.startLifetime = 0.05f;
            main.startSpeed = 0f;

            SetPrivateField(entryTrigger, "_encounter", encounter);
            SetPrivateField(entryTrigger, "_sessionCoordinator", coordinator);
            SetPrivateField(entryTrigger, "_playerLayers", (LayerMask)(1 << 8));
            SetPrivateField(
                entryTrigger,
                "_safeZoneVisualRoot",
                visualRoot.transform);
            triggerObject.SetActive(true);
            return entryTrigger;
        }

        private static void PublishDeath(int targetId)
        {
            EventBus<DeathEvent>.Publish(new DeathEvent
            {
                TargetId = targetId
            });
        }

        private void OnStarted(BossEncounterStartedEvent startedEvent)
        {
            _startedCount++;
        }

        private void OnRunStateChanged(RunStateChangedEvent stateEvent)
        {
            _runStateChangedCount++;
            _lastRunState = stateEvent.CurrentState;
        }

        private static void SetPrivateField(
            object target,
            string fieldName,
            object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }
    }
}

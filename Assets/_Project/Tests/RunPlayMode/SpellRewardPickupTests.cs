using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// 验证 Encounter 完成事件、场景奖励表现与 RunSpellSession 之间的连接。
    /// 这里使用 PlayMode，是因为 Awake / OnEnable / Trigger 都属于 Unity 生命周期行为。
    /// </summary>
    public sealed class SpellRewardPickupTests
    {
        private readonly List<UnityEngine.Object> _createdObjects =
            new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            EventBus<EncounterCompletedEvent>.Clear();
            EventBus<SpellRewardGrantedEvent>.Clear();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                UnityEngine.Object createdObject = _createdObjects[i];
                if (createdObject == null)
                {
                    continue;
                }

                if (createdObject is GameObject gameObject)
                {
                    gameObject.SetActive(false);
                }

                UnityEngine.Object.Destroy(createdObject);
            }

            _createdObjects.Clear();
            yield return null;

            EventBus<EncounterCompletedEvent>.Clear();
            EventBus<SpellRewardGrantedEvent>.Clear();
        }

        [UnityTest]
        public IEnumerator CompletionForOtherEncounter_KeepsPickupUnavailable()
        {
            SpellRewardPickup pickup = CreatePickup(encounterIndex: 1, out Collider trigger, out GameObject visual);

            EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent
            {
                EncounterIndex = 0,
                EncounterCount = 2,
                RewardSpell = CreateSpell("Fire")
            });

            Assert.That(pickup.IsAvailable, Is.False);
            Assert.That(trigger.enabled, Is.False);
            Assert.That(visual.activeSelf, Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator MatchingCompletion_RevealsPickupAndGrantsRewardToRuntimeSession()
        {
            SpellDefinition fire = CreateSpell("Fire");
            SpellDefinition water = CreateSpell("Water");
            RunSpellSession session = CreateSession(fire);
            SpellRewardPickup pickup = CreatePickup(encounterIndex: 1, out Collider trigger, out GameObject visual);
            SetPrivateField(pickup, "_session", session);

            EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent
            {
                EncounterIndex = 1,
                EncounterCount = 2,
                RewardSpell = water
            });

            Assert.That(pickup.IsAvailable, Is.True);
            Assert.That(trigger.enabled, Is.True);
            Assert.That(visual.activeSelf, Is.True);

            GameObject player = CreateGameObject("Player");
            player.layer = 8;
            Collider playerCollider = player.AddComponent<BoxCollider>();
            SetPrivateField(pickup, "_playerLayers", (LayerMask)(1 << player.layer));

            InvokePrivate(pickup, "OnTriggerEnter", playerCollider);

            Assert.That(session.RuntimeLibrary.Available, Is.EqualTo(new[] { fire, water }));
            Assert.That(pickup.IsConsumed, Is.True);
            Assert.That(trigger.enabled, Is.False);
            Assert.That(visual.activeSelf, Is.False);
            yield return null;
        }

        private RunSpellSession CreateSession(SpellDefinition startingSpell)
        {
            SpellLibrary library = ScriptableObject.CreateInstance<SpellLibrary>();
            library.Available = new[] { startingSpell };
            _createdObjects.Add(library);

            WandLoadout wand = ScriptableObject.CreateInstance<WandLoadout>();
            wand.Spells = new[] { startingSpell };
            wand.BaseDraws = 1;
            _createdObjects.Add(wand);

            GameObject sessionObject = CreateGameObject("Run Spell Session");
            sessionObject.SetActive(false);
            RunSpellSession session = sessionObject.AddComponent<RunSpellSession>();
            session.ConfigureAndInitialize(library, wand);
            return session;
        }

        private SpellRewardPickup CreatePickup(
            int encounterIndex,
            out Collider trigger,
            out GameObject visual)
        {
            GameObject pickupObject = CreateGameObject("Spell Reward Pickup");
            pickupObject.SetActive(false);
            trigger = pickupObject.AddComponent<SphereCollider>();
            trigger.isTrigger = true;

            visual = CreateGameObject("Visual", pickupObject.transform);
            SpellRewardPickup pickup = pickupObject.AddComponent<SpellRewardPickup>();
            SetPrivateField(pickup, "_encounterIndex", encounterIndex);
            SetPrivateField(pickup, "_visualRoot", visual);
            pickupObject.SetActive(true);
            return pickup;
        }

        private SpellDefinition CreateSpell(string displayName)
        {
            SpellDefinition spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.DisplayName = displayName;
            _createdObjects.Add(spell);
            return spell;
        }

        private GameObject CreateGameObject(string objectName, Transform parent = null)
        {
            GameObject gameObject = new GameObject(objectName);
            if (parent != null)
            {
                gameObject.transform.SetParent(parent, false);
            }

            _createdObjects.Add(gameObject);
            return gameObject;
        }

        private static void SetPrivateField<T>(object target, string fieldName, T value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"找不到测试字段 {fieldName}。");
            field.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"找不到测试方法 {methodName}。");
            method.Invoke(target, arguments);
        }
    }
}

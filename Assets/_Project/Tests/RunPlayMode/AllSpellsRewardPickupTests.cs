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
    /// 全法术奖励是地图一毕业奖励：由最终 Encounter 解锁，并只修改 Runtime Library。
    /// </summary>
    public sealed class AllSpellsRewardPickupTests
    {
        private readonly List<UnityEngine.Object> _createdObjects =
            new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            EventBus<EncounterCompletedEvent>.Clear();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                {
                    Object.Destroy(_createdObjects[i]);
                }
            }

            _createdObjects.Clear();
            yield return null;
            EventBus<EncounterCompletedEvent>.Clear();
        }

        [UnityTest]
        public IEnumerator MatchingCompletion_RevealsPickupAndCollectUnlocksFullLibraryOnce()
        {
            SpellDefinition fire = CreateSpell("Fire");
            SpellDefinition water = CreateSpell("Water");
            SpellLibrary fullLibrary = CreateLibrary(fire, water);
            RunSpellSession session = CreateSession(fire);

            GameObject pickupObject = CreateGameObject("All Spells Reward");
            pickupObject.SetActive(false);
            Collider trigger = pickupObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            GameObject visual = CreateGameObject("Visual", pickupObject.transform);
            AllSpellsRewardPickup pickup = pickupObject.AddComponent<AllSpellsRewardPickup>();
            SetPrivateField(pickup, "_session", session);
            SetPrivateField(pickup, "_fullLibrary", fullLibrary);
            SetPrivateField(pickup, "_encounterIndex", 3);
            SetPrivateField(pickup, "_visualRoot", visual);
            SetPrivateField(pickup, "_playerLayers", (LayerMask)(1 << 8));
            pickupObject.SetActive(true);

            Assert.That(pickup.IsAvailable, Is.False);
            Assert.That(trigger.enabled, Is.False);
            Assert.That(visual.activeSelf, Is.False);
            EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent
            {
                EncounterIndex = 2,
                EncounterCount = 4
            });
            Assert.That(pickup.IsAvailable, Is.False,
                "只有绑定的最终 Encounter 可以开放全法术奖励。");

            EventBus<EncounterCompletedEvent>.Publish(new EncounterCompletedEvent
            {
                EncounterIndex = 3,
                EncounterCount = 4
            });

            Assert.That(pickup.IsAvailable, Is.True);
            Assert.That(trigger.enabled, Is.True);
            Assert.That(visual.activeSelf, Is.True);

            GameObject player = CreateGameObject("Player");
            player.layer = 8;
            Collider playerCollider = player.AddComponent<BoxCollider>();

            Assert.That(pickup.TryCollect(playerCollider), Is.True);
            SpellDefinition[] firstUnlockedArray = session.RuntimeLibrary.Available;
            Assert.That(firstUnlockedArray, Is.EqualTo(new[] { fire, water }));
            Assert.That(pickup.TryCollect(playerCollider), Is.False);
            Assert.That(session.RuntimeLibrary.Available, Is.SameAs(firstUnlockedArray),
                "重复 Collider 不能再次合并或替换 Runtime Library 数组。");
            Assert.That(pickup.IsConsumed, Is.True);
            Assert.That(trigger.enabled, Is.False);
            Assert.That(visual.activeSelf, Is.False);
            yield return null;
        }

        private RunSpellSession CreateSession(SpellDefinition startingSpell)
        {
            SpellLibrary startingLibrary = CreateLibrary(startingSpell);
            WandLoadout wand = ScriptableObject.CreateInstance<WandLoadout>();
            wand.Spells = new[] { startingSpell };
            wand.BaseDraws = 1;
            _createdObjects.Add(wand);

            GameObject sessionObject = CreateGameObject("Run Spell Session");
            sessionObject.SetActive(false);
            RunSpellSession session = sessionObject.AddComponent<RunSpellSession>();
            session.ConfigureAndInitialize(startingLibrary, wand);
            return session;
        }

        private SpellDefinition CreateSpell(string displayName)
        {
            SpellDefinition spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.DisplayName = displayName;
            _createdObjects.Add(spell);
            return spell;
        }

        private SpellLibrary CreateLibrary(params SpellDefinition[] spells)
        {
            SpellLibrary library = ScriptableObject.CreateInstance<SpellLibrary>();
            library.Available = spells;
            _createdObjects.Add(library);
            return library;
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
    }
}

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Character;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// Scene-local 消费者不能序列化引用上一张 Scene 带来的 DDOL Root；
    /// 测试锁定它们会在地图二按需绑定 RunSpellSession.Current。
    /// </summary>
    public sealed class CrossSceneRunSessionConsumerTests
    {
        private readonly List<Object> _createdObjects = new List<Object>();

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
        }

        [UnityTest]
        public IEnumerator SpellCaster_MissingSceneReference_UsesCurrentRuntimeWand()
        {
            WandLoadout runtimeTemplate = CreateWand("Runtime Wand");
            RunSpellSession session = CreateCurrentSession(runtimeTemplate);
            WandLoadout fallbackWand = CreateWand("Authoring Fallback");

            GameObject casterObject = CreateGameObject("SpellCaster");
            SpellCaster caster = casterObject.AddComponent<SpellCaster>();
            SetPrivateField(caster, "_runSpellSession", null);
            SetPrivateField(caster, "_wand", fallbackWand);

            Assert.That(session.RuntimeWand, Is.Not.Null);
            Assert.That(caster.TryBindCurrentRunSession(), Is.True);
            Assert.That(caster.Wand, Is.SameAs(session.RuntimeWand),
                "地图二的 SpellCaster 必须使用跨 Scene Runtime Wand，不能退回 Authoring Asset。");
            yield return null;
        }

        [UnityTest]
        public IEnumerator WandEditor_MissingSceneReference_BindsCurrentRuntimeWand()
        {
            WandLoadout runtimeTemplate = CreateWand("Runtime Wand");
            RunSpellSession session = CreateCurrentSession(runtimeTemplate);
            WandLoadout fallbackWand = CreateWand("Authoring Fallback");

            GameObject editorObject = CreateGameObject("WandEditor");
            editorObject.SetActive(false);
            WandEditorController editor = editorObject.AddComponent<WandEditorController>();
            SetPrivateField(editor, "_runSpellSession", null);
            SetPrivateField(editor, "_wand", fallbackWand);

            Assert.That(editor.TryBindCurrentRunSession(), Is.True);
            InvokePrivate(editor, "BindRuntimeData");

            Assert.That(
                GetPrivateField<WandLoadout>(editor, "_wand"),
                Is.SameAs(session.RuntimeWand),
                "地图二 Wand Editor 必须编辑 DDOL Session 的 Runtime Clone。");
            yield return null;
        }

        [UnityTest]
        public IEnumerator Coordinator_NestedInAuthoringHierarchy_PromotesItselfToSceneRoot()
        {
            WandLoadout startingWand = CreateWand("Nested Session Wand");
            SpellLibrary startingLibrary =
                ScriptableObject.CreateInstance<SpellLibrary>();
            startingLibrary.Available = startingWand.Spells;
            _createdObjects.Add(startingLibrary);

            GameObject gameplayRoot = CreateGameObject("_Gameplay");
            GameObject sessionRoot = CreateGameObject("P8_RunSessionRoot");
            sessionRoot.SetActive(false);
            sessionRoot.transform.SetParent(gameplayRoot.transform, false);
            RunSpellSession session =
                sessionRoot.AddComponent<RunSpellSession>();
            Assert.That(
                session.ConfigureAndInitialize(startingLibrary, startingWand),
                Is.True);
            RunSessionCoordinator coordinator =
                sessionRoot.AddComponent<RunSessionCoordinator>();

            sessionRoot.SetActive(true);
            yield return null;

            Assert.That(sessionRoot.transform.parent, Is.Null,
                "DontDestroyOnLoad 只接受 Scene Root；Coordinator 必须先解除错误的 Authoring Parent。");
            Assert.That(RunSessionCoordinator.Current, Is.SameAs(coordinator));
            Assert.That(RunSpellSession.Current, Is.SameAs(session));
        }

        private RunSpellSession CreateCurrentSession(WandLoadout startingWand)
        {
            SpellLibrary startingLibrary = ScriptableObject.CreateInstance<SpellLibrary>();
            startingLibrary.name = "Starting Library";
            startingLibrary.Available = startingWand.Spells;
            _createdObjects.Add(startingLibrary);

            GameObject sessionObject = CreateGameObject("RunSpellSession");
            sessionObject.SetActive(false);
            RunSpellSession session = sessionObject.AddComponent<RunSpellSession>();
            Assert.That(
                session.ConfigureAndInitialize(startingLibrary, startingWand),
                Is.True);
            return session;
        }

        private WandLoadout CreateWand(string wandName)
        {
            SpellDefinition spell = ScriptableObject.CreateInstance<SpellDefinition>();
            spell.DisplayName = $"{wandName} Spell";
            _createdObjects.Add(spell);

            WandLoadout wand = ScriptableObject.CreateInstance<WandLoadout>();
            wand.name = wandName;
            wand.Spells = new[] { spell };
            wand.BaseDraws = 1;
            _createdObjects.Add(wand);
            return wand;
        }

        private GameObject CreateGameObject(string objectName)
        {
            GameObject gameObject = new GameObject(objectName);
            _createdObjects.Add(gameObject);
            return gameObject;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"找不到测试字段 {fieldName}。");
            field.SetValue(target, value);
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"找不到测试字段 {fieldName}。");
            return (T)field.GetValue(target);
        }

        private static void InvokePrivate(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"找不到测试方法 {methodName}。");
            method.Invoke(target, null);
        }
    }
}

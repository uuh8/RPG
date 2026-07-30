using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    public sealed class DemoPauseMenuBossFlowTests
    {
        private readonly List<Object> _createdObjects = new List<Object>();
        private RunSpellSession _spellSession;
        private RunSessionCoordinator _sessionCoordinator;
        private DemoSceneTransitionController _transition;
        private DemoPauseMenuController _menu;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Time.timeScale = 1f;

            SpellDefinition spell =
                ScriptableObject.CreateInstance<SpellDefinition>();
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

            GameObject sessionObject = new GameObject("Run Session Root");
            sessionObject.SetActive(false);
            _createdObjects.Add(sessionObject);
            _spellSession = sessionObject.AddComponent<RunSpellSession>();
            _spellSession.ConfigureAndInitialize(library, wand);
            _sessionCoordinator =
                sessionObject.AddComponent<RunSessionCoordinator>();
            sessionObject.SetActive(true);
            _sessionCoordinator.PrepareBossFieldEntry();
            Assert.That(_sessionCoordinator.CaptureBossCheckpoint(), Is.True);

            GameObject transitionObject = new GameObject("Scene Transition");
            transitionObject.SetActive(false);
            _createdObjects.Add(transitionObject);
            CanvasGroup fade = transitionObject.AddComponent<CanvasGroup>();
            _transition =
                transitionObject.AddComponent<DemoSceneTransitionController>();
            SetPrivateField(_transition, "_fadeCanvasGroup", fade);
            SetPrivateField(_transition, "_fadeDuration", 1000f);
            transitionObject.SetActive(true);

            GameObject menuObject = new GameObject("Pause Menu");
            menuObject.SetActive(false);
            _createdObjects.Add(menuObject);
            _menu = menuObject.AddComponent<DemoPauseMenuController>();
            SetPrivateField(_menu, "_sceneTransition", _transition);
            SetPrivateField(_menu, "_sessionCoordinator", _sessionCoordinator);
            SetPrivateField(_menu, "_bossSceneName", "P8_BossField");
            SetPrivateField(_menu, "_runSceneName", "P7_DemoRun");
            SetPrivateField(_menu, "_mainMenuSceneName", "P7_MainMenu");
            menuObject.SetActive(true);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = 1f;
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
        public IEnumerator RetryBoss_RestoresCheckpointAndKeepsSessionAuthority()
        {
            _spellSession.RuntimeWand.BaseDraws = 9;

            _menu.RetryBoss();
            yield return null;

            Assert.That(_spellSession.RuntimeWand.BaseDraws, Is.EqualTo(1));
            Assert.That(
                _sessionCoordinator.EntryMode,
                Is.EqualTo(RunEntryMode.RetryBoss));
            Assert.That(RunSessionCoordinator.Current,
                Is.SameAs(_sessionCoordinator));
            Assert.That(_transition.IsTransitioning, Is.True);
        }

        [UnityTest]
        public IEnumerator RestartRun_EndsSessionBeforeRequestingMapOne()
        {
            _menu.RestartRun();
            yield return null;

            Assert.That(RunSpellSession.Current, Is.Null);
            Assert.That(RunSessionCoordinator.Current, Is.Null);
            Assert.That(_transition.IsTransitioning, Is.True);
        }

        [UnityTest]
        public IEnumerator ReturnToMainMenu_EndsSessionBeforeNavigation()
        {
            _menu.ReturnToMainMenu();
            yield return null;

            Assert.That(RunSpellSession.Current, Is.Null);
            Assert.That(RunSessionCoordinator.Current, Is.Null);
            Assert.That(_transition.IsTransitioning, Is.True);
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

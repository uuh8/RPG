using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
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
        private RunPauseCoordinator _pauseCoordinator;
        private GameObject _victoryPanel;
        private GameObject _defeatPanel;

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
            _pauseCoordinator = menuObject.AddComponent<RunPauseCoordinator>();
            _menu = menuObject.AddComponent<DemoPauseMenuController>();

            _victoryPanel = new GameObject("Victory Panel");
            _victoryPanel.transform.SetParent(menuObject.transform);
            _defeatPanel = new GameObject("Defeat Panel");
            _defeatPanel.transform.SetParent(menuObject.transform);

            SetPrivateField(_menu, "_pauseCoordinator", _pauseCoordinator);
            SetPrivateField(_menu, "_victoryPanel", _victoryPanel);
            SetPrivateField(_menu, "_defeatPanel", _defeatPanel);
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

        [UnityTest]
        public IEnumerator TerminalResult_EventFrameKeepsDeathPresentationVisible()
        {
            EventBus<RunStateChangedEvent>.Publish(new RunStateChangedEvent
            {
                PreviousState = RunState.Running,
                CurrentState = RunState.Completed,
                CurrentEncounterIndex = -1
            });

            yield return null;

            Assert.That(_victoryPanel.activeSelf, Is.False,
                "死亡展示延迟结束前不应显示胜利界面。");
            Assert.That(_defeatPanel.activeSelf, Is.False);
            Assert.That(
                _pauseCoordinator.HasReason(RunPauseReason.TerminalResult),
                Is.False,
                "事件同帧不能硬暂停，否则 Die 动画会被冻结。");
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [UnityTest]
        public IEnumerator TerminalResult_FadeContinuesAfterTerminalPause()
        {
            SetPrivateField(_menu, "_victoryRevealDelay", 0f);
            SetPrivateField(_menu, "_resultFadeDuration", 0.15f);
            SetPrivateField(_menu, "_resultStartScale", 0.8f);

            EventBus<RunStateChangedEvent>.Publish(new RunStateChangedEvent
            {
                PreviousState = RunState.Running,
                CurrentState = RunState.Completed,
                CurrentEncounterIndex = -1
            });

            yield return null;

            CanvasGroup canvasGroup = _victoryPanel.GetComponent<CanvasGroup>();
            Assert.That(_victoryPanel.activeSelf, Is.True);
            Assert.That(canvasGroup, Is.Not.Null,
                "结果 Panel 应通过 CanvasGroup 统一控制整组 UI 的透明度。");
            Assert.That(
                _pauseCoordinator.HasReason(RunPauseReason.TerminalResult),
                Is.True);
            Assert.That(Time.timeScale, Is.EqualTo(0f));
            Assert.That(canvasGroup.alpha, Is.GreaterThan(0f).And.LessThan(1f));
            Assert.That(_victoryPanel.transform.localScale.x,
                Is.GreaterThan(0.8f).And.LessThan(1f));

            yield return new WaitForSecondsRealtime(0.2f);

            Assert.That(canvasGroup.alpha, Is.EqualTo(1f).Within(0.001f));
            Assert.That(_victoryPanel.transform.localScale,
                Is.EqualTo(Vector3.one));
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

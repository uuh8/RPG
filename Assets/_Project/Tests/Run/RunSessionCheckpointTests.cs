using System.Collections;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// 验证跨 Scene Coordinator 只保存轻量 Run Data，并区分 New Run 与 Boss Retry 的恢复语义。
    /// </summary>
    public sealed class RunSessionCheckpointTests
    {
        private SpellDefinition _fire;
        private SpellDefinition _water;
        private SpellLibrary _startingLibrary;
        private WandLoadout _startingWand;
        private GameObject _sessionObject;
        private RunSpellSession _spellSession;
        private RunSessionCoordinator _coordinator;

        [SetUp]
        public void SetUp()
        {
            CreateFixture();
        }

        private void CreateFixture(bool activateSessionRoot = false)
        {
            _fire = ScriptableObject.CreateInstance<SpellDefinition>();
            _water = ScriptableObject.CreateInstance<SpellDefinition>();
            _startingLibrary = ScriptableObject.CreateInstance<SpellLibrary>();
            _startingLibrary.Available = new[] { _fire };
            _startingWand = ScriptableObject.CreateInstance<WandLoadout>();
            _startingWand.Spells = new[] { _fire };
            _startingWand.BaseDraws = 1;

            _sessionObject = new GameObject("RunSessionCheckpointTests");
            _sessionObject.SetActive(false);
            _spellSession = _sessionObject.AddComponent<RunSpellSession>();
            _spellSession.ConfigureAndInitialize(_startingLibrary, _startingWand);
            _coordinator = _sessionObject.AddComponent<RunSessionCoordinator>();
            _sessionObject.SetActive(activateSessionRoot);
        }

        [TearDown]
        public void TearDown()
        {
            DestroyFixture();
        }

        private void DestroyFixture()
        {
            if (_sessionObject != null)
                Object.DestroyImmediate(_sessionObject);
            if (_startingLibrary != null)
                Object.DestroyImmediate(_startingLibrary);
            if (_startingWand != null)
                Object.DestroyImmediate(_startingWand);
            if (_fire != null)
                Object.DestroyImmediate(_fire);
            if (_water != null)
                Object.DestroyImmediate(_water);

            _sessionObject = null;
            _spellSession = null;
            _coordinator = null;
            _startingLibrary = null;
            _startingWand = null;
            _fire = null;
            _water = null;
        }

        [Test]
        public void BeginNewRun_ResetsTemplatesAndClearsOldCheckpoint()
        {
            _spellSession.RuntimeWand.Spells = new[] { _water };
            Assert.That(_coordinator.CaptureBossCheckpoint(), Is.True);

            _coordinator.BeginNewRun();

            Assert.That(_coordinator.Stage, Is.EqualTo(RunStage.MapOne));
            Assert.That(_coordinator.EntryMode, Is.EqualTo(RunEntryMode.NewRun));
            Assert.That(_coordinator.HasBossCheckpoint, Is.False);
            Assert.That(_spellSession.RuntimeWand.Spells, Is.EqualTo(new[] { _fire }));
        }

        [Test]
        public void RestoreBossCheckpoint_RestoresWandAndMarksRetryEntry()
        {
            _spellSession.RuntimeWand.Spells = new[] { _water, _fire };
            _spellSession.RuntimeWand.BaseDraws = 2;
            _coordinator.PrepareBossFieldEntry();
            Assert.That(_coordinator.CaptureBossCheckpoint(), Is.True);

            _spellSession.RuntimeWand.Spells[0] = _fire;
            _spellSession.RuntimeWand.BaseDraws = 7;

            Assert.That(_coordinator.RestoreBossCheckpoint(), Is.True);
            Assert.That(_coordinator.Stage, Is.EqualTo(RunStage.BossField));
            Assert.That(_coordinator.EntryMode, Is.EqualTo(RunEntryMode.RetryBoss));
            Assert.That(_spellSession.RuntimeWand.Spells, Is.EqualTo(new[] { _water, _fire }));
            Assert.That(_spellSession.RuntimeWand.BaseDraws, Is.EqualTo(2));
        }

        [Test]
        public void ClearSession_ReleasesRuntimeAndRejectsCheckpointRestore()
        {
            Assert.That(_coordinator.CaptureBossCheckpoint(), Is.True);

            _coordinator.ClearSession();

            Assert.That(_spellSession.IsInitialized, Is.False);
            Assert.That(RunSpellSession.Current, Is.Null);
            Assert.That(_coordinator.HasBossCheckpoint, Is.False);
            Assert.That(_coordinator.RestoreBossCheckpoint(), Is.False);
        }

        [UnityTest]
        public IEnumerator EndRun_ClearsRuntimeAndDestroysSessionAuthority()
        {
            // EndRun 的契约包含 DontDestroyOnLoad Authority 与 Destroy 的帧末语义，
            // 这两者只能在真实 Play Mode 生命周期中验证，不能用 inactive EditMode 夹具代替。
            DestroyFixture();
            yield return new EnterPlayMode();

            // EnterPlayMode 会载入当前 Editor Scene；若场景本身带有 Session Root，
            // 先清掉该临时 Play Mode Authority，避免它污染当前测试的 Singleton 前置条件。
            if (RunSessionCoordinator.Current != null)
            {
                Object.Destroy(RunSessionCoordinator.Current.gameObject);
                yield return null;
            }

            CreateFixture(activateSessionRoot: true);
            Assert.That(_coordinator.CaptureBossCheckpoint(), Is.True);

            Assert.That(_coordinator.EndRun(), Is.True);
            yield return null;

            Assert.That(RunSpellSession.Current, Is.Null);
            Assert.That(RunSessionCoordinator.Current, Is.Null);
            Assert.That(_sessionObject, Is.Null,
                "Restart/Main Menu 必须销毁 DDOL Authority，不能让 P7 新 Root 被空 Session 拒绝。");

            yield return new ExitPlayMode();
        }
    }
}

using Game.Skills;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>
    /// 验证 Authoring Template 与单局 Runtime State 的所有权边界。
    /// </summary>
    public sealed class RunSpellSessionTests
    {
        private SpellDefinition _fire;
        private SpellDefinition _water;
        private SpellLibrary _templateLibrary;
        private SpellLibrary _fullLibrary;
        private WandLoadout _templateWand;
        private GameObject _sessionObject;

        [SetUp]
        public void SetUp()
        {
            _fire = ScriptableObject.CreateInstance<SpellDefinition>();
            _fire.DisplayName = "Fire";
            _water = ScriptableObject.CreateInstance<SpellDefinition>();
            _water.DisplayName = "Water";

            _templateLibrary = ScriptableObject.CreateInstance<SpellLibrary>();
            _templateLibrary.Available = new[] { _fire };
            _fullLibrary = ScriptableObject.CreateInstance<SpellLibrary>();
            _fullLibrary.Available = new[] { _fire, _water };
            _templateWand = ScriptableObject.CreateInstance<WandLoadout>();
            _templateWand.Spells = new[] { _fire };
            _templateWand.BaseDraws = 1;
        }

        [TearDown]
        public void TearDown()
        {
            if (_sessionObject != null)
                Object.DestroyImmediate(_sessionObject);
            Object.DestroyImmediate(_templateLibrary);
            Object.DestroyImmediate(_fullLibrary);
            Object.DestroyImmediate(_templateWand);
            Object.DestroyImmediate(_fire);
            Object.DestroyImmediate(_water);
        }

        [Test]
        public void Initialize_CreatesIndependentRuntimeContainers()
        {
            RunSpellSession session = CreateSession();

            Assert.That(session.RuntimeLibrary, Is.Not.SameAs(_templateLibrary));
            Assert.That(session.RuntimeWand, Is.Not.SameAs(_templateWand));
            Assert.That(session.RuntimeLibrary.Available, Is.Not.SameAs(_templateLibrary.Available));
            Assert.That(session.RuntimeWand.Spells, Is.Not.SameAs(_templateWand.Spells));
            Assert.That(session.RuntimeLibrary.Available[0], Is.SameAs(_fire));
            Assert.That(session.RuntimeWand.Spells[0], Is.SameAs(_fire));
        }

        [Test]
        public void GrantAndWandEdit_DoNotMutateAuthoringTemplates()
        {
            RunSpellSession session = CreateSession();

            Assert.That(session.TryGrantSpell(_water), Is.True);
            session.RuntimeWand.Spells = new[] { _water };

            Assert.That(session.RuntimeLibrary.Available, Is.EqualTo(new[] { _fire, _water }));
            Assert.That(_templateLibrary.Available, Is.EqualTo(new[] { _fire }));
            Assert.That(_templateWand.Spells, Is.EqualTo(new[] { _fire }));
        }

        [Test]
        public void NewSessionFromSameTemplates_StartsCleanAfterPreviousRunChanged()
        {
            RunSpellSession first = CreateSession();
            first.TryGrantSpell(_water);
            Object.DestroyImmediate(_sessionObject);
            _sessionObject = null;

            RunSpellSession second = CreateSession();

            Assert.That(second.RuntimeLibrary.Available, Is.EqualTo(new[] { _fire }));
            Assert.That(second.RuntimeWand.Spells, Is.EqualTo(new[] { _fire }));
        }

        [Test]
        public void EnsureAllSpellsUnlocked_IsIdempotentAndPreservesFullLibraryOrder()
        {
            RunSpellSession session = CreateSession();

            Assert.That(session.EnsureAllSpellsUnlocked(_fullLibrary), Is.True);
            SpellDefinition[] firstResult = session.RuntimeLibrary.Available;
            Assert.That(firstResult, Is.EqualTo(new[] { _fire, _water }));

            Assert.That(session.EnsureAllSpellsUnlocked(_fullLibrary), Is.False);
            Assert.That(session.RuntimeLibrary.Available, Is.SameAs(firstResult),
                "第二次全解锁不得替换数组，否则 Reward 与 Portal 双入口不再是零变更幂等操作。");
        }

        [Test]
        public void ResetFromTemplates_DiscardsRuntimeLibraryAndWandChanges()
        {
            RunSpellSession session = CreateSession();
            session.EnsureAllSpellsUnlocked(_fullLibrary);
            session.RuntimeWand.Spells = new[] { _water };
            session.RuntimeWand.BaseDraws = 3;

            session.ResetFromTemplates();

            Assert.That(session.RuntimeLibrary.Available, Is.EqualTo(new[] { _fire }));
            Assert.That(session.RuntimeWand.Spells, Is.EqualTo(new[] { _fire }));
            Assert.That(session.RuntimeWand.BaseDraws, Is.EqualTo(1));
            Assert.That(_templateLibrary.Available, Is.EqualTo(new[] { _fire }));
            Assert.That(_templateWand.Spells, Is.EqualTo(new[] { _fire }));
        }

        [Test]
        public void RestoreBossCheckpoint_CopiesArrayAndPreservesSpellAssetReferences()
        {
            RunSpellSession session = CreateSession();
            var checkpoint = new BossCheckpointSnapshot(
                new[] { _water, _fire },
                baseDraws: 2);

            Assert.That(session.RestoreBossCheckpoint(in checkpoint), Is.True);
            Assert.That(session.RuntimeWand.Spells, Is.EqualTo(new[] { _water, _fire }));
            Assert.That(session.RuntimeWand.BaseDraws, Is.EqualTo(2));

            session.RuntimeWand.Spells[0] = _fire;
            Assert.That(session.RestoreBossCheckpoint(in checkpoint), Is.True);
            Assert.That(session.RuntimeWand.Spells[0], Is.SameAs(_water),
                "恢复后的 Runtime 数组必须与 Checkpoint 隔离，SpellDefinition Asset 引用则继续共享。");
        }

        [Test]
        public void ClearRuntime_ReleasesCurrentAndAllowsCleanReinitialize()
        {
            RunSpellSession session = CreateSession();
            Assert.That(RunSpellSession.Current, Is.SameAs(session));

            session.ClearRuntime();

            Assert.That(session.IsInitialized, Is.False);
            Assert.That(session.RuntimeLibrary, Is.Null);
            Assert.That(session.RuntimeWand, Is.Null);
            Assert.That(RunSpellSession.Current, Is.Null);

            session.ResetFromTemplates();
            Assert.That(session.IsInitialized, Is.True);
            Assert.That(RunSpellSession.Current, Is.SameAs(session));
        }

        [Test]
        public void SecondSession_DoesNotReplaceExistingCurrentOwner()
        {
            RunSpellSession first = CreateSession();
            var duplicateObject = new GameObject("DuplicateRunSpellSession");
            duplicateObject.SetActive(false);

            try
            {
                RunSpellSession duplicate = duplicateObject.AddComponent<RunSpellSession>();

                Assert.That(
                    duplicate.ConfigureAndInitialize(_templateLibrary, _templateWand),
                    Is.False,
                    "后加载 Scene 中的重复 Session 必须等待 Coordinator 自毁，不能覆盖或抛异常中断加载。");
                Assert.That(RunSpellSession.Current, Is.SameAs(first));
                Assert.That(duplicate.IsInitialized, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(duplicateObject);
            }
        }

        private RunSpellSession CreateSession()
        {
            _sessionObject = new GameObject("RunSpellSessionTests");
            _sessionObject.SetActive(false);
            RunSpellSession session = _sessionObject.AddComponent<RunSpellSession>();
            session.ConfigureAndInitialize(_templateLibrary, _templateWand);
            return session;
        }
    }
}

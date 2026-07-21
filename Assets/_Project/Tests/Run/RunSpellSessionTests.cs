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

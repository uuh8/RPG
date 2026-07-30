using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.Run;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    public sealed class WizardBossControllerTests
    {
        private readonly List<Object> _createdObjects = new List<Object>();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            BossRuntimeTestProjectile[] projectiles =
                Object.FindObjectsByType<BossRuntimeTestProjectile>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
            for (int i = 0; i < projectiles.Length; i++)
            {
                if (projectiles[i] != null)
                {
                    Object.Destroy(projectiles[i].gameObject);
                }
            }

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
        public IEnumerator BeforeActivation_RemainsInactiveAndStill()
        {
            WizardBossController boss = CreateBoss(CreateDefinition());
            Vector3 positionBefore = boss.transform.position;

            yield return null;
            yield return null;

            Assert.That(boss.IsEncounterActive, Is.False);
            Assert.That(
                boss.CurrentStateKind,
                Is.EqualTo(BossRuntimeStateKind.Inactive));
            Assert.That(boss.transform.position, Is.EqualTo(positionBefore));
        }

        [UnityTest]
        public IEnumerator TryActivate_CapturesFirstTargetAndNeverRetargets()
        {
            BossDefinition definition = CreateDefinition();
            definition.StopDistance = 1f;
            definition.MoveSpeed = 6f;
            WizardBossController boss = CreateBoss(definition);
            Transform firstTarget =
                CreateTarget("First Target", new Vector3(10f, 0f, 0f));
            Transform secondTarget =
                CreateTarget("Second Target", new Vector3(-10f, 0f, 0f));

            Assert.That(boss.TryActivate(firstTarget), Is.True);
            Assert.That(boss.TryActivate(secondTarget), Is.False);

            float startX = boss.transform.position.x;
            yield return new WaitForSeconds(0.1f);

            Assert.That(boss.Target, Is.SameAs(firstTarget));
            Assert.That(boss.transform.position.x, Is.GreaterThan(startX));
            Assert.That(
                boss.CurrentStateKind,
                Is.Not.EqualTo(BossRuntimeStateKind.Inactive));
        }

        [UnityTest]
        public IEnumerator TryActivate_PublishesDataDrivenBossHudSnapshot()
        {
            BossHudInitializedEvent received = default;
            int receivedCount = 0;
            void OnInitialized(BossHudInitializedEvent initializedEvent)
            {
                received = initializedEvent;
                receivedCount++;
            }

            EventBus<BossHudInitializedEvent>.Clear();
            EventBus<BossHudInitializedEvent>.Subscribe(OnInitialized);

            BossDefinition definition = CreateDefinition();
            definition.DisplayName = "测试奥术法师";
            definition.Phase2.EnterAtOrBelowHealthRatio = 0.72f;
            definition.Phase3.EnterAtOrBelowHealthRatio = 0.31f;
            WizardBossController boss = CreateBoss(definition);
            Transform target =
                CreateTarget("Target", new Vector3(4f, 0f, 0f));

            Assert.That(boss.TryActivate(target), Is.True);
            Assert.That(receivedCount, Is.EqualTo(1));
            Assert.That(received.BossId, Is.EqualTo(boss.Health.Id));
            Assert.That(received.DisplayName, Is.EqualTo("测试奥术法师"));
            Assert.That(received.CurrentHp, Is.EqualTo(boss.Health.CurrentHp));
            Assert.That(received.MaxHp, Is.EqualTo(boss.Health.MaxHp));
            Assert.That(received.CurrentPhase, Is.EqualTo((byte)BossPhase.Phase1));
            Assert.That(received.Phase2Threshold, Is.EqualTo(0.72f));
            Assert.That(received.Phase3Threshold, Is.EqualTo(0.31f));

            EventBus<BossHudInitializedEvent>.Unsubscribe(OnInitialized);
            EventBus<BossHudInitializedEvent>.Clear();
            yield return null;
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator Approach_WritesHorizontalVelocityToAnimatorSpeed()
        {
            RuntimeAnimatorController animatorController =
                AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                    "Assets/_Project/Art/Animators/Enemy_Wizard.controller");
            Assert.That(animatorController, Is.Not.Null);

            BossDefinition definition = CreateDefinition();
            definition.StopDistance = 1f;
            definition.MoveSpeed = 6f;
            WizardBossController boss =
                CreateBoss(definition, animatorController);
            Transform target =
                CreateTarget("Target", new Vector3(10f, 0f, 0f));

            Assert.That(boss.TryActivate(target), Is.True);
            yield return null;

            Animator animator = boss.GetComponentInChildren<Animator>();
            Assert.That(animator, Is.Not.Null);
            Assert.That(
                animator.GetFloat(Animator.StringToHash("speed")),
                Is.GreaterThan(0.1f));
        }
#endif

        [UnityTest]
        public IEnumerator PhaseThresholdDuringCast_DefersUntilActionCompletes()
        {
            BossDefinition definition = CreateDefinition();
            definition.StopDistance = 50f;
            definition.DecisionInterval = 0.01f;
            definition.CastTelegraphDuration = 0.12f;
            definition.CastRecoveryDuration = 0.12f;
            definition.PhaseTransitionDuration = 0.12f;
            definition.Phase1.CastInterval = 0.01f;
            definition.Programs = new[]
            {
                CreateProgram(CreateProjectileWand())
            };

            WizardBossController boss = CreateBoss(definition);
            Transform target =
                CreateTarget("Target", new Vector3(4f, 0f, 0f));
            Assert.That(boss.TryActivate(target), Is.True);

            yield return WaitForState(
                boss,
                BossRuntimeStateKind.Cast,
                1f);
            Assert.That(boss.IsActionLocked, Is.True);

            Vector3 lockedAim = boss.LockedAimPoint;
            target.position = new Vector3(12f, 0f, 0f);
            Assert.That(boss.LockedAimPoint, Is.EqualTo(lockedAim));

            DamageRequest request = new DamageRequest(
                1,
                2,
                40f,
                DamageType.Magical,
                boss.transform.position,
                Vector3.forward);
            boss.Health.ReceiveHit(in request);
            yield return null;

            Assert.That(boss.CurrentPhase, Is.EqualTo(BossPhase.Phase1));
            Assert.That(boss.PendingPhase, Is.EqualTo(BossPhase.Phase2));
            Assert.That(
                boss.CurrentStateKind,
                Is.EqualTo(BossRuntimeStateKind.Cast));

            yield return WaitForState(
                boss,
                BossRuntimeStateKind.PhaseTransition,
                1f);
            Assert.That(boss.Health.IsInvulnerable, Is.True);
            Assert.That(boss.CurrentPhase, Is.EqualTo(BossPhase.Phase1));

            yield return WaitForPhase(boss, BossPhase.Phase2, 1f);
            Assert.That(boss.Health.IsInvulnerable, Is.False);
            Assert.That(boss.Health.CurrentHp, Is.EqualTo(60f));
        }

        private WizardBossController CreateBoss(
            BossDefinition definition,
            RuntimeAnimatorController animatorController = null)
        {
            GameObject bossObject = new GameObject("Wizard Boss");
            bossObject.SetActive(false);
            _createdObjects.Add(bossObject);

            if (animatorController != null)
            {
                GameObject visualObject = new GameObject("Boss Visual");
                visualObject.transform.SetParent(
                    bossObject.transform,
                    false);
                Animator animator = visualObject.AddComponent<Animator>();
                animator.runtimeAnimatorController = animatorController;
            }

            bossObject.AddComponent<CharacterController>();
            HealthComponent health =
                bossObject.AddComponent<HealthComponent>();
            bossObject.AddComponent<StatusController>();
            bossObject.AddComponent<SpellCaster>();
            WizardBossController boss =
                bossObject.AddComponent<WizardBossController>();
            SetPrivateField(boss, "_definition", definition);
            SetPrivateField(boss, "_castOrigin", bossObject.transform);
            bossObject.SetActive(true);

            Assert.That(boss.Health, Is.SameAs(health));
            return boss;
        }

        private Transform CreateTarget(string name, Vector3 position)
        {
            GameObject target = new GameObject(name);
            target.transform.position = position;
            target.AddComponent<CharacterController>();
            target.AddComponent<HealthComponent>();
            target.AddComponent<StatusController>();
            _createdObjects.Add(target);
            return target.transform;
        }

        private BossDefinition CreateDefinition()
        {
            BossDefinition definition =
                ScriptableObject.CreateInstance<BossDefinition>();
            definition.MoveSpeed = 3.5f;
            definition.StopDistance = 8f;
            definition.DecisionInterval = 0.02f;
            definition.CastTelegraphDuration = 0.05f;
            definition.CastRecoveryDuration = 0.05f;
            definition.PhaseTransitionDuration = 0.05f;
            definition.Programs =
                System.Array.Empty<BossSpellProgramDefinition>();
            _createdObjects.Add(definition);
            return definition;
        }

        private BossSpellProgramDefinition CreateProgram(WandLoadout wand)
        {
            BossSpellProgramDefinition program =
                ScriptableObject.CreateInstance<BossSpellProgramDefinition>();
            program.Wand = wand;
            program.PhaseMask = BossPhaseMask.All;
            program.BaseWeight = 1f;
            program.IsFallback = true;
            _createdObjects.Add(program);
            return program;
        }

        private WandLoadout CreateProjectileWand()
        {
            GameObject projectileObject =
                new GameObject("Boss Runtime Projectile");
            projectileObject.SetActive(false);
            projectileObject.AddComponent<Rigidbody>();
            projectileObject.AddComponent<SphereCollider>();
            projectileObject.AddComponent<BossRuntimeTestProjectile>();
            _createdObjects.Add(projectileObject);

            SpellDefinition emit =
                ScriptableObject.CreateInstance<SpellDefinition>();
            emit.DisplayName = "Boss Runtime Emit";
            emit.Kind = SpellKind.Emit;
            emit.ProjectilePrefab = projectileObject;
            emit.ManaCost = 100f;
            emit.BaseDamage = 1f;
            emit.BaseSpeed = 1f;
            _createdObjects.Add(emit);

            WandLoadout wand = ScriptableObject.CreateInstance<WandLoadout>();
            wand.Spells = new[] { emit };
            wand.BaseDraws = 1;
            _createdObjects.Add(wand);
            return wand;
        }

        private static IEnumerator WaitForState(
            WizardBossController boss,
            BossRuntimeStateKind expected,
            float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (boss.CurrentStateKind != expected &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(boss.CurrentStateKind, Is.EqualTo(expected));
        }

        private static IEnumerator WaitForPhase(
            WizardBossController boss,
            BossPhase expected,
            float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (boss.CurrentPhase != expected &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(boss.CurrentPhase, Is.EqualTo(expected));
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

    public sealed class BossRuntimeTestProjectile : ProjectileBase
    {
    }
}

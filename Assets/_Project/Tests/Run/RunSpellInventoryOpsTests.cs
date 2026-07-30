using System;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>
    /// 锁定局内法术库存的纯数据规则。这里测试的是“数组所有权”，不是法术的伤害或施法逻辑：
    /// Authoring Template 与 Runtime State 可以引用同一批 SpellDefinition，但绝不能共用同一个可变数组。
    /// </summary>
    public sealed class RunSpellInventoryOpsTests
    {
        private SpellDefinition _fire;
        private SpellDefinition _water;

        [SetUp]
        public void SetUp()
        {
            _fire = ScriptableObject.CreateInstance<SpellDefinition>();
            _fire.DisplayName = "Fire";
            _water = ScriptableObject.CreateInstance<SpellDefinition>();
            _water.DisplayName = "Water";
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_fire);
            UnityEngine.Object.DestroyImmediate(_water);
        }

        [Test]
        public void CopyEntries_CreatesIndependentArrayAndPreservesSpellReferences()
        {
            var template = new[] { _fire, _water };

            SpellDefinition[] runtime = RunSpellInventoryOps.CopyEntries(template);

            Assert.That(runtime, Is.Not.SameAs(template),
                "Runtime 必须拥有独立数组，否则局内编辑会直接改写 ScriptableObject 模板。");
            Assert.That(runtime, Is.EqualTo(template),
                "浅拷贝只隔离排列容器，SpellDefinition 资源引用应继续共享。");

            runtime[0] = _water;
            Assert.That(template[0], Is.SameAs(_fire),
                "修改 Runtime 数组后，Authoring Template 的顺序必须保持不变。");
        }

        [Test]
        public void TryAppendUnique_NewReward_AppendsWithoutMutatingCurrentArray()
        {
            var current = new[] { _fire };

            bool added = RunSpellInventoryOps.TryAppendUnique(current, _water, out SpellDefinition[] result);

            Assert.That(added, Is.True);
            Assert.That(result, Is.EqualTo(new[] { _fire, _water }));
            Assert.That(result, Is.Not.SameAs(current));
            Assert.That(current, Is.EqualTo(new[] { _fire }),
                "纯函数不能原地修改调用者持有的数组。");
        }

        [Test]
        public void TryAppendUnique_DuplicateReward_DoesNotCreateSecondEntry()
        {
            var current = new[] { _fire, _water };

            bool added = RunSpellInventoryOps.TryAppendUnique(current, _fire, out SpellDefinition[] result);

            Assert.That(added, Is.False);
            Assert.That(result, Is.SameAs(current),
                "没有发生变化时复用原数组，避免拾取重复奖励时产生无意义分配。");
            Assert.That(result, Is.EqualTo(new[] { _fire, _water }));
        }

        [Test]
        public void TryAppendUnique_NullReward_ThrowsInsteadOfSilentlyAddingInvalidEntry()
        {
            Assert.Throws<ArgumentNullException>(() =>
                RunSpellInventoryOps.TryAppendUnique(Array.Empty<SpellDefinition>(), null, out _));
        }

        [Test]
        public void TryMergeUniqueInOrder_AppendsOnlyMissingSpellsInAuthoringOrder()
        {
            var current = new[] { _water };
            var fullLibrary = new[] { _fire, _water };

            bool changed = RunSpellInventoryOps.TryMergeUniqueInOrder(
                current,
                fullLibrary,
                out SpellDefinition[] result);

            Assert.That(changed, Is.True);
            Assert.That(result, Is.EqualTo(new[] { _water, _fire }),
                "已有 Runtime 顺序必须保留，缺失法术按完整 Library 的 Authoring 顺序追加。");
            Assert.That(current, Is.EqualTo(new[] { _water }),
                "合并不能原地修改调用方持有的 Runtime 数组。");
        }

        [Test]
        public void TryMergeUniqueInOrder_AlreadyComplete_ReusesCurrentArray()
        {
            var current = new[] { _fire, _water };
            var fullLibrary = new[] { _fire, _water, _fire };

            bool changed = RunSpellInventoryOps.TryMergeUniqueInOrder(
                current,
                fullLibrary,
                out SpellDefinition[] result);

            Assert.That(changed, Is.False);
            Assert.That(result, Is.SameAs(current),
                "没有缺失法术时必须复用原数组，保证重复 Reward/Portal 调用幂等且不分配。");
        }

        [Test]
        public void TryMergeUniqueInOrder_NullFullLibrary_ThrowsConfigurationError()
        {
            Assert.Throws<ArgumentNullException>(() =>
                RunSpellInventoryOps.TryMergeUniqueInOrder(
                    Array.Empty<SpellDefinition>(),
                    null,
                    out _));
        }
    }
}

using NUnit.Framework;

namespace Game.Combat.Tests
{
    /// <summary>
    /// 这些测试只验证“输入元素快照 -> 纯公式结果”，不创建 GameObject。
    /// 这样失败时可以先排除 Unity 生命周期、Physics 和 VFX，只检查规则本身。
    /// </summary>
    public sealed class ElementReactionEvaluatorTests
    {
        private static readonly StatusSource DefaultSource = new StatusSource(10, 1);

        [Test]
        public void SerializedEnumValues_AreStable()
        {
            // 这些值将来会进入 ScriptableObject 与跨模块事件。
            // 显式测试可以防止有人在枚举中间插值，导致旧资产被解释成另一种元素/反应。
            Assert.AreEqual(0, (byte)ElementChannel.Fire);
            Assert.AreEqual(1, (byte)ElementChannel.Water);
            Assert.AreEqual(2, (byte)ElementChannel.Poison);
            Assert.AreEqual(3, (byte)ElementChannel.Goo);

            Assert.AreEqual(0, (byte)ElementReactionId.Extinguish);
            Assert.AreEqual(1, (byte)ElementReactionId.ToxicCombustion);
            Assert.AreEqual(2, (byte)ElementReactionId.IgniteGoo);
        }

        [TestCase(9.99f, 100f, false)]
        [TestCase(100f, 9.99f, false)]
        [TestCase(10f, 10f, true)]
        [TestCase(100f, 100f, true)]
        public void Extinguish_FormalThreshold_IsInclusive(
            float fire,
            float water,
            bool expected)
        {
            var tuning = new ExtinguishTuning
            {
                FormalThreshold = 10f,
                LowRatePerSecond = 10f,
                FormalRatePerSecond = 50f,
            };

            bool actual = ElementReactionEvaluator.IsFormalExtinguish(
                Snapshot(fire: fire, water: water),
                in tuning);

            Assert.AreEqual(expected, actual);
        }

        [Test]
        public void WetCleanse_PrioritizesPoisonThenGoo()
        {
            var tuning = new WetCleanseTuning
            {
                BudgetPerSecondAtFullWet = 30f,
            };

            WetCleanseResult result = ElementReactionEvaluator.CalculateWetCleanse(
                Snapshot(water: 40f, poison: 3f, goo: 20f),
                deltaTime: 1f,
                poisonMultiplier: 0.75f,
                gooMultiplier: 1.25f,
                in tuning);

            // Wet=40 产生 12 点预算；清掉 3 Poison 花 4 点预算，
            // 剩余 8 点预算按 1.25 倍率清掉 10 Goo。
            Assert.AreEqual(3f, result.PoisonRemoved, 1e-4f);
            Assert.AreEqual(10f, result.GooRemoved, 1e-4f);
        }

        [Test]
        public void WetCleanse_DoesNotTouchGooWhilePoisonConsumesFullBudget()
        {
            var tuning = new WetCleanseTuning
            {
                BudgetPerSecondAtFullWet = 30f,
            };

            WetCleanseResult result = ElementReactionEvaluator.CalculateWetCleanse(
                Snapshot(water: 40f, poison: 100f, goo: 100f),
                deltaTime: 1f,
                poisonMultiplier: 0.75f,
                gooMultiplier: 1.25f,
                in tuning);

            Assert.AreEqual(9f, result.PoisonRemoved, 1e-4f);
            Assert.AreEqual(0f, result.GooRemoved, 1e-4f);
        }

        [TestCase(24.99f, 20f, false)]
        [TestCase(25f, 19.99f, false)]
        [TestCase(25f, 20f, true)]
        public void ToxicThreshold_RequiresFireAndPoison(
            float fire,
            float poison,
            bool expected)
        {
            var tuning = new ToxicCombustionTuning
            {
                FireThreshold = 25f,
                PoisonThreshold = 20f,
            };

            bool actual = ElementReactionEvaluator.CanStartToxicCombustion(
                Snapshot(fire: fire, poison: poison),
                in tuning);

            Assert.AreEqual(expected, actual);
        }

        [TestCase(9.99f, true)]
        [TestCase(10f, false)]
        [TestCase(100f, false)]
        public void Ignite_IsBlockedAtWetThreshold(float water, bool expected)
        {
            var tuning = new IgniteGooTuning
            {
                FireThreshold = 15f,
                GooThreshold = 20f,
                WetInhibitThreshold = 10f,
                RequiredFireCapacity = 10f,
            };

            bool actual = ElementReactionEvaluator.CanStartIgniteGoo(
                Snapshot(fire: 80f, water: water, goo: 20f),
                in tuning);

            Assert.AreEqual(expected, actual);
        }

        [TestCase(90f, true)]
        [TestCase(90.01f, false)]
        public void Ignite_RequiresConfiguredFireCapacity(float fire, bool expected)
        {
            var tuning = new IgniteGooTuning
            {
                FireThreshold = 15f,
                GooThreshold = 20f,
                WetInhibitThreshold = 10f,
                RequiredFireCapacity = 10f,
            };

            bool actual = ElementReactionEvaluator.CanStartIgniteGoo(
                Snapshot(fire: fire, water: 0f, goo: 20f),
                in tuning);

            Assert.AreEqual(expected, actual);
        }

        private static ElementStateSnapshot Snapshot(
            float fire = 0f,
            float water = 0f,
            float poison = 0f,
            float goo = 0f)
        {
            return new ElementStateSnapshot(
                fire,
                water,
                poison,
                goo,
                DefaultSource,
                DefaultSource,
                DefaultSource,
                DefaultSource);
        }
    }
}

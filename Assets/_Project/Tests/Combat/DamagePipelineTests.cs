using NUnit.Framework;
using UnityEngine;

namespace Game.Combat.Tests
{
    public class DamagePipelineTests
    {
        private static DamageRequest MakeRequest(float amount, DamageType type)
        {
            return new DamageRequest(
                attackerId: 1,
                attackerTeam: 0,
                baseAmount: amount,
                type: type,
                hitPoint: Vector3.zero,
                hitDirection: Vector3.forward);
        }

        // [Test] 特性：标记一个方法是测试用例
        [Test]
        public void True_IgnoresDefense_ReturnsBaseAmount()
        {
            var req = MakeRequest(50f, DamageType.True);
            var def = new DefenseProfile { Armor = 9999f, MagicResist = 9999f };

            DamageResult result = DamagePipeline.Resolve(in req, in def);

            // 这行代码的作用是判断计算出的最终伤害值是否等于预期的 50f，如果相等则测试通过，否则测试失败。
            Assert.That(result.Final, Is.EqualTo(50f));
        }

        [Test]
        public void Physical_WithZeroDefense_ReturnsBaseAmount()
        {
            var req = MakeRequest(30f, DamageType.Physical);
            var def = new DefenseProfile { Armor = 0f, MagicResist = 0f };

            DamageResult result = DamagePipeline.Resolve(in req, in def);

            Assert.That(result.Final, Is.EqualTo(30f));
        }

        [Test]
        public void Magical_WithZeroDefense_ReturnsBaseAmount()
        {
            var req = MakeRequest(40f, DamageType.Magical);
            var def = new DefenseProfile { Armor = 0f, MagicResist = 0f };

            DamageResult result = DamagePipeline.Resolve(in req, in def);

            Assert.That(result.Final, Is.EqualTo(40f));
        }

        [Test]
        public void Resolve_NegativeBase_ClampsToZero()
        {
            var req = MakeRequest(-10f, DamageType.Physical);
            var def = new DefenseProfile();

            DamageResult result = DamagePipeline.Resolve(in req, in def);

            Assert.That(result.Final, Is.EqualTo(0f));
        }

        [Test]
        public void Resolve_PreservesDamageType()
        {
            var req = MakeRequest(10f, DamageType.Magical);
            var def = new DefenseProfile();

            DamageResult result = DamagePipeline.Resolve(in req, in def);

            Assert.That(result.Type, Is.EqualTo(DamageType.Magical));
        }
    }
}

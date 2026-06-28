using NUnit.Framework;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class SpellAimingTests
    {
        [Test]
        public void SingleProjectile_NoOffset()
        {
            Assert.AreEqual(0f, SpellAiming.SpreadOffsetDegrees(0, 1, 30f), 1e-4f);
        }

        [Test]
        public void ZeroSpread_NoOffset()
        {
            Assert.AreEqual(0f, SpellAiming.SpreadOffsetDegrees(1, 3, 0f), 1e-4f);
        }

        [Test]
        public void Three_Spread30_FansEvenly()
        {
            Assert.AreEqual(-15f, SpellAiming.SpreadOffsetDegrees(0, 3, 30f), 1e-4f);
            Assert.AreEqual(0f, SpellAiming.SpreadOffsetDegrees(1, 3, 30f), 1e-4f);
            Assert.AreEqual(15f, SpellAiming.SpreadOffsetDegrees(2, 3, 30f), 1e-4f);
        }

        [Test]
        public void Two_Spread20_SymmetricEdges()
        {
            Assert.AreEqual(-10f, SpellAiming.SpreadOffsetDegrees(0, 2, 20f), 1e-4f);
            Assert.AreEqual(10f, SpellAiming.SpreadOffsetDegrees(1, 2, 20f), 1e-4f);
        }
    }
}

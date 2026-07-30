using NUnit.Framework;

namespace Game.Character.Tests
{
    public sealed class PlayerAirActionBudgetTests
    {
        [Test]
        public void ExtraJump_CanOnlyBeConsumedOnceBeforeLandingReset()
        {
            PlayerAirActionBudget budget = new PlayerAirActionBudget(1, 1);

            Assert.That(budget.TryConsumeExtraJump(), Is.True);
            Assert.That(budget.TryConsumeExtraJump(), Is.False);

            budget.ResetForGrounded();
            Assert.That(budget.TryConsumeExtraJump(), Is.True);
        }

        [Test]
        public void AirDash_HasIndependentBudget()
        {
            PlayerAirActionBudget budget = new PlayerAirActionBudget(1, 1);

            Assert.That(budget.TryConsumeAirDash(), Is.True);
            Assert.That(budget.TryConsumeAirDash(), Is.False);
            Assert.That(budget.TryConsumeExtraJump(), Is.True);
        }
    }
}

using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidGameplayOccupancyBridgeTests
    {
        [Test]
        public void RequestState_AllowsOnlyOneFlightAndOneLeaseRelease()
        {
            var state = new FluidReadbackRequestState();

            Assert.That(state.TryBegin(), Is.True);
            Assert.That(state.TryBegin(), Is.False);
            state.RecordCompletion(hasError: false);
            Assert.That(state.TryConsumeCompletion(out bool hasError), Is.True);
            Assert.That(hasError, Is.False);
            Assert.That(state.TryConsumeCompletion(out _), Is.False);
            Assert.That(state.TryReleaseLease(), Is.True);
            Assert.That(state.TryReleaseLease(), Is.False);
        }

        [Test]
        public void RequestState_DestroyWaitAndReleaseAreBothIdempotent()
        {
            var state = new FluidReadbackRequestState();
            state.TryBegin();

            Assert.That(state.TryBeginDestroyWait(), Is.True);
            Assert.That(state.TryBeginDestroyWait(), Is.False);
            state.RecordCompletion(hasError: true);
            Assert.That(state.TryReleaseLease(), Is.True);
            Assert.That(state.TryReleaseLease(), Is.False);
        }

        [Test]
        public void RequestState_CancelBeforeSubmissionClearsFlightAndStillReleasesLeaseOnce()
        {
            var state = new FluidReadbackRequestState();
            Assert.That(state.TryBegin(), Is.True);

            Assert.That(state.TryCancelBeforeSubmission(), Is.True);
            Assert.That(state.IsInFlight, Is.False);
            Assert.That(state.TryBeginDestroyWait(), Is.False);
            Assert.That(state.TryReleaseLease(), Is.True);
            Assert.That(state.TryReleaseLease(), Is.False);
            Assert.That(state.TryBegin(), Is.True);
        }

        [Test]
        public void SourceLeaseTracker_DefersTeardownUntilOutstandingLeaseIsReleased()
        {
            var tracker = new FluidReadbackLeaseTracker();
            Assert.That(tracker.TryAcquire(), Is.True);
            Assert.That(tracker.TryAcquire(), Is.False);
            Assert.That(tracker.RequestRelease(), Is.False);
            Assert.That(tracker.ReleaseLease(), Is.True);
            Assert.That(tracker.ReleaseLease(), Is.False);
        }

        [Test]
        public void FailedReadbackDecisionPreservesPreviousPublishedSnapshot()
        {
            Assert.That(FluidSnapshotPublishPolicy.ShouldPublish(
                requestHasError: true,
                stagingRebuildSucceeded: true), Is.False);
            Assert.That(FluidSnapshotPublishPolicy.ShouldPublish(
                requestHasError: false,
                stagingRebuildSucceeded: false), Is.False);
            Assert.That(FluidSnapshotPublishPolicy.ShouldPublish(
                requestHasError: false,
                stagingRebuildSucceeded: true), Is.True);
        }

        [TestCase(WaterSimulationMode.LegacyCell, ElementMaterialKind.Water, false)]
        [TestCase(WaterSimulationMode.LegacyCell, ElementMaterialKind.Fire, false)]
        [TestCase(WaterSimulationMode.GpuPbf, ElementMaterialKind.Water, true)]
        [TestCase(WaterSimulationMode.GpuPbf, ElementMaterialKind.Fire, false)]
        public void ExposurePolicy_RoutesOnlyPbfWaterToOccupancy(
            WaterSimulationMode mode,
            ElementMaterialKind material,
            bool expectedOccupancy)
        {
            Assert.That(
                ElementExposureSourcePolicy.ShouldReadOccupancy(mode, material),
                Is.EqualTo(expectedOccupancy));
        }

        [Test]
        public void ExposurePolicy_MissingPbfSnapshotDoesNotRedirectFire()
        {
            Assert.That(ElementExposureSourcePolicy.ResolveAmount(
                WaterSimulationMode.GpuPbf,
                ElementMaterialKind.Fire,
                hasLegacyCell: true,
                legacyAmount: 77,
                hasOccupancy: false,
                occupancyAmount: 0), Is.EqualTo(77));

            Assert.That(ElementExposureSourcePolicy.ResolveAmount(
                WaterSimulationMode.GpuPbf,
                ElementMaterialKind.Water,
                hasLegacyCell: true,
                legacyAmount: 99,
                hasOccupancy: false,
                occupancyAmount: 0), Is.Zero);
        }

        [Test]
        public void BroadphasePlan_KeepsLegacyAndDelayedLiquidBoundsAsTwoQueries()
        {
            var legacyBounds = new Bounds(Vector3.zero, Vector3.one * 4f);
            var delayedLiquidBounds = new Bounds(new Vector3(100f, 0f, 0f), Vector3.one * 4f);

            ElementExposureBroadphasePlan plan = ElementExposureBroadphasePlanner.Plan(
                WaterSimulationMode.GpuPbf,
                legacyBounds,
                hasLiquidSnapshot: true,
                delayedLiquidBounds);

            Assert.That(plan.QueryCount, Is.EqualTo(2),
                "不能把相距很远的 Bounds 合成一个巨大 OverlapBox；中间无关 Collider 会先填满 NonAlloc Buffer。");
            Assert.That(plan.GetQueryBounds(0), Is.EqualTo(legacyBounds),
                "Legacy/Fire Bounds 必须优先查询，避免 Water Snapshot 延迟时吞掉 Burning。");
            Assert.That(plan.GetQueryBounds(1), Is.EqualTo(delayedLiquidBounds));
        }

        [TestCase(WaterSimulationMode.LegacyCell, false)]
        [TestCase(WaterSimulationMode.GpuPbf, false)]
        public void BroadphasePlan_WithoutUsablePbfSnapshotUsesOnlyLegacyBounds(
            WaterSimulationMode mode,
            bool hasLiquidSnapshot)
        {
            var legacyBounds = new Bounds(Vector3.one, Vector3.one * 2f);

            ElementExposureBroadphasePlan plan = ElementExposureBroadphasePlanner.Plan(
                mode,
                legacyBounds,
                hasLiquidSnapshot,
                new Bounds(Vector3.one * 50f, Vector3.one));

            Assert.That(plan.QueryCount, Is.EqualTo(1));
            Assert.That(plan.GetQueryBounds(0), Is.EqualTo(legacyBounds));
        }

        [TestCase(float.NaN, 0.25f)]
        [TestCase(float.PositiveInfinity, 0.25f)]
        [TestCase(float.NegativeInfinity, 0.25f)]
        [TestCase(0f, 0.05f)]
        [TestCase(-1f, 0.05f)]
        [TestCase(0.4f, 0.4f)]
        public void ReadbackIntervalPolicy_PreventsInvalidValuesFromRequestingEveryFrame(
            float configured,
            float expected)
        {
            Assert.That(
                FluidReadbackIntervalPolicy.Sanitize(configured),
                Is.EqualTo(expected));
        }
    }
}

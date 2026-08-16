using System.Reflection;
using Game.Combat;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class ElementWorldFluidRoutingTests
    {
        [TestCase(WaterSimulationMode.LegacyCell, ElementMaterialKind.Water, 1, 0)]
        [TestCase(WaterSimulationMode.GpuPbf, ElementMaterialKind.Water, 0, 1)]
        [TestCase(WaterSimulationMode.LegacyCell, ElementMaterialKind.Fire, 1, 0)]
        [TestCase(WaterSimulationMode.GpuPbf, ElementMaterialKind.Fire, 1, 0)]
        public void RequestReachesExactlyOneExpectedSink(
            WaterSimulationMode mode,
            ElementMaterialKind material,
            int expectedCellWrites,
            int expectedFluidWrites)
        {
            var cell = new RecordingCellSink(true);
            var fluid = new RecordingFluidSink(true);
            ElementWriteRequest request = Request(material);

            bool accepted = ElementWorldWriteRouter.TryRoute(
                mode,
                in request,
                cell,
                fluid);

            Assert.That(accepted, Is.True);
            Assert.That(cell.WriteCount, Is.EqualTo(expectedCellWrites));
            Assert.That(fluid.DepositCount, Is.EqualTo(expectedFluidWrites));
        }

        [Test]
        public void GpuPbfWaterWithoutFluidSinkIsRejectedWithoutLegacyFallback()
        {
            var cell = new RecordingCellSink(true);
            ElementWriteRequest request = Request(ElementMaterialKind.Water);

            bool accepted = ElementWorldWriteRouter.TryRoute(
                WaterSimulationMode.GpuPbf,
                in request,
                cell,
                fluidSink: null);

            Assert.That(accepted, Is.False);
            Assert.That(cell.WriteCount, Is.Zero);
        }

        [Test]
        public void GpuPbfWaterWithUninitializedFluidSinkIsRejectedWithoutLegacyFallback()
        {
            var cell = new RecordingCellSink(true);
            var fluid = new RecordingFluidSink(true, isInitialized: false);
            ElementWriteRequest request = Request(ElementMaterialKind.Water);

            bool accepted = ElementWorldWriteRouter.TryRoute(
                WaterSimulationMode.GpuPbf,
                in request,
                cell,
                fluid);

            Assert.That(accepted, Is.False);
            Assert.That(cell.WriteCount, Is.Zero);
            Assert.That(fluid.DepositCount, Is.Zero,
                "未初始化状态应在 Router 边界直接拒绝，不应误入 Runtime Queue。");
        }

        [Test]
        public void GpuPbfWaterPropagatesFluidQueueRejectionWithoutLegacyFallback()
        {
            var cell = new RecordingCellSink(true);
            var fluid = new RecordingFluidSink(false);
            ElementWriteRequest request = Request(ElementMaterialKind.Water);

            bool accepted = ElementWorldWriteRouter.TryRoute(
                WaterSimulationMode.GpuPbf,
                in request,
                cell,
                fluid);

            Assert.That(accepted, Is.False);
            Assert.That(cell.WriteCount, Is.Zero);
            Assert.That(fluid.DepositCount, Is.EqualTo(1));
        }

        [Test]
        public void ProfileDefaultsToLegacyAndSnapshotsCellWaterToggle()
        {
            ElementWorldProfile profile = ScriptableObject.CreateInstance<ElementWorldProfile>();
            ElementReactionProfile reaction = ScriptableObject.CreateInstance<ElementReactionProfile>();
            try
            {
                SetPrivateField(profile, "_reactionProfile", reaction);
                Assert.That((int)WaterSimulationMode.LegacyCell, Is.Zero);
                Assert.That(profile.WaterSimulationMode, Is.EqualTo(WaterSimulationMode.LegacyCell));
                Assert.That(profile.CreateSimulationSettings().SimulateCellWater, Is.True);

                SetPrivateField(profile, "_waterSimulationMode", WaterSimulationMode.GpuPbf);
                Assert.That(profile.CreateSimulationSettings().SimulateCellWater, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(reaction);
            }
        }

        [Test]
        public void DepositConversionUsesCeilMonotonicSeedAndFalloffFlag()
        {
            var queue = new FluidSpawnQueue(2);
            var adapter = new FluidDepositQueueAdapter(
                queue,
                amountUnitsPerParticle: 8u,
                particleCapacity: 64);
            var request = new ElementWriteRequest(
                Vector3.one,
                ElementMaterialKind.Water,
                totalAmount: 17,
                radius: 0.75f,
                useLinearFalloff: true,
                initialVelocity: new Vector3(2f, 3f, 4f));

            Assert.That(adapter.TryEnqueueDeposit(in request), Is.True);
            Assert.That(adapter.TryEnqueueDeposit(in request), Is.True);
            var copied = new FluidSpawnRequest[2];
            Assert.That(queue.CopyAndClear(copied), Is.EqualTo(2));
            Assert.That(copied[0].ParticleCount, Is.EqualTo(3u));
            Assert.That(copied[0].InitialVelocity, Is.EqualTo(new Vector3(2f, 3f, 4f)));
            Assert.That(copied[0].Radius, Is.EqualTo(0.75f));
            Assert.That(copied[0].Flags, Is.EqualTo(FluidSpawnFlags.UseLinearFalloff));
            Assert.That(copied[1].Seed, Is.EqualTo(copied[0].Seed + 1u));
        }

        [Test]
        public void DepositAboveParticleCapacityIsRejectedInsteadOfClamped()
        {
            var queue = new FluidSpawnQueue(1);
            var adapter = new FluidDepositQueueAdapter(
                queue,
                amountUnitsPerParticle: 8u,
                particleCapacity: 2);
            ElementWriteRequest request = Request(ElementMaterialKind.Water, totalAmount: 17);

            Assert.That(adapter.TryEnqueueDeposit(in request), Is.False);
            Assert.That(queue.Count, Is.Zero);
        }

        [Test]
        public void RejectedQueueDoesNotAdvanceDepositSeed()
        {
            var queue = new FluidSpawnQueue(1);
            var adapter = new FluidDepositQueueAdapter(queue, 8u, 64);
            ElementWriteRequest request = Request(ElementMaterialKind.Water);
            Assert.That(adapter.TryEnqueueDeposit(in request), Is.True);
            Assert.That(adapter.TryEnqueueDeposit(in request), Is.False);

            var copied = new FluidSpawnRequest[1];
            queue.CopyAndClear(copied);
            Assert.That(adapter.TryEnqueueDeposit(in request), Is.True);
            queue.CopyAndClear(copied);
            Assert.That(copied[0].Seed, Is.EqualTo(1u));
        }

        private static ElementWriteRequest Request(
            ElementMaterialKind material,
            ushort totalAmount = 16)
        {
            return new ElementWriteRequest(Vector3.zero, material, totalAmount, 0.5f, false);
        }

        private static void SetPrivateField<T>(object target, string fieldName, T value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少序列化字段 {fieldName}。");
            field.SetValue(target, value);
        }

        private sealed class RecordingCellSink : IElementWriteSink
        {
            private readonly bool _result;

            public RecordingCellSink(bool result) => _result = result;
            public int WriteCount { get; private set; }

            public bool TryEnqueueWrite(in ElementWriteRequest request)
            {
                WriteCount++;
                return _result;
            }
        }

        private sealed class RecordingFluidSink : IFluidDepositSink
        {
            private readonly bool _result;

            private readonly bool _isInitialized;

            public RecordingFluidSink(bool result, bool isInitialized = true)
            {
                _result = result;
                _isInitialized = isInitialized;
            }
            public int DepositCount { get; private set; }

            public bool IsFluidInitialized => _isInitialized;

            public bool TryEnqueueDeposit(in ElementWriteRequest request)
            {
                DepositCount++;
                return _result;
            }
        }
    }
}

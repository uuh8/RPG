using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidGameplaySnapshotTests
    {
        [Test]
        public void PackedLayout_UsesOneFloat4AndActiveMaterialPlusOneTag()
        {
            Assert.That(FluidGameplaySampleCodec.Stride, Is.EqualTo(16));
            Assert.That(FluidGameplaySampleCodec.EncodeTag(false, ElementMaterialKind.Water), Is.Zero);
            Assert.That(
                FluidGameplaySampleCodec.EncodeTag(true, ElementMaterialKind.Water),
                Is.EqualTo((float)((byte)ElementMaterialKind.Water + 1)));
        }

        [Test]
        public void Rebuild_UsesFloorForNegativeWorldCoordinates()
        {
            using var samples = Samples(
                new Vector4(-0.01f, 0.25f, 0.25f, WaterTag()));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 8);

            Assert.That(snapshot.TryRebuild(
                samples,
                Metadata(new Bounds(Vector3.zero, new Vector3(2f, 1f, 1f)))), Is.True);
            Assert.That(snapshot.TryGetAmount(
                new Vector3Int(-1, 0, 0), ElementMaterialKind.Water, out byte amount), Is.True);
            Assert.That(amount, Is.EqualTo(8));
        }

        [Test]
        public void Rebuild_MaxBoundsEdgeIsExclusive()
        {
            using var samples = Samples(
                new Vector4(0.9999f, 0.25f, 0.25f, WaterTag()),
                new Vector4(1f, 0.25f, 0.25f, WaterTag()));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);

            Assert.That(snapshot.TryRebuild(
                samples,
                Metadata(new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one))), Is.True);
            Assert.That(snapshot.TryGetAmount(
                Vector3Int.zero, ElementMaterialKind.Water, out byte amount), Is.True);
            Assert.That(amount, Is.EqualTo(8));
        }

        [Test]
        public void Rebuild_AccumulatesInFixedSlotOrderAndSaturatesAtByteMax()
        {
            var samples = new NativeArray<Vector4>(40, Allocator.Temp);
            try
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = new Vector4(0.25f, 0.25f, 0.25f, WaterTag());
                var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);

                Assert.That(snapshot.TryRebuild(
                    samples,
                    Metadata(new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one))), Is.True);
                Assert.That(snapshot.TryGetAmount(
                    Vector3Int.zero, ElementMaterialKind.Water, out byte amount), Is.True);
                Assert.That(amount, Is.EqualTo(byte.MaxValue));
            }
            finally
            {
                samples.Dispose();
            }
        }

        [Test]
        public void Rebuild_SkipsInactiveAndNonWaterSamples()
        {
            using var samples = Samples(
                new Vector4(0.25f, 0.25f, 0.25f, 0f),
                new Vector4(0.25f, 0.25f, 0.25f,
                    FluidGameplaySampleCodec.EncodeTag(true, ElementMaterialKind.Fire)));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);

            Assert.That(snapshot.TryRebuild(
                samples,
                Metadata(new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one))), Is.True);
            Assert.That(snapshot.TryGetAmount(
                Vector3Int.zero, ElementMaterialKind.Water, out byte amount), Is.True);
            Assert.That(amount, Is.Zero);
            Assert.That(snapshot.TryGetAmount(
                Vector3Int.zero, ElementMaterialKind.Fire, out _), Is.False);
        }

        [Test]
        public void Rebuild_SkipsNonFiniteParticlePositions()
        {
            using var samples = Samples(
                new Vector4(float.NaN, 0.25f, 0.25f, WaterTag()),
                new Vector4(float.PositiveInfinity, 0.25f, 0.25f, WaterTag()));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);

            Assert.That(snapshot.TryRebuild(
                samples,
                Metadata(new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one))), Is.True);
            Assert.That(snapshot.TryGetAmount(
                Vector3Int.zero, ElementMaterialKind.Water, out byte amount), Is.True);
            Assert.That(amount, Is.Zero);
        }

        [Test]
        public void Rebuild_AfterBoundsTranslationClearsOldCellsAndPublishesFrozenVersions()
        {
            using var first = Samples(new Vector4(0.25f, 0.25f, 0.25f, WaterTag()));
            using var second = Samples(new Vector4(4.25f, 0.25f, 0.25f, WaterTag()));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);
            snapshot.TryRebuild(first, Metadata(
                new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one), topology: 7u));

            Assert.That(snapshot.TryRebuild(second, Metadata(
                new Bounds(new Vector3(4.5f, 0.5f, 0.5f), Vector3.one), topology: 9u)), Is.True);
            Assert.That(snapshot.TryGetAmount(Vector3Int.zero, ElementMaterialKind.Water, out _), Is.False);
            Assert.That(snapshot.TryGetAmount(
                new Vector3Int(4, 0, 0), ElementMaterialKind.Water, out byte amount), Is.True);
            Assert.That(amount, Is.EqualTo(8));
            Assert.That(snapshot.TopologyVersion, Is.EqualTo(9u));
            Assert.That(snapshot.LayoutVersion, Is.EqualTo(FluidGpuLayout.LayoutVersion));
        }

        [Test]
        public void InvalidFrozenMetadataFailsWithoutReplacingPreviousSnapshot()
        {
            using var samples = Samples(new Vector4(0.25f, 0.25f, 0.25f, WaterTag()));
            var snapshot = new LiquidGameplaySnapshot(maximumCellCount: 1);
            Assert.That(snapshot.TryRebuild(samples, Metadata(
                new Bounds(new Vector3(0.5f, 0.5f, 0.5f), Vector3.one), topology: 3u)), Is.True);

            var invalid = new FluidGameplayReadbackMetadata(
                new Bounds(new Vector3(float.NaN, 0f, 0f), Vector3.one),
                new Vector3(float.PositiveInfinity, 0f, 0f),
                1f,
                64,
                8,
                FluidGpuLayout.LayoutVersion,
                topologyVersion: 4u,
                snapshotVersion: 4u);
            Assert.That(FluidGameplayMetadataValidator.IsValid(in invalid), Is.False);
            Assert.That(snapshot.TryRebuild(samples, in invalid), Is.False);
            Assert.That(snapshot.TopologyVersion, Is.EqualTo(3u));
            Assert.That(snapshot.TryGetAmount(
                Vector3Int.zero, ElementMaterialKind.Water, out byte amount), Is.True);
            Assert.That(amount, Is.EqualTo(8));
        }

        private static NativeArray<Vector4> Samples(params Vector4[] values)
        {
            return new NativeArray<Vector4>(values, Allocator.Temp);
        }

        private static FluidGameplayReadbackMetadata Metadata(Bounds bounds, uint topology = 1u)
        {
            return new FluidGameplayReadbackMetadata(
                bounds,
                Vector3.zero,
                cellSize: 1f,
                particleCapacity: 64,
                amountUnitsPerParticle: 8,
                FluidGpuLayout.LayoutVersion,
                topology,
                snapshotVersion: topology);
        }

        private static float WaterTag()
        {
            return FluidGameplaySampleCodec.EncodeTag(true, ElementMaterialKind.Water);
        }
    }
}

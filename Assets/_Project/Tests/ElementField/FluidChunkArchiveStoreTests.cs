using Game.Materials;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidChunkArchiveStoreTests
    {
        [Test]
        public void Commit_WhenCapacityIsInsufficient_PreservesPreviousArchive()
        {
            var store = new FluidChunkArchiveStore(2, 1);
            Assert.That(store.TryCommit(Builder(MaterialId.Water, Vector3.zero), 1u), Is.True);
            Assert.That(store.TryCommit(Builder(MaterialId.Poison, new Vector3(8f, 0f, 0f)), 2u), Is.False);
            Assert.That(store.TryGetAmount(Vector3Int.zero, MaterialId.Water, out uint amount), Is.True);
            Assert.That(amount, Is.EqualTo(8u));
            Assert.That(store.ChunkCount, Is.EqualTo(1));
        }

        [Test]
        public void RemoveChunk_TombstoneKeepsOtherRecordsQueryable()
        {
            var store = new FluidChunkArchiveStore(4, 4);
            Assert.That(store.TryCommit(Builder(MaterialId.Water, Vector3.zero), 1u), Is.True);
            Assert.That(store.TryCommit(Builder(MaterialId.Poison, new Vector3(8f, 0f, 0f)), 2u), Is.True);
            store.RemoveChunk(new ElementChunkKey(0, 0, 0));
            Assert.That(store.TryGetAmount(new Vector3Int(8, 0, 0), MaterialId.Poison, out uint amount), Is.True);
            Assert.That(amount, Is.EqualTo(4u));
        }

        [Test]
        public void Commit_ClassifiesRetainedChunkAsDormantAndAdvancesVersion()
        {
            var store = new FluidChunkArchiveStore(4, 4);
            var retained = new FluidChunkRegion(
                new ElementChunkKey(-1, -1, -1), new ElementChunkKey(1, 1, 1));

            Assert.That(store.TryCommit(
                Builder(MaterialId.Water, Vector3.zero), 7u, in retained), Is.True);
            Assert.That(store.TryGetChunkKind(
                new ElementChunkKey(0, 0, 0), out FluidArchivedChunkKind kind), Is.True);
            Assert.That(kind, Is.EqualTo(FluidArchivedChunkKind.Dormant));
            Assert.That(store.DormantChunkCount, Is.EqualTo(1));
            Assert.That(store.DormantRecordCount, Is.EqualTo(1));
            Assert.That(store.DormantAmount, Is.EqualTo(8u));
            Assert.That(store.Version, Is.EqualTo(1u));

            store.RemoveChunk(new ElementChunkKey(0, 0, 0));
            Assert.That(store.DormantChunkCount, Is.Zero);
            Assert.That(store.DormantRecordCount, Is.Zero);
            Assert.That(store.DormantAmount, Is.Zero);
            Assert.That(store.Version, Is.EqualTo(2u));
        }

        private static FluidChunkArchiveBuilder Builder(MaterialId material, Vector3 position)
        {
            var samples = new NativeArray<FluidGpuArchiveSample>(1, Allocator.Temp);
            try
            {
                samples[0] = new FluidGpuArchiveSample(position, material, Vector3.zero,
                    FluidGpuLayout.AliveFlag | FluidActivityFlags.ArchiveLocked);
                var builder = new FluidChunkArchiveBuilder(1);
                var scales = LiquidMaterialAmountScaleSnapshot.Create(new[]
                {
                    new LiquidMaterialAmountScale(MaterialId.Water, 8u),
                    new LiquidMaterialAmountScale(MaterialId.Poison, 4u),
                });
                Assert.That(builder.TryRebuild(samples, new FluidArchiveBuildContext(Vector3.zero, 1f, 8, scales)), Is.True);
                return builder;
            }
            finally { samples.Dispose(); }
        }
    }
}

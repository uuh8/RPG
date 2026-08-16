using Game.Combat;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidFireReactionSystemTests
    {
        private static readonly ExtinguishTuning Tuning = new ExtinguishTuning
        {
            FormalThreshold = 20f,
            LowRatePerSecond = 5f,
            FormalRatePerSecond = 40f,
        };

        [Test]
        public void Plan_UsesSameCellAndSixAxialNeighbors_ThenDeduplicatesSnapshotVersion()
        {
            var occupancy = new FakeOccupancy(11u);
            occupancy.Set(new Vector3Int(1, 0, 0), 80);
            var fires = new[] { new FluidFireCellSample(Vector3Int.zero, 100) };
            var queue = new FluidReactionCommandQueue(8);
            var system = new FluidFireReactionSystem(8);

            Assert.That(system.TryPlanAndCommit(occupancy, fires, 1, 1f, in Tuning, 8u, queue), Is.True);
            Assert.That(queue.PendingFireDeltaCount, Is.EqualTo(1));
            Assert.That(queue.PendingConsumeCount, Is.EqualTo(1));
            Assert.That(queue.TryDequeueFireDelta(out ElementCellDeltaRequest formalDelta), Is.True);
            Assert.That(formalDelta.AmountToRemove, Is.EqualTo(80),
                "两边都跨过 FormalThreshold 后应使用 Formal Rate，并受 Water 存量上限限制。");
            Assert.That(system.TryPlanAndCommit(occupancy, fires, 1, 1f, in Tuning, 8u, queue), Is.False);
        }

        [Test]
        public void Plan_LowIntensityUsesLowRateAndParticleQuantizationIsBelowOneParticle()
        {
            var occupancy = new FakeOccupancy(12u);
            occupancy.Set(Vector3Int.zero, 3);
            var fires = new[] { new FluidFireCellSample(Vector3Int.zero, 10) };
            var queue = new FluidReactionCommandQueue(4);
            var system = new FluidFireReactionSystem(4);

            Assert.That(system.TryPlanAndCommit(occupancy, fires, 1, 1f, in Tuning, 8u, queue), Is.True);
            Assert.That(queue.TryDequeueFireDelta(out ElementCellDeltaRequest delta), Is.True);
            Assert.That(delta.AmountToRemove, Is.EqualTo(3));
            var commands = new FluidConsumeCommand[1];
            Assert.That(queue.CopyConsumeCommandsAndClear(commands), Is.EqualTo(1));
            Assert.That(commands[0].MaximumParticleCount, Is.EqualTo(1u));
            Assert.That(commands[0].MaximumParticleCount * 8u - delta.AmountToRemove, Is.LessThan(8u));
        }

        [Test]
        public void Plan_ReservesSharedWaterAcrossMultipleFireCells()
        {
            var occupancy = new FakeOccupancy(13u);
            occupancy.Set(Vector3Int.zero, 100);
            var fires = new[]
            {
                new FluidFireCellSample(Vector3Int.left, 10),
                new FluidFireCellSample(Vector3Int.right, 10),
            };
            var queue = new FluidReactionCommandQueue(8);
            var system = new FluidFireReactionSystem(8);

            Assert.That(system.TryPlanAndCommit(occupancy, fires, 2, 1f, in Tuning, 8u, queue), Is.True);
            int removed = 0;
            while (queue.TryDequeueFireDelta(out ElementCellDeltaRequest delta))
                removed += delta.AmountToRemove;
            Assert.That(removed, Is.EqualTo(20));
            var consume = new FluidConsumeCommand[2];
            Assert.That(queue.CopyConsumeCommandsAndClear(consume), Is.EqualTo(1));
            Assert.That(consume[0].MaximumParticleCount, Is.EqualTo(3u));
            Assert.That(consume[0].MaximumParticleCount * 8u - (uint)removed, Is.LessThan(8u));
        }

        [Test]
        public void Plan_QueueFailureDoesNotConsumeVersionAndCanRetry()
        {
            var occupancy = new FakeOccupancy(14u);
            occupancy.Set(Vector3Int.zero, 80);
            var fires = new[] { new FluidFireCellSample(Vector3Int.zero, 100) };
            var fullQueue = new FluidReactionCommandQueue(1);
            var fillerFire = new[] { new ElementCellDeltaRequest(Vector3Int.one, ElementMaterialKind.Fire, 1) };
            var fillerConsume = new[] { new FluidConsumeCommand(Vector3Int.one, Vector3Int.one, 0u, 1u, 1u) };
            fullQueue.TryEnqueueBatch(fillerFire, fillerConsume, 1);
            var system = new FluidFireReactionSystem(4);

            Assert.That(system.TryPlanAndCommit(occupancy, fires, 1, 1f, in Tuning, 8u, fullQueue), Is.False);
            fullQueue.TryDequeueFireDelta(out _);
            fullQueue.CopyConsumeCommandsAndClear(new FluidConsumeCommand[1]);
            Assert.That(system.TryPlanAndCommit(occupancy, fires, 1, 1f, in Tuning, 8u, fullQueue), Is.True);
        }

        private sealed class FakeOccupancy : ILiquidOccupancyReadOnly
        {
            private readonly Vector3Int[] _cells = new Vector3Int[8];
            private readonly byte[] _amounts = new byte[8];
            private int _count;

            internal FakeOccupancy(uint version) => SnapshotVersion = version;
            public bool HasValidSnapshot => true;
            public Bounds SnapshotBounds => new Bounds(Vector3.zero, Vector3.one * 100f);
            public uint SnapshotVersion { get; }
            public uint AmountUnitsPerParticle => 8u;

            internal void Set(Vector3Int cell, byte amount)
            {
                _cells[_count] = cell;
                _amounts[_count++] = amount;
            }

            public bool TryGetAmount(Vector3Int globalCell, ElementMaterialKind materialKind, out byte amount)
            {
                if (materialKind == ElementMaterialKind.Water)
                {
                    for (int i = 0; i < _count; i++)
                    {
                        if (_cells[i] == globalCell)
                        {
                            amount = _amounts[i];
                            return true;
                        }
                    }
                }

                amount = 0;
                return false;
            }
        }
    }
}

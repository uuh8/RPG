using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidReactionCommandQueueTests
    {
        [Test]
        public void Batch_WhenEitherSideHasNoCapacity_CommitsNeitherSide()
        {
            var queue = new FluidReactionCommandQueue(1);
            var fire = new[]
            {
                new ElementCellDeltaRequest(Vector3Int.zero, ElementMaterialKind.Fire, 3),
                new ElementCellDeltaRequest(Vector3Int.right, ElementMaterialKind.Fire, 4),
            };
            var consume = new[]
            {
                new FluidConsumeCommand(Vector3Int.zero, Vector3Int.zero, 0u, 1u, 7u),
                new FluidConsumeCommand(Vector3Int.right, Vector3Int.right, 0u, 1u, 8u),
            };

            Assert.That(queue.TryEnqueueBatch(fire, consume, 2), Is.False);
            Assert.That(queue.PendingFireDeltaCount, Is.Zero);
            Assert.That(queue.PendingConsumeCount, Is.Zero);
            Assert.That(queue.RejectedBatchCount, Is.EqualTo(1));
        }

        [Test]
        public void Batch_ExposesIndependentFifoConsumersAfterAtomicCommit()
        {
            var queue = new FluidReactionCommandQueue(2);
            var fire = new[]
            {
                new ElementCellDeltaRequest(new Vector3Int(-1, 0, 0), ElementMaterialKind.Fire, 3),
                new ElementCellDeltaRequest(new Vector3Int(2, 0, 0), ElementMaterialKind.Fire, 4),
            };
            var consume = new[]
            {
                new FluidConsumeCommand(new Vector3Int(-1, 0, 0), Vector3Int.zero, 0u, 1u, 7u),
                new FluidConsumeCommand(new Vector3Int(2, 0, 0), new Vector3Int(3, 0, 0), 0u, 2u, 8u),
            };

            Assert.That(queue.TryEnqueueBatch(fire, consume, 2), Is.True);
            Assert.That(queue.TryDequeueFireDelta(out ElementCellDeltaRequest first), Is.True);
            Assert.That(first.GlobalCell, Is.EqualTo(new Vector3Int(-1, 0, 0)));

            var destination = new FluidConsumeCommand[2];
            Assert.That(queue.CopyConsumeCommandsAndClear(destination), Is.EqualTo(2));
            Assert.That(destination[0].MinimumCell, Is.EqualTo(new Vector3Int(-1, 0, 0)));
            Assert.That(destination[1].MaximumParticleCount, Is.EqualTo(2u));
            Assert.That(queue.PendingConsumeCount, Is.Zero);
            Assert.That(queue.PendingFireDeltaCount, Is.EqualTo(1));
        }
    }
}

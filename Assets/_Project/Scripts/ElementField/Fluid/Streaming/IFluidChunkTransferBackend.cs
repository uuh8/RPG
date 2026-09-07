using Unity.Collections;
using UnityEngine;

namespace Game.ElementField
{
    internal readonly struct FluidArchiveReadbackLease
    {
        public readonly GraphicsBuffer Samples;
        public readonly int ParticleCapacity;
        public readonly Bounds RetainedBounds;
        public readonly Vector3 WorldOrigin;
        public readonly float CellSize;
        public readonly int ChunkSize;
        public readonly uint TransactionId;
        public readonly uint LayoutVersion;
        public readonly uint TopologyVersion;
        public readonly LiquidMaterialAmountScaleSnapshot AmountScales;

        public FluidArchiveReadbackLease(GraphicsBuffer samples, int particleCapacity,
            Bounds retainedBounds, Vector3 worldOrigin, float cellSize, int chunkSize,
            uint transactionId, uint layoutVersion, uint topologyVersion,
            LiquidMaterialAmountScaleSnapshot amountScales)
        {
            Samples = samples; ParticleCapacity = particleCapacity; RetainedBounds = retainedBounds;
            WorldOrigin = worldOrigin; CellSize = cellSize; ChunkSize = chunkSize;
            TransactionId = transactionId; LayoutVersion = layoutVersion;
            TopologyVersion = topologyVersion; AmountScales = amountScales;
        }
    }

    internal readonly struct FluidRestoreStatusReadbackLease
    {
        public readonly GraphicsBuffer Status;
        public readonly int ParticleCount;
        public readonly uint TransactionId;

        public FluidRestoreStatusReadbackLease(
            GraphicsBuffer status, int particleCount, uint transactionId)
        {
            Status = status;
            ParticleCount = particleCount;
            TransactionId = transactionId;
        }
    }

    internal interface IFluidChunkTransferBackend
    {
        bool TryGetParticleCapacity(out int particleCapacity);
        bool TryGetFreeParticleCount(out uint freeParticleCount);
        bool TryGetActivityCounts(out uint awakeParticleCount, out uint sleepingParticleCount);
        bool TryAcquireArchiveReadbackLease(Bounds retainedBounds, Vector3 worldOrigin,
            float cellSize, int chunkSize, bool includeSleepingRetained,
            uint transactionId, out FluidArchiveReadbackLease lease);
        void CommitArchiveAndRelease(uint transactionId);
        void CancelArchive(uint transactionId);
        bool TryAcquireRestoreStatusLease(
            NativeArray<FluidGpuRestoreParticle> particles,
            int particleCount,
            int gameplayReserveParticles,
            uint transactionId,
            out FluidRestoreStatusReadbackLease lease);
        void CommitRestore(uint transactionId, int particleCount, out uint publishedTopologyVersion);
        void RollbackRestore(uint transactionId, int particleCount);
        void ReleaseTransferReadbackLease();
    }
}

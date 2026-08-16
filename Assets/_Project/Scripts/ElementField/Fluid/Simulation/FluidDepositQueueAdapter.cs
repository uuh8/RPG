using System;

namespace Game.ElementField
{
    /// <summary>
    /// 把 Legacy Amount 单位无损量化成一次完整 Spawn Command。这里的“无损”指永不静默 clamp：
    /// 尾数用 Ceiling 保留；若完整粒子数超过 Pool Capacity，则整笔拒绝，由调用方决定反馈策略。
    /// </summary>
    public sealed class FluidDepositQueueAdapter : IFluidDepositSink
    {
        private const uint WaterMaterialId = (uint)ElementMaterialKind.Water;

        private readonly IFluidSpawnSink _spawnSink;
        private readonly uint _amountUnitsPerParticle;
        private readonly uint _particleCapacity;
        private uint _nextSeed;

        public FluidDepositQueueAdapter(
            IFluidSpawnSink spawnSink,
            uint amountUnitsPerParticle,
            int particleCapacity)
        {
            _spawnSink = spawnSink ?? throw new ArgumentNullException(nameof(spawnSink));
            if (amountUnitsPerParticle == 0u)
                throw new ArgumentOutOfRangeException(nameof(amountUnitsPerParticle));
            if (particleCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(particleCapacity));

            _amountUnitsPerParticle = amountUnitsPerParticle;
            _particleCapacity = (uint)particleCapacity;
        }

        public bool IsFluidInitialized => true;

        public bool TryEnqueueDeposit(in ElementWriteRequest request)
        {
            if (!IsFluidInitialized || request.MaterialKind != ElementMaterialKind.Water)
                return false;

            uint totalAmount = request.TotalAmount;
            uint particleCount = totalAmount / _amountUnitsPerParticle;
            if (totalAmount % _amountUnitsPerParticle != 0u)
                particleCount++;
            if (particleCount == 0u || particleCount > _particleCapacity)
                return false;

            uint flags = request.UseLinearFalloff
                ? FluidSpawnFlags.UseLinearFalloff
                : 0u;
            var spawn = new FluidSpawnRequest(
                request.WorldPosition,
                request.InitialVelocity,
                request.Radius,
                particleCount,
                WaterMaterialId,
                _nextSeed,
                flags);
            if (!_spawnSink.TryEnqueueSpawn(in spawn))
                return false;

            unchecked
            {
                _nextSeed++;
            }
            return true;
        }
    }
}

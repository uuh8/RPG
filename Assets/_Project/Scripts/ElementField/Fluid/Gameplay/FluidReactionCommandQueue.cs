using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>只允许 Runtime 内部扣减既有 Fire；公共 Projectile 写入口永远看不到负 Amount。</summary>
    public readonly struct ElementCellDeltaRequest
    {
        public ElementCellDeltaRequest(
            Vector3Int globalCell,
            ElementMaterialKind expectedMaterial,
            byte amountToRemove)
        {
            GlobalCell = globalCell;
            ExpectedMaterial = expectedMaterial;
            AmountToRemove = amountToRemove;
        }

        public Vector3Int GlobalCell { get; }
        public ElementMaterialKind ExpectedMaterial { get; }
        public byte AmountToRemove { get; }
    }

    /// <summary>
    /// CPU/HLSL 共用的 48-byte 消耗命令。AABB 使用 inclusive Global Cell；GPU 只释放匹配材质的粒子。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidConsumeCommand
    {
        public readonly FluidGpuInt4 MinimumCellAndPadding;
        public readonly FluidGpuInt4 MaximumCellAndParticleCount;
        public readonly FluidGpuUInt4 MaterialSeedPadding;

        public FluidConsumeCommand(
            Vector3Int minimumCell,
            Vector3Int maximumCell,
            uint materialId,
            uint maximumParticleCount,
            uint seed)
        {
            MinimumCellAndPadding = new FluidGpuInt4(
                minimumCell.x, minimumCell.y, minimumCell.z, 0);
            MaximumCellAndParticleCount = new FluidGpuInt4(
                maximumCell.x, maximumCell.y, maximumCell.z,
                unchecked((int)maximumParticleCount));
            MaterialSeedPadding = new FluidGpuUInt4(materialId, seed, 0u, 0u);
        }

        public Vector3Int MinimumCell => new Vector3Int(
            MinimumCellAndPadding.X,
            MinimumCellAndPadding.Y,
            MinimumCellAndPadding.Z);
        public Vector3Int MaximumCell => new Vector3Int(
            MaximumCellAndParticleCount.X,
            MaximumCellAndParticleCount.Y,
            MaximumCellAndParticleCount.Z);
        public uint MaximumParticleCount => unchecked((uint)MaximumCellAndParticleCount.W);
    }

    /// <summary>
    /// 两条固定容量 Ring Buffer 共用一次 Batch Admission。只有 Fire Delta 与 GPU Consume 都放得下时
    /// 才同时提交，避免“火先灭了，但水粒子因队列满没有减少”的跨 Timeline 失守恒。
    /// </summary>
    public sealed class FluidReactionCommandQueue
    {
        private readonly ElementCellDeltaRequest[] _fireDeltas;
        private readonly FluidConsumeCommand[] _consumeCommands;
        private int _fireHead;
        private int _fireCount;
        private int _consumeHead;
        private int _consumeCount;

        public FluidReactionCommandQueue(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _fireDeltas = new ElementCellDeltaRequest[capacity];
            _consumeCommands = new FluidConsumeCommand[capacity];
        }

        public int Capacity => _fireDeltas.Length;
        public int PendingFireDeltaCount => _fireCount;
        public int PendingConsumeCount => _consumeCount;
        public uint RejectedBatchCount { get; private set; }

        public bool TryEnqueueBatch(
            ElementCellDeltaRequest[] fireDeltas,
            FluidConsumeCommand[] consumeCommands,
            int count)
        {
            return TryEnqueueBatch(fireDeltas, count, consumeCommands, count);
        }

        public bool TryEnqueueBatch(
            ElementCellDeltaRequest[] fireDeltas,
            int fireDeltaCount,
            FluidConsumeCommand[] consumeCommands,
            int consumeCommandCount)
        {
            if (fireDeltas == null)
                throw new ArgumentNullException(nameof(fireDeltas));
            if (consumeCommands == null)
                throw new ArgumentNullException(nameof(consumeCommands));
            if (fireDeltaCount < 0 || fireDeltaCount > fireDeltas.Length)
                throw new ArgumentOutOfRangeException(nameof(fireDeltaCount));
            if (consumeCommandCount < 0 || consumeCommandCount > consumeCommands.Length)
                throw new ArgumentOutOfRangeException(nameof(consumeCommandCount));
            if (fireDeltaCount == 0 || consumeCommandCount == 0)
                return false;
            if (fireDeltaCount > Capacity - _fireCount
                || consumeCommandCount > Capacity - _consumeCount)
            {
                if (RejectedBatchCount < uint.MaxValue)
                    RejectedBatchCount++;
                return false;
            }

            for (int index = 0; index < fireDeltaCount; index++)
            {
                int fireTail = (_fireHead + _fireCount) % Capacity;
                _fireDeltas[fireTail] = fireDeltas[index];
                _fireCount++;
            }
            for (int index = 0; index < consumeCommandCount; index++)
            {
                int consumeTail = (_consumeHead + _consumeCount) % Capacity;
                _consumeCommands[consumeTail] = consumeCommands[index];
                _consumeCount++;
            }
            return true;
        }

        public bool TryDequeueFireDelta(out ElementCellDeltaRequest request)
        {
            if (_fireCount == 0)
            {
                request = default;
                return false;
            }
            request = _fireDeltas[_fireHead];
            _fireHead = (_fireHead + 1) % Capacity;
            _fireCount--;
            return true;
        }

        public int CopyConsumeCommandsAndClear(FluidConsumeCommand[] destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            int count = Math.Min(destination.Length, _consumeCount);
            for (int index = 0; index < count; index++)
                destination[index] = _consumeCommands[(_consumeHead + index) % Capacity];
            _consumeHead = (_consumeHead + count) % Capacity;
            _consumeCount -= count;
            return count;
        }
    }
}

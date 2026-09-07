using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public enum FluidArchivedChunkKind : byte
    {
        Cold = 0,
        Dormant = 1,
    }

    /// <summary>
    /// 固定容量 RAM Cold Store。Open Addressing 使用 Empty/Occupied/Tombstone，
    /// 删除 Chunk 后探测链仍连续；归档前先做完整容量检查，失败不会污染旧权威数据。
    /// </summary>
    public sealed class FluidChunkArchiveStore
    {
        private readonly FluidArchiveCellRecord[] _records;
        private readonly uint[] _recordVersions;
        private readonly byte[] _recordStates;
        private readonly ElementChunkKey[] _chunks;
        private readonly uint[] _chunkVersions;
        private readonly FluidArchivedChunkKind[] _chunkKinds;
        private readonly byte[] _chunkStates;
        private readonly int _recordMask;
        private readonly int _chunkMask;
        private readonly int _chunkSize;

        public int RecordCount { get; private set; }
        public int ChunkCount { get; private set; }
        public int DormantChunkCount { get; private set; }
        public int DormantRecordCount { get; private set; }
        public ulong DormantAmount { get; private set; }
        public uint Version { get; private set; }

        public FluidChunkArchiveStore(int maximumChunks, int maximumCellRecords, int chunkSize = 8)
        {
            if (maximumChunks <= 0) throw new ArgumentOutOfRangeException(nameof(maximumChunks));
            if (maximumCellRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCellRecords));
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            int chunkCapacity = TableCapacity(maximumChunks);
            int recordCapacity = TableCapacity(maximumCellRecords);
            _chunks = new ElementChunkKey[chunkCapacity];
            _chunkVersions = new uint[chunkCapacity];
            _chunkKinds = new FluidArchivedChunkKind[chunkCapacity];
            _chunkStates = new byte[chunkCapacity];
            _records = new FluidArchiveCellRecord[recordCapacity];
            _recordVersions = new uint[recordCapacity];
            _recordStates = new byte[recordCapacity];
            _chunkMask = chunkCapacity - 1;
            _recordMask = recordCapacity - 1;
            _chunkSize = chunkSize;
        }

        public bool TryCommit(FluidChunkArchiveBuilder builder, uint version)
        {
            return TryCommitCore(builder, version, false, default);
        }

        public bool TryCommit(
            FluidChunkArchiveBuilder builder,
            uint version,
            in FluidChunkRegion retainedRegion)
        {
            return TryCommitCore(builder, version, true, in retainedRegion);
        }

        private bool TryCommitCore(
            FluidChunkArchiveBuilder builder,
            uint version,
            bool classifyRetained,
            in FluidChunkRegion retainedRegion)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (builder.RecordCount == 0) return true;

            int newChunks = 0;
            for (int slot = 0; slot < builder.SlotCapacity; slot++)
            {
                if (!builder.TryGetRecordAtSlot(slot, out FluidArchiveCellRecord record)) continue;
                if (ContainsChunk(record.Chunk)) return false; // 同一 Chunk 只能有一个权威版本。
                bool seen = false;
                for (int prior = 0; prior < slot; prior++)
                {
                    if (builder.TryGetRecordAtSlot(prior, out FluidArchiveCellRecord other)
                        && other.Chunk == record.Chunk) { seen = true; break; }
                }
                if (!seen) newChunks++;
            }
            if (ChunkCount + newChunks > MaximumOccupied(_chunks.Length)
                || RecordCount + builder.RecordCount > MaximumOccupied(_records.Length)) return false;

            // 预检后所有 key 都一定能放入固定表；查询和 Commit 都在 ElementWorld 主线程串行执行。
            for (int slot = 0; slot < builder.SlotCapacity; slot++)
            {
                if (!builder.TryGetRecordAtSlot(slot, out FluidArchiveCellRecord record)) continue;
                FluidArchivedChunkKind kind = classifyRetained && retainedRegion.Contains(record.Chunk)
                    ? FluidArchivedChunkKind.Dormant
                    : FluidArchivedChunkKind.Cold;
                InsertChunk(record.Chunk, version, kind);
                InsertRecord(record, version);
            }
            IncrementVersion();
            return true;
        }

        public bool ContainsChunk(ElementChunkKey chunk) => FindChunk(chunk) >= 0;

        public bool TryFindNearestChunk(
            ElementChunkKey interestChunk, in FluidChunkRegion region, out ElementChunkKey chunk)
        {
            return TryFindNearestChunk(
                interestChunk, in region, FluidArchivedChunkKind.Cold, false, out chunk);
        }

        public bool TryFindNearestChunk(
            ElementChunkKey interestChunk,
            in FluidChunkRegion region,
            FluidArchivedChunkKind requiredKind,
            bool includeOtherKinds,
            out ElementChunkKey chunk)
        {
            long bestDistance = long.MaxValue;
            bool found = false;
            chunk = default;
            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunkStates[i] != 1 || !region.Contains(_chunks[i])
                    || (!includeOtherKinds && _chunkKinds[i] != requiredKind)) continue;
                ElementChunkKey candidate = _chunks[i];
                long distance = Math.Abs((long)candidate.X - interestChunk.X)
                    + Math.Abs((long)candidate.Y - interestChunk.Y)
                    + Math.Abs((long)candidate.Z - interestChunk.Z);
                if (!found || distance < bestDistance
                    || (distance == bestDistance && Compare(candidate, chunk) < 0))
                {
                    found = true;
                    bestDistance = distance;
                    chunk = candidate;
                }
            }
            return found;
        }

        public bool TryGetChunkVersion(ElementChunkKey chunk, out uint version)
        {
            int slot = FindChunk(chunk);
            version = slot >= 0 ? _chunkVersions[slot] : 0u;
            return slot >= 0;
        }

        public bool TryGetChunkKind(ElementChunkKey chunk, out FluidArchivedChunkKind kind)
        {
            int slot = FindChunk(chunk);
            kind = slot >= 0 ? _chunkKinds[slot] : FluidArchivedChunkKind.Cold;
            return slot >= 0;
        }

        public bool TryGetAmount(Vector3Int globalCell, MaterialId material, out uint amount)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                globalCell, _chunkSize, out ElementChunkKey chunk, out Vector3Int local);
            int localIndex = local.x + _chunkSize * (local.y + _chunkSize * local.z);
            int slot = FindRecord(chunk, localIndex, material);
            amount = slot >= 0 ? _records[slot].Amount : 0u;
            return slot >= 0;
        }

        public int CopyChunkRecords(ElementChunkKey chunk, FluidArchiveCellRecord[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int count = 0;
            for (int slot = 0; slot < _records.Length; slot++)
            {
                if (_recordStates[slot] != 1 || _records[slot].Chunk != chunk) continue;
                if (count >= destination.Length) return -1;
                destination[count++] = _records[slot];
            }
            return count;
        }

        public int CopyCells(
            in FluidChunkRegion region,
            FluidArchivedChunkKind kind,
            MaterialId material,
            LiquidMaterialCellSample[] destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int count = 0;
            for (int slot = 0; slot < _records.Length && count < destination.Length; slot++)
            {
                if (_recordStates[slot] != 1) continue;
                FluidArchiveCellRecord record = _records[slot];
                if (record.Material != material || !region.Contains(record.Chunk)
                    || !TryGetChunkKind(record.Chunk, out FluidArchivedChunkKind recordKind)
                    || recordKind != kind)
                    continue;
                destination[count++] = new LiquidMaterialCellSample(
                    ToGlobalCell(in record), (byte)Math.Min(byte.MaxValue, record.Amount));
            }
            return count;
        }

        public void RemoveChunk(ElementChunkKey chunk)
        {
            int chunkSlot = FindChunk(chunk);
            if (chunkSlot < 0) return;
            _chunkStates[chunkSlot] = 2;
            ChunkCount--;
            bool wasDormant = _chunkKinds[chunkSlot] == FluidArchivedChunkKind.Dormant;
            if (wasDormant) DormantChunkCount--;
            for (int slot = 0; slot < _records.Length; slot++)
            {
                if (_recordStates[slot] == 1 && _records[slot].Chunk == chunk)
                {
                    _recordStates[slot] = 2;
                    RecordCount--;
                    if (wasDormant) DormantRecordCount--;
                    if (wasDormant) DormantAmount -= _records[slot].Amount;
                }
            }
            IncrementVersion();
        }

        private void InsertChunk(
            ElementChunkKey chunk,
            uint version,
            FluidArchivedChunkKind kind)
        {
            int existing = FindChunk(chunk);
            if (existing >= 0) return;
            int slot = FindInsertChunkSlot(chunk);
            _chunks[slot] = chunk;
            _chunkVersions[slot] = version;
            _chunkKinds[slot] = kind;
            _chunkStates[slot] = 1;
            ChunkCount++;
            if (kind == FluidArchivedChunkKind.Dormant) DormantChunkCount++;
        }

        private void IncrementVersion()
        {
            unchecked { Version++; }
            if (Version == 0u) Version = 1u;
        }

        private Vector3Int ToGlobalCell(in FluidArchiveCellRecord record)
        {
            int layer = _chunkSize * _chunkSize;
            int localZ = record.LocalCellIndex / layer;
            int remainder = record.LocalCellIndex - localZ * layer;
            int localY = remainder / _chunkSize;
            int localX = remainder - localY * _chunkSize;
            return new Vector3Int(
                checked(record.Chunk.X * _chunkSize + localX),
                checked(record.Chunk.Y * _chunkSize + localY),
                checked(record.Chunk.Z * _chunkSize + localZ));
        }

        private void InsertRecord(FluidArchiveCellRecord record, uint version)
        {
            int slot = FindInsertRecordSlot(record.Chunk, record.LocalCellIndex, record.Material);
            _records[slot] = record; _recordVersions[slot] = version; _recordStates[slot] = 1; RecordCount++;
            if (TryGetChunkKind(record.Chunk, out FluidArchivedChunkKind kind)
                && kind == FluidArchivedChunkKind.Dormant)
            {
                DormantRecordCount++;
                DormantAmount += record.Amount;
            }
        }

        private int FindChunk(ElementChunkKey chunk)
        {
            int slot = HashChunk(chunk) & _chunkMask;
            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunkStates[slot] == 0) return -1;
                if (_chunkStates[slot] == 1 && _chunks[slot] == chunk) return slot;
                slot = (slot + 1) & _chunkMask;
            }
            return -1;
        }

        private int FindRecord(ElementChunkKey chunk, int local, MaterialId material)
        {
            int slot = HashRecord(chunk, local, material) & _recordMask;
            for (int i = 0; i < _records.Length; i++)
            {
                if (_recordStates[slot] == 0) return -1;
                FluidArchiveCellRecord record = _records[slot];
                if (_recordStates[slot] == 1 && record.Chunk == chunk
                    && record.LocalCellIndex == local && record.Material == material) return slot;
                slot = (slot + 1) & _recordMask;
            }
            return -1;
        }

        private int FindInsertChunkSlot(ElementChunkKey chunk)
        {
            int slot = HashChunk(chunk) & _chunkMask; int tombstone = -1;
            for (int i = 0; i < _chunks.Length; i++)
            {
                if (_chunkStates[slot] == 0) return tombstone >= 0 ? tombstone : slot;
                if (_chunkStates[slot] == 2 && tombstone < 0) tombstone = slot;
                slot = (slot + 1) & _chunkMask;
            }
            return tombstone;
        }

        private int FindInsertRecordSlot(ElementChunkKey chunk, int local, MaterialId material)
        {
            int slot = HashRecord(chunk, local, material) & _recordMask; int tombstone = -1;
            for (int i = 0; i < _records.Length; i++)
            {
                if (_recordStates[slot] == 0) return tombstone >= 0 ? tombstone : slot;
                if (_recordStates[slot] == 2 && tombstone < 0) tombstone = slot;
                slot = (slot + 1) & _recordMask;
            }
            return tombstone;
        }

        private static int MaximumOccupied(int capacity) => capacity / 2;
        private static int TableCapacity(int maximum)
        {
            int result = 1; int required = checked(maximum * 2);
            while (result < required) result = checked(result << 1);
            return result;
        }
        private static int HashChunk(ElementChunkKey chunk) => chunk.GetHashCode() & int.MaxValue;
        private static int Compare(ElementChunkKey left, ElementChunkKey right)
        {
            int z = left.Z.CompareTo(right.Z);
            if (z != 0) return z;
            int y = left.Y.CompareTo(right.Y);
            return y != 0 ? y : left.X.CompareTo(right.X);
        }
        private static int HashRecord(ElementChunkKey chunk, int local, MaterialId material)
        {
            unchecked { int hash = chunk.GetHashCode(); hash = hash * 397 ^ local; hash = hash * 397 ^ (byte)material; return hash & int.MaxValue; }
        }
    }
}

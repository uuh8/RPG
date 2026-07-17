using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 一个世界 Chunk 的常驻数据容器。内部复用 P6-A 的 ElementGrid，从而直接获得预分配的
    /// Current/Next Double Buffer、Solid Mask、Amount Delta 与脏版本基础设施。
    /// </summary>
    public sealed class ElementWorldChunk
    {
        private readonly ElementGrid _grid;
        private int _nonEmptyCellCount;

        internal ElementWorldChunk(ElementChunkKey key, int chunkSize, int cellCount)
        {
            Key = key;
            ChunkSize = chunkSize;
            _grid = new ElementGrid(
                new Vector3Int(chunkSize, chunkSize, chunkSize),
                cellCount,
                chunkSize);
        }

        public ElementChunkKey Key { get; }
        public int ChunkSize { get; }
        public int CellCount => _grid.CellCount;
        public bool HasAnyElement => _nonEmptyCellCount > 0;
        public ElementChunkActivityState ActivityState { get; internal set; }
        public long LastRelevantTick { get; private set; }

        // 这些 Buffer 只向本程序集和测试程序集开放，外部模块不能绕过 ElementField 的写入规则。
        internal ElementCell[] CurrentCells => _grid.CurrentCells;
        internal ElementCell[] NextCells => _grid.NextCells;
        internal bool[] SolidMask => _grid.SolidMask;
        internal int[] AmountDelta => _grid.AmountDelta;
        internal ElementGrid Grid => _grid;

        internal ElementCell GetCell(Vector3Int localCell)
        {
            return _grid.GetCell(localCell);
        }

        internal void SetCell(Vector3Int localCell, ElementCell cell)
        {
            ElementCell before = _grid.GetCell(localCell);
            bool wasEmpty = before.IsEmpty;
            bool isEmpty = cell.IsEmpty;

            if (wasEmpty && !isEmpty)
            {
                _nonEmptyCellCount++;
            }
            else if (!wasEmpty && isEmpty)
            {
                _nonEmptyCellCount--;
            }

            // 将“有种类但 Amount=0”一类中间无效值规范成真正的 Empty，避免休眠回收判断被脏数据干扰。
            _grid.SetCell(localCell, isEmpty ? default : cell);
        }

        internal void MarkRelevant(long worldTick)
        {
            if (worldTick < 0)
                throw new System.ArgumentOutOfRangeException(nameof(worldTick));

            // Tick 只允许单调前进；重复标记同一 Tick 合法，倒退则说明 Runtime 时间源发生错误。
            if (worldTick < LastRelevantTick)
                throw new System.ArgumentOutOfRangeException(nameof(worldTick));

            LastRelevantTick = worldTick;
        }
    }
}

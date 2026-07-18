using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 把初始化时的场景 Collider Rasterize 成紧凑 SolidMask。Bake 后 Simulation 只读 bool 数组，
    /// 不会在每个 Cell、每个 Tick 调用 Physics，从而把昂贵的空间查询移出热路径。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElementFieldSolidBaker : MonoBehaviour
    {
        [SerializeField] private LayerMask _solidLayerMask;

        public int Bake(ElementGrid grid, Vector3 origin, float cellSize)
        {
            if (grid == null)
                return 0;

            grid.ClearSolidMask();
            if (_solidLayerMask.value == 0)
            {
                GameLog.Warn(
                    "ElementFieldSolidBaker has an empty LayerMask; no Cell will be marked Solid.",
                    "ElementField");
                return 0;
            }

            int solidCount = 0;
            Vector3 halfExtents = Vector3.one * (cellSize * 0.49f);
            Vector3Int dimensions = grid.Dimensions;
            for (int z = 0; z < dimensions.z; z++)
            for (int y = 0; y < dimensions.y; y++)
            for (int x = 0; x < dimensions.x; x++)
            {
                var coordinate = new Vector3Int(x, y, z);
                Vector3 center = origin + new Vector3(
                    (x + 0.5f) * cellSize,
                    (y + 0.5f) * cellSize,
                    (z + 0.5f) * cellSize);
                bool isSolid = Physics.CheckBox(
                    center,
                    halfExtents,
                    Quaternion.identity,
                    _solidLayerMask,
                    QueryTriggerInteraction.Ignore);
                grid.SetSolid(coordinate, isSolid);
                if (isSolid)
                    solidCount++;
            }

            return solidCount;
        }

        /// <summary>
        /// 为稀疏世界中新创建的单个 Chunk 烘焙 SolidMask。与有限 Grid 的区别是：Local Cell
        /// 不能直接用于世界定位，必须先通过 global = chunkKey * chunkSize + local 还原 Global Cell。
        /// 该方法只在 Chunk 首次分配时调用，不进入每个 Simulation Tick。
        /// </summary>
        public int BakeWorldChunk(ElementWorldChunk chunk, Vector3 origin, float cellSize)
        {
            if (chunk == null)
                return 0;

            chunk.Grid.ClearSolidMask();
            if (_solidLayerMask.value == 0)
            {
                GameLog.Warn(
                    "ElementFieldSolidBaker has an empty LayerMask; no World Cell will be marked Solid.",
                    "ElementField");
                return 0;
            }

            int solidCount = 0;
            Vector3 halfExtents = Vector3.one * (cellSize * 0.49f);
            int chunkSize = chunk.ChunkSize;
            for (int z = 0; z < chunkSize; z++)
            for (int y = 0; y < chunkSize; y++)
            for (int x = 0; x < chunkSize; x++)
            {
                var localCell = new Vector3Int(x, y, z);
                Vector3Int globalCell = ElementWorldCoordinates.ComposeGlobalCell(
                    chunk.Key,
                    localCell,
                    chunkSize);
                Vector3 center = origin + new Vector3(
                    (globalCell.x + 0.5f) * cellSize,
                    (globalCell.y + 0.5f) * cellSize,
                    (globalCell.z + 0.5f) * cellSize);
                bool isSolid = Physics.CheckBox(
                    center,
                    halfExtents,
                    Quaternion.identity,
                    _solidLayerMask,
                    QueryTriggerInteraction.Ignore);
                int index = ElementFieldCoordinates.ToIndex(localCell, chunk.Grid.Dimensions);
                chunk.SolidMask[index] = isSolid;
                if (isSolid)
                    solidCount++;
            }

            return solidCount;
        }
    }
}

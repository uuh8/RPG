using System;
using System.Collections.Generic;
using Game.ElementField;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 把一个 Chunk 内的离散 Water Cell 转换为只包含可见表面的 Mesh 数据。
    ///
    /// Builder 不创建 Mesh、GameObject 或 Material，也不修改 ElementField；它只通过
    /// IElementFieldReadOnly 读取 Gameplay Snapshot，并把结果写入调用方长期复用的 List。
    /// 这种纯转换便于 NUnit 测试，也把“水面几何”与“Water Shader 着色”明确分离。
    /// </summary>
    public static class WaterChunkMeshBuilder
    {
        private const float HeightEpsilon = 0.000001f;

        /// <summary>
        /// 生成 Chunk Local Space 顶点、连续 UV 与 Triangle Index。
        /// 调用方应把承载 Mesh 的 Transform 放在该 Chunk 的 World Space 原点。
        /// </summary>
        public static void Build(
            IElementFieldReadOnly field,
            Vector3Int chunkCoordinate,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> indices)
        {
            if (field == null)
                throw new ArgumentNullException(nameof(field));
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));
            if (uvs == null)
                throw new ArgumentNullException(nameof(uvs));
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));

            // Clear 只把 Count 归零，不释放 List 内部数组；下一次 Dirty Chunk 重建会复用 Capacity。
            vertices.Clear();
            uvs.Clear();
            indices.Clear();

            if (!field.IsInitialized || field.CellSize <= 0f || field.ChunkSize <= 0)
                return;

            Vector3Int chunkCounts = field.ChunkCounts;
            if (chunkCoordinate.x < 0 || chunkCoordinate.x >= chunkCounts.x
                || chunkCoordinate.y < 0 || chunkCoordinate.y >= chunkCounts.y
                || chunkCoordinate.z < 0 || chunkCoordinate.z >= chunkCounts.z)
            {
                return;
            }

            int chunkSize = field.ChunkSize;
            Vector3Int dimensions = field.Dimensions;
            int startX = chunkCoordinate.x * chunkSize;
            int startY = chunkCoordinate.y * chunkSize;
            int startZ = chunkCoordinate.z * chunkSize;
            int endX = Mathf.Min(startX + chunkSize, dimensions.x);
            int endY = Mathf.Min(startY + chunkSize, dimensions.y);
            int endZ = Mathf.Min(startZ + chunkSize, dimensions.z);

            for (int z = startZ; z < endZ; z++)
            for (int y = startY; y < endY; y++)
            for (int x = startX; x < endX; x++)
            {
                if (!TryGetWaterAmount(field, x, y, z, out byte amount))
                    continue;

                AddCellSurface(
                    field,
                    x,
                    y,
                    z,
                    amount,
                    startX,
                    startY,
                    startZ,
                    vertices,
                    uvs,
                    indices);
            }
        }

        private static void AddCellSurface(
            IElementFieldReadOnly field,
            int x,
            int y,
            int z,
            byte amount,
            int chunkStartX,
            int chunkStartY,
            int chunkStartZ,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> indices)
        {
            float cellSize = field.CellSize;
            float x0 = (x - chunkStartX) * cellSize;
            float x1 = x0 + cellSize;
            float z0 = (z - chunkStartZ) * cellSize;
            float z1 = z0 + cellSize;
            float bottomY = (y - chunkStartY) * cellSize;

            // Cell Amount 是 [0,255] 离散存量；视觉高度使用线性映射。
            // surfaceY = cellBottomY + cellSize * amount / 255。
            float fillRatio = amount / (float)byte.MaxValue;
            float surfaceY = bottomY + cellSize * fillRatio;

            // 当前 Cell 上方没有 Water 时才生成顶面。顶点顺序使 Cross(B-A, C-A) 指向 +Y。
            if (!TryGetWaterAmount(field, x, y + 1, z, out _))
            {
                AddQuad(
                    new Vector3(x0, surfaceY, z0),
                    new Vector3(x0, surfaceY, z1),
                    new Vector3(x1, surfaceY, z1),
                    new Vector3(x1, surfaceY, z0),
                    new Vector2(x, z),
                    new Vector2(x, z + 1f),
                    new Vector2(x + 1f, z + 1f),
                    new Vector2(x + 1f, z),
                    vertices,
                    uvs,
                    indices);
            }

            // Side 只生成“当前水面高于邻居水面”的高度差。相同高度邻居之间没有内部面，
            // 高低水位相邻时也只补齐露出的水墙，不生成从 Cell 底部贯穿上来的重叠墙面。
            float negativeXNeighbor = GetNeighborSurfaceY(field, x - 1, y, z, bottomY, cellSize);
            if (surfaceY - negativeXNeighbor > HeightEpsilon)
            {
                AddQuad(
                    new Vector3(x0, negativeXNeighbor, z0),
                    new Vector3(x0, negativeXNeighbor, z1),
                    new Vector3(x0, surfaceY, z1),
                    new Vector3(x0, surfaceY, z0),
                    new Vector2(z, y + (negativeXNeighbor - bottomY) / cellSize),
                    new Vector2(z + 1f, y + (negativeXNeighbor - bottomY) / cellSize),
                    new Vector2(z + 1f, y + fillRatio),
                    new Vector2(z, y + fillRatio),
                    vertices,
                    uvs,
                    indices);
            }

            float positiveXNeighbor = GetNeighborSurfaceY(field, x + 1, y, z, bottomY, cellSize);
            if (surfaceY - positiveXNeighbor > HeightEpsilon)
            {
                AddQuad(
                    new Vector3(x1, positiveXNeighbor, z1),
                    new Vector3(x1, positiveXNeighbor, z0),
                    new Vector3(x1, surfaceY, z0),
                    new Vector3(x1, surfaceY, z1),
                    new Vector2(z + 1f, y + (positiveXNeighbor - bottomY) / cellSize),
                    new Vector2(z, y + (positiveXNeighbor - bottomY) / cellSize),
                    new Vector2(z, y + fillRatio),
                    new Vector2(z + 1f, y + fillRatio),
                    vertices,
                    uvs,
                    indices);
            }

            float negativeZNeighbor = GetNeighborSurfaceY(field, x, y, z - 1, bottomY, cellSize);
            if (surfaceY - negativeZNeighbor > HeightEpsilon)
            {
                AddQuad(
                    new Vector3(x1, negativeZNeighbor, z0),
                    new Vector3(x0, negativeZNeighbor, z0),
                    new Vector3(x0, surfaceY, z0),
                    new Vector3(x1, surfaceY, z0),
                    new Vector2(x + 1f, y + (negativeZNeighbor - bottomY) / cellSize),
                    new Vector2(x, y + (negativeZNeighbor - bottomY) / cellSize),
                    new Vector2(x, y + fillRatio),
                    new Vector2(x + 1f, y + fillRatio),
                    vertices,
                    uvs,
                    indices);
            }

            float positiveZNeighbor = GetNeighborSurfaceY(field, x, y, z + 1, bottomY, cellSize);
            if (surfaceY - positiveZNeighbor > HeightEpsilon)
            {
                AddQuad(
                    new Vector3(x0, positiveZNeighbor, z1),
                    new Vector3(x1, positiveZNeighbor, z1),
                    new Vector3(x1, surfaceY, z1),
                    new Vector3(x0, surfaceY, z1),
                    new Vector2(x, y + (positiveZNeighbor - bottomY) / cellSize),
                    new Vector2(x + 1f, y + (positiveZNeighbor - bottomY) / cellSize),
                    new Vector2(x + 1f, y + fillRatio),
                    new Vector2(x, y + fillRatio),
                    vertices,
                    uvs,
                    indices);
            }
        }

        private static float GetNeighborSurfaceY(
            IElementFieldReadOnly field,
            int x,
            int y,
            int z,
            float currentCellBottomY,
            float cellSize)
        {
            return TryGetWaterAmount(field, x, y, z, out byte amount)
                ? currentCellBottomY + cellSize * (amount / (float)byte.MaxValue)
                : currentCellBottomY;
        }

        private static bool TryGetWaterAmount(
            IElementFieldReadOnly field,
            int x,
            int y,
            int z,
            out byte amount)
        {
            Vector3Int dimensions = field.Dimensions;
            if (x < 0 || x >= dimensions.x
                || y < 0 || y >= dimensions.y
                || z < 0 || z >= dimensions.z
                || field.IsSolid(x, y, z))
            {
                amount = 0;
                return false;
            }

            ElementCell cell = field.GetCell(x, y, z);
            if (cell.IsEmpty || cell.MaterialKind != ElementMaterialKind.Water)
            {
                amount = 0;
                return false;
            }

            amount = cell.Amount;
            return true;
        }

        private static void AddQuad(
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 d,
            Vector2 uvA,
            Vector2 uvB,
            Vector2 uvC,
            Vector2 uvD,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> indices)
        {
            int first = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            uvs.Add(uvA);
            uvs.Add(uvB);
            uvs.Add(uvC);
            uvs.Add(uvD);

            // 两个 Triangle 共用同一个 Quad 的四个顶点；Winding 为 A→B→C、A→C→D。
            indices.Add(first);
            indices.Add(first + 1);
            indices.Add(first + 2);
            indices.Add(first);
            indices.Add(first + 2);
            indices.Add(first + 3);
        }
    }
}

using System;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>Cell Proxy 的 Pure 几何规则；Amount 只改变填充高度，不改变世界 Cell 占地。</summary>
    public static class DormantLiquidRenderPolicy
    {
        public static float CalculateHeight(byte amount, float cellSize, float minimumHeightRatio)
        {
            if (!(cellSize > 0f) || !float.IsFinite(cellSize))
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (minimumHeightRatio < 0f || minimumHeightRatio > 1f
                || !float.IsFinite(minimumHeightRatio))
                throw new ArgumentOutOfRangeException(nameof(minimumHeightRatio));
            if (amount == 0) return 0f;
            return cellSize * Mathf.Max(minimumHeightRatio, amount / 255f);
        }

        public static Matrix4x4 CreateCellMatrix(
            Vector3Int globalCell,
            Vector3 worldOrigin,
            float cellSize,
            byte amount,
            float minimumHeightRatio)
        {
            float height = CalculateHeight(amount, cellSize, minimumHeightRatio);
            Vector3 minimum = worldOrigin + (Vector3)globalCell * cellSize;
            Vector3 center = minimum + new Vector3(cellSize * .5f, height * .5f, cellSize * .5f);
            // 略微收窄 XZ，避免透明 Cell 共面产生明显 Z-fighting；相邻块仍保持视觉连贯。
            return Matrix4x4.TRS(center, Quaternion.identity,
                new Vector3(cellSize * .96f, height, cellSize * .96f));
        }
    }
}

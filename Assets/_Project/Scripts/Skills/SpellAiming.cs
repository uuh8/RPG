using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 法术发射方向辅助（纯函数，可单测）。SpellCaster 用它把"同一批 count 发投射物"散成扇形。
    /// </summary>
    public static class SpellAiming
    {
        /// <summary>
        /// 把同批 count 发投射物在 [-spread/2, +spread/2] 上均匀展开，返回第 index 发的偏航角（度）。
        /// count<=1 或 spread<=0 → 0。例：count=3, spread=30 → index 0/1/2 → -15/0/+15；count=2, spread=20 → -10/+10。
        /// </summary>
        public static float SpreadOffsetDegrees(int index, int count, float spreadDegrees)
        {
            if (count <= 1 || spreadDegrees <= 0f) return 0f;
            float t = (float)index / (count - 1); // 0..1
            return Mathf.Lerp(-spreadDegrees * 0.5f, spreadDegrees * 0.5f, t);
        }

        /// <summary>
        /// 把同一批 count 发投射物均匀放到 360° 圆环相位上。count<=1 时返回 0。
        /// </summary>
        public static float PhaseOffsetDegrees(int index, int count)
        {
            if (count <= 1) return 0f;
            return 360f * index / count;
        }

        /// <summary>
        /// 给同批轨道投射物分配不同旋转平面。index 0 保持 0，后续按 +base/-base/+2base/-2base 交替。
        /// </summary>
        public static float PlaneTiltDegrees(int index, int count, float baseTiltDegrees)
        {
            if (count <= 1 || Mathf.Approximately(baseTiltDegrees, 0f)) return 0f;
            if (index <= 0) return 0f;

            int ring = (index + 1) / 2;
            float sign = index % 2 == 1 ? 1f : -1f;
            return sign * ring * baseTiltDegrees;
        }
    }
}

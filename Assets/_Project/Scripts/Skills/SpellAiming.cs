using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 发射角度的 Pure Logic 辅助：只接收数字并返回角度，不读取 Camera/Transform。
    /// SpellCaster 得到角度后再用 Quaternion 旋转世界空间方向，因此数学规则可以独立做 EditMode Test。
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
            // 显式转 float 防止整数除法；t 将离散下标映射到闭区间 [0, 1]。
            float t = (float)index / (count - 1);
            // Lerp(a,b,t) 做线性插值，让第一发位于 -spread/2、最后一发位于 +spread/2，中间均匀分布。
            return Mathf.Lerp(-spreadDegrees * 0.5f, spreadDegrees * 0.5f, t);
        }

        /// <summary>
        /// 把同一批 count 发投射物均匀放到 360° 圆环相位上。count<=1 时返回 0。
        /// </summary>
        public static float PhaseOffsetDegrees(int index, int count)
        {
            if (count <= 1) return 0f;
            // 把完整 360° 等分为 count 份；浮点常量 360f 保证结果不是整数截断。
            return 360f * index / count;
        }

        /// <summary>
        /// 给同批轨道投射物分配不同旋转平面。index 0 保持 0，后续按 +base/-base/+2base/-2base 交替。
        /// </summary>
        public static float PlaneTiltDegrees(int index, int count, float baseTiltDegrees)
        {
            // Approximately 使用浮点容差判断“接近 0”，比直接 == 0 更能容忍 Inspector/计算产生的微小误差。
            if (count <= 1 || Mathf.Approximately(baseTiltDegrees, 0f)) return 0f;
            if (index <= 0) return 0f;

            // 这里故意使用整数除法：index 1/2 属于第 1 圈，3/4 属于第 2 圈，以此类推。
            int ring = (index + 1) / 2;
            // % 是取余运算；奇数 index 取正号，偶数 index 取负号，让平面在中心两侧交替展开。
            float sign = index % 2 == 1 ? 1f : -1f;
            return sign * ring * baseTiltDegrees;
        }
    }
}

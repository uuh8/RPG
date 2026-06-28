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
    }
}

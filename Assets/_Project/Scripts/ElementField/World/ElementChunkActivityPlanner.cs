using System;

namespace Game.ElementField
{
    /// <summary>
    /// Resident Chunk 相对当前 Interest Point 的调度状态。
    /// Reclaimable 只是“允许 Store 回收”的判定结果，不代表 Planner 自己会修改 Dictionary。
    /// </summary>
    public enum ElementChunkActivityState : byte
    {
        Sleeping = 0,
        Active = 1,
        Reclaimable = 2,
    }

    /// <summary>
    /// 不依赖 MonoBehaviour 的 Chunk 生命周期纯规则。玩家只决定数据是否工作，绝不改变世界坐标或 Chunk Key。
    /// </summary>
    public static class ElementChunkActivityPlanner
    {
        public static bool IsInsideActiveRegion(
            ElementChunkKey candidate,
            ElementChunkKey interest,
            int activeRadiusXZ,
            int activeRadiusY)
        {
            ValidateRadii(activeRadiusXZ, activeRadiusY);

            // 使用 long 做差，避免两个极端 int 坐标相减时先溢出。
            long deltaX = (long)candidate.X - interest.X;
            long deltaY = (long)candidate.Y - interest.Y;
            long deltaZ = (long)candidate.Z - interest.Z;

            // 第一版采用 Axis-Aligned Chunk Box，而不是球形距离：边界稳定、无需 sqrt，
            // 且能完整覆盖一个规则的 Streaming 窗口，后续跨 Chunk 邻居预取更容易推理。
            return Math.Abs(deltaX) <= activeRadiusXZ
                && Math.Abs(deltaY) <= activeRadiusY
                && Math.Abs(deltaZ) <= activeRadiusXZ;
        }

        public static ElementChunkActivityState Evaluate(
            ElementChunkKey candidate,
            ElementChunkKey interest,
            bool hasAnyElement,
            long lastRelevantTick,
            long currentTick,
            long sleepGraceTicks,
            int activeRadiusXZ,
            int activeRadiusY)
        {
            ValidateRadii(activeRadiusXZ, activeRadiusY);
            if (lastRelevantTick < 0)
                throw new ArgumentOutOfRangeException(nameof(lastRelevantTick));
            if (currentTick < lastRelevantTick)
                throw new ArgumentOutOfRangeException(nameof(currentTick));
            if (sleepGraceTicks < 0)
                throw new ArgumentOutOfRangeException(nameof(sleepGraceTicks));

            if (IsInsideActiveRegion(candidate, interest, activeRadiusXZ, activeRadiusY))
                return ElementChunkActivityState.Active;

            // Interest Point 不是唯一激活源。法术写入、跨 Chunk 传输等离散事件会刷新 LastRelevantTick，
            // 让远处 Chunk 在 Grace Lease 内继续完成流动/反应，而不是命中后一离开玩家半径就冻结。
            long inactiveTicks = currentTick - lastRelevantTick;
            if (inactiveTicks < sleepGraceTicks)
                return ElementChunkActivityState.Active;

            // 非空数据代表玩家曾改变过世界。距离只能让它暂停工作，不能让历史凭空消失。
            if (hasAnyElement)
                return ElementChunkActivityState.Sleeping;

            return ElementChunkActivityState.Reclaimable;
        }

        private static void ValidateRadii(int activeRadiusXZ, int activeRadiusY)
        {
            if (activeRadiusXZ < 0)
                throw new ArgumentOutOfRangeException(nameof(activeRadiusXZ));
            if (activeRadiusY < 0)
                throw new ArgumentOutOfRangeException(nameof(activeRadiusY));
        }
    }
}

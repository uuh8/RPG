using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// Gameplay 只读取 GPU Water 的低频 CPU 投影；它不是 Solver Truth，也不能反向修改粒子。
    /// </summary>
    public interface ILiquidOccupancyReadOnly
    {
        bool HasValidSnapshot { get; }
        Bounds SnapshotBounds { get; }
        uint SnapshotVersion { get; }
        uint AmountUnitsPerParticle { get; }

        bool TryGetAmount(
            Vector3Int globalCell,
            ElementMaterialKind materialKind,
            out byte amount);
    }
}

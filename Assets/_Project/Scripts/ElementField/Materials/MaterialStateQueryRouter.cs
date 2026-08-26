using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 查询与写入使用同一 Final Route，避免把残留 Cell Water 与 GPU Water Projection 相加。
    /// </summary>
    public sealed class MaterialStateQueryRouter : IMaterialAmountReadOnly
    {
        private readonly IMaterialSimulationRouteReadOnly _routes;
        private readonly IMaterialAmountReadOnly _elementCells;
        private readonly ILiquidOccupancyReadOnly _liquidOccupancy;

        public MaterialStateQueryRouter(
            IMaterialSimulationRouteReadOnly routes,
            IMaterialAmountReadOnly elementCells,
            ILiquidOccupancyReadOnly liquidOccupancy)
        {
            _routes = routes;
            _elementCells = elementCells;
            _liquidOccupancy = liquidOccupancy;
        }

        public bool TryGetAmount(Vector3Int globalCell, MaterialId material, out byte amount)
        {
            amount = 0;
            if (_routes == null || !_routes.TryResolve(material, out MaterialSimulationBackendKind backend))
                return false;

            switch (backend)
            {
                case MaterialSimulationBackendKind.ElementCell:
                    return _elementCells != null
                        && _elementCells.TryGetAmount(globalCell, material, out amount);
                case MaterialSimulationBackendKind.GpuPbfLiquid:
                    return _liquidOccupancy != null
                        && _liquidOccupancy.TryGetAmount(globalCell, material, out amount);
                default:
                    return false;
            }
        }
    }
}

namespace Game.ElementField
{
    /// <summary>
    /// ElementWorld 的纯分流规则。Single Writer 在这里意味着一次请求只选一个下游；
    /// PBF Water 失败时绝不回写 Cell，否则同一份水量会形成两套 Simulation Truth。
    /// </summary>
    public static class ElementWorldWriteRouter
    {
        public static bool TryRoute(
            WaterSimulationMode waterMode,
            in ElementWriteRequest request,
            IElementWriteSink cellSink,
            IFluidDepositSink fluidSink)
        {
            if (request.MaterialKind == ElementMaterialKind.Water
                && waterMode == WaterSimulationMode.GpuPbf)
            {
                return fluidSink != null
                    && fluidSink.IsFluidInitialized
                    && fluidSink.TryEnqueueDeposit(in request);
            }

            return cellSink != null && cellSink.TryEnqueueWrite(in request);
        }
    }
}

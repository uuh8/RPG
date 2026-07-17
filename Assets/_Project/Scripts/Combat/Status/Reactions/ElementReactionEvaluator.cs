namespace Game.Combat
{
    /// <summary>
    /// 元素反应的纯公式入口：只读取值快照和调参，返回判断/数值结果，不执行跨帧反应。
    /// 这里禁止访问 Time、Physics、EventBus 和任何 MonoBehaviour，保证同一输入永远得到同一输出。
    /// </summary>
    public static class ElementReactionEvaluator
    {
        // P2 统一把状态强度解释为百分比 [0,100]。集中定义上限，避免公式散落魔法数字。
        private const float MaximumIntensity = 100f;

        /// <summary>
        /// 判断 Fire 与 Water 是否同时达到正式灭火阈值。
        /// 使用 >= 而不是 >，确保 Inspector 填 10 时，恰好 10 也属于正式反应。
        /// </summary>
        /// <param name="snapshot">本次求值开始时的四元素只读快照。</param>
        /// <param name="tuning">灭火阈值与速率参数；此方法只读取 FormalThreshold。</param>
        /// <returns>两种通道都达到阈值时返回 true。</returns>
        public static bool IsFormalExtinguish(
            in ElementStateSnapshot snapshot,
            in ExtinguishTuning tuning)
        {
            // 即使运行时代码或旧 Asset 绕过了 [Min] 写入负数，也按 0 处理。
            float threshold = MaxZero(tuning.FormalThreshold);
            return snapshot.Fire >= threshold && snapshot.Water >= threshold;
        }

        /// <summary>
        /// 计算一个模拟步内 Fire 与 Water 应当等量消耗多少，但不修改任何运行时状态。
        /// 角色反应和空间 Cell 反应共享这条守恒公式，各自保留自己的生命周期与数据容器。
        /// </summary>
        /// <param name="fire">当前可参与反应的 Fire 存量。</param>
        /// <param name="water">当前可参与反应的 Water 存量。</param>
        /// <param name="deltaTime">模拟步长，单位为秒；非正数表示本步不推进。</param>
        /// <param name="useFormalRate">true 使用正式高速率，false 使用低速接触速率。</param>
        /// <param name="tuning">灭火的低速与正式速率调参。</param>
        /// <returns>本步两边应各自扣除的非负数值。</returns>
        public static float CalculateExtinguishConsumption(
            float fire,
            float water,
            float deltaTime,
            bool useFormalRate,
            in ExtinguishTuning tuning)
        {
            // Early Return 清楚表达三种“不能反应”的情况，也防止负输入进入后续乘法。
            if (fire <= 0f || water <= 0f || deltaTime <= 0f)
                return 0f;

            float rate = useFormalRate
                ? MaxZero(tuning.FormalRatePerSecond)
                : MaxZero(tuning.LowRatePerSecond);

            // Consumed = min(Fire, Water, Rate * deltaTime)。
            // Fire/Water 两个存量上限保证不会扣成负数；时间预算让结果随时间连续推进，
            // 同一个 consumed 交给双方使用，则保证 Fire 与 Water 始终等量中和。
            return Min(Min(MaxZero(fire), MaxZero(water)), rate * MaxZero(deltaTime));
        }

        /// <summary>
        /// 计算这一帧 Wet 能移除多少 Poison/Goo，但不直接修改角色状态。
        /// 预算先服务 Poison，剩余部分才服务 Goo，因而严格实现“先洗毒、再洗粘液”。
        /// </summary>
        /// <param name="snapshot">包含 Water、Poison、Goo 当前强度的值快照。</param>
        /// <param name="deltaTime">本次模拟步长，单位为秒；非正数表示不推进模拟。</param>
        /// <param name="poisonMultiplier">Wet 对 Poison 的效率倍率，来自 Poison StatusDefinition。</param>
        /// <param name="gooMultiplier">Wet 对 Goo 的效率倍率，来自 Sticky StatusDefinition。</param>
        /// <param name="tuning">满 Wet 时每秒拥有的基础清洗预算。</param>
        /// <returns>只读的移除量结果；真正写回由 Runtime/StatusController 完成。</returns>
        public static WetCleanseResult CalculateWetCleanse(
            in ElementStateSnapshot snapshot,
            float deltaTime,
            float poisonMultiplier,
            float gooMultiplier,
            in WetCleanseTuning tuning)
        {
            // Early Return 除了表达“无时间/无水就没有清洗”，也避免后续无意义除法和运算。
            if (deltaTime <= 0f || snapshot.Water <= 0f)
                return default;

            // Wet=40 意味着获得满 Wet 预算的 40%。Clamp 防止异常强度放大预算。
            // 公式：budget = rate(full wet) × clamp(Wet,0,100)/100 × deltaTime。
            float normalizedWet = Clamp(snapshot.Water, 0f, MaximumIntensity) / MaximumIntensity;
            float budget = MaxZero(tuning.BudgetPerSecondAtFullWet) * normalizedWet * deltaTime;
            if (budget <= 0f)
                return default;

            float safePoisonMultiplier = MaxZero(poisonMultiplier);
            float poisonRemoved = 0f;
            if (safePoisonMultiplier > 0f && snapshot.Poison > 0f)
            {
                // 状态移除量 = 预算 × 效率，但不能超过现有 Poison。
                poisonRemoved = Min(snapshot.Poison, budget * safePoisonMultiplier);

                // 必须把“实际移除量”除以效率，换算回它消耗了多少统一预算。
                // 若 Poison 只剩 3，不能仍然扣除原计划的全部预算，否则 Goo 得不到正确剩余量。
                budget -= poisonRemoved / safePoisonMultiplier;
            }

            float safeGooMultiplier = MaxZero(gooMultiplier);
            float gooRemoved = 0f;
            if (budget > 0f && safeGooMultiplier > 0f && snapshot.Goo > 0f)
                gooRemoved = Min(snapshot.Goo, budget * safeGooMultiplier);

            return new WetCleanseResult(poisonRemoved, gooRemoved);
        }

        /// <summary>
        /// Toxic Combustion 是“同时满足”条件：Fire 和 Poison 任一不足都不能启动。
        /// Wind-up、消费和伤害属于跨帧 Runtime，不应塞进这个瞬时布尔公式。
        /// </summary>
        public static bool CanStartToxicCombustion(
            in ElementStateSnapshot snapshot,
            in ToxicCombustionTuning tuning)
        {
            return snapshot.Fire >= MaxZero(tuning.FireThreshold)
                && snapshot.Poison >= MaxZero(tuning.PoisonThreshold);
        }

        /// <summary>
        /// Ignite Goo 除了要求 Fire/Goo 达阈值，还必须没有被 Wet 抑制，
        /// 并且 Fire 距离 100 至少留有 RequiredFireCapacity，避免消费 Goo 却无法增加 Fire。
        /// </summary>
        public static bool CanStartIgniteGoo(
            in ElementStateSnapshot snapshot,
            in IgniteGooTuning tuning)
        {
            // remainingFireCapacity = 100 - 当前 Fire。先 Clamp，避免异常输入产生负容量或超过 100。
            float remainingFireCapacity = MaximumIntensity - Clamp(snapshot.Fire, 0f, MaximumIntensity);
            return snapshot.Fire >= MaxZero(tuning.FireThreshold)
                && snapshot.Goo >= MaxZero(tuning.GooThreshold)
                && snapshot.Water < MaxZero(tuning.WetInhibitThreshold)
                && remainingFireCapacity >= MaxZero(tuning.RequiredFireCapacity);
        }

        private static float MaxZero(float value)
        {
            // 不使用 Mathf，是为了让这个纯内核尽量只依赖普通 C# 数值运算，便于独立测试/迁移。
            return value > 0f ? value : 0f;
        }

        private static float Min(float a, float b)
        {
            return a < b ? a : b;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }
}

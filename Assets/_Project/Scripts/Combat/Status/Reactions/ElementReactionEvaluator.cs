namespace Game.Combat
{
    /// <summary>
    /// 元素反应的纯公式入口：只读取值快照和调参，返回判断/数值结果。
    /// 这里禁止访问 Time、Physics、EventBus 和任何 MonoBehaviour，保证同一输入永远得到同一输出。
    /// </summary>
    public static class ElementReactionEvaluator
    {
        private const float MaximumIntensity = 100f;

        public static bool IsFormalExtinguish(
            in ElementStateSnapshot snapshot,
            in ExtinguishTuning tuning)
        {
            float threshold = MaxZero(tuning.FormalThreshold);
            return snapshot.Fire >= threshold && snapshot.Water >= threshold;
        }

        public static WetCleanseResult CalculateWetCleanse(
            in ElementStateSnapshot snapshot,
            float deltaTime,
            float poisonMultiplier,
            float gooMultiplier,
            in WetCleanseTuning tuning)
        {
            if (deltaTime <= 0f || snapshot.Water <= 0f)
                return default;

            float normalizedWet = Clamp(snapshot.Water, 0f, MaximumIntensity) / MaximumIntensity;
            float budget = MaxZero(tuning.BudgetPerSecondAtFullWet) * normalizedWet * deltaTime;
            if (budget <= 0f)
                return default;

            float safePoisonMultiplier = MaxZero(poisonMultiplier);
            float poisonRemoved = 0f;
            if (safePoisonMultiplier > 0f && snapshot.Poison > 0f)
            {
                poisonRemoved = Min(snapshot.Poison, budget * safePoisonMultiplier);
                budget -= poisonRemoved / safePoisonMultiplier;
            }

            float safeGooMultiplier = MaxZero(gooMultiplier);
            float gooRemoved = 0f;
            if (budget > 0f && safeGooMultiplier > 0f && snapshot.Goo > 0f)
                gooRemoved = Min(snapshot.Goo, budget * safeGooMultiplier);

            return new WetCleanseResult(poisonRemoved, gooRemoved);
        }

        public static bool CanStartToxicCombustion(
            in ElementStateSnapshot snapshot,
            in ToxicCombustionTuning tuning)
        {
            return snapshot.Fire >= MaxZero(tuning.FireThreshold)
                && snapshot.Poison >= MaxZero(tuning.PoisonThreshold);
        }

        public static bool CanStartIgniteGoo(
            in ElementStateSnapshot snapshot,
            in IgniteGooTuning tuning)
        {
            float remainingFireCapacity = MaximumIntensity - Clamp(snapshot.Fire, 0f, MaximumIntensity);
            return snapshot.Fire >= MaxZero(tuning.FireThreshold)
                && snapshot.Goo >= MaxZero(tuning.GooThreshold)
                && snapshot.Water < MaxZero(tuning.WetInhibitThreshold)
                && remainingFireCapacity >= MaxZero(tuning.RequiredFireCapacity);
        }

        private static float MaxZero(float value)
        {
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

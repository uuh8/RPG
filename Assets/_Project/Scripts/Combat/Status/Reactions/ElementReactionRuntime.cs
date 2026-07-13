namespace Game.Combat
{
    /// <summary>
    /// 预实例化、可复用的连续反应状态机。它只处理数值和阶段，不访问 Unity 生命周期、Physics 或 EventBus。
    /// 三种 Process 使用明确字段而非动态集合，使优先级固定且每帧零托管分配。
    /// </summary>
    public sealed class ElementReactionRuntime
    {
        private const float Epsilon = 0.0001f;
        private const float MaximumIntensity = 100f;

        private readonly ElementReactionTuningSnapshot _tuning;

        private bool _dirty;
        private StatusSource _triggerSource;

        private bool _extinguishActive;
        private bool _extinguishFormalSignalled;

        private bool _toxicActive;
        private float _toxicElapsed;
        private float _toxicPlannedConsume;
        private float _toxicConsumed;
        private float _toxicCooldown;
        private StatusSource _toxicSource;

        private bool _igniteActive;
        private bool _ignitePaused;
        private float _igniteCooldown;
        private StatusSource _igniteSource;

        public bool IsExtinguishActive => _extinguishActive;
        public bool IsToxicActive => _toxicActive;
        public bool IsIgniteActive => _igniteActive;

        public ElementReactionRuntime(in ElementReactionTuningSnapshot tuning)
        {
            _tuning = tuning;
        }

        public void MarkDirty(StatusSource triggerSource)
        {
            _dirty = true;
            _triggerSource = triggerSource;
        }

        public ElementReactionFrame Tick(in ElementStateSnapshot snapshot, float deltaTime)
        {
            // 零时间步仍允许 Dirty 输入建立 Process 和发布 Started，但禁止任何连续积分。
            // Adapter 因而能在 ApplyStatus 当帧建立反应，下一次正常 Tick 再推进数值。
            deltaTime = MaxZero(deltaTime);

            _toxicCooldown = MaxZero(_toxicCooldown - deltaTime);
            _igniteCooldown = MaxZero(_igniteCooldown - deltaTime);

            var frame = new ElementReactionFrame();
            float fire = ClampIntensity(snapshot.Fire);
            float water = ClampIntensity(snapshot.Water);
            float poison = ClampIntensity(snapshot.Poison);
            float goo = ClampIntensity(snapshot.Goo);

            bool startedExtinguish = false;
            bool startedToxic = false;
            bool startedIgnite = false;

            if (_dirty)
            {
                _dirty = false;
                startedExtinguish = TryStartExtinguish(in snapshot, ref frame);
                startedToxic = TryStartToxic(in snapshot, ref frame);
                startedIgnite = TryStartIgnite(in snapshot, ref frame);
            }

            if (deltaTime <= 0f)
                return frame;

            if (_extinguishActive && !startedExtinguish)
                TickExtinguish(ref fire, ref water, deltaTime, ref frame);

            WetCleanseResult cleanse = ElementReactionEvaluator.CalculateWetCleanse(
                new ElementStateSnapshot(
                    fire, water, poison, goo,
                    snapshot.FireSource, snapshot.WaterSource, snapshot.PoisonSource, snapshot.GooSource),
                deltaTime,
                _tuning.PoisonWetCleanseMultiplier,
                _tuning.GooWetCleanseMultiplier,
                in _tuning.WetCleanse);
            poison -= cleanse.PoisonRemoved;
            goo -= cleanse.GooRemoved;
            frame.PoisonDelta -= cleanse.PoisonRemoved;
            frame.GooDelta -= cleanse.GooRemoved;

            if (_toxicActive && !startedToxic)
                TickToxic(ref poison, deltaTime, ref frame);

            if (_igniteActive && !startedIgnite)
                TickIgnite(ref fire, water, ref goo, deltaTime, ref frame);

            return frame;
        }

        public ElementReactionFrame CancelAll()
        {
            var frame = new ElementReactionFrame();
            if (_extinguishActive && _extinguishFormalSignalled)
                AddSignal(ref frame, ElementReactionId.Extinguish, ElementReactionPhase.Cancelled, 0f, 0f);
            if (_toxicActive)
                AddSignal(ref frame, ElementReactionId.ToxicCombustion, ElementReactionPhase.Cancelled, 0f, 0f);
            if (_igniteActive)
                AddSignal(ref frame, ElementReactionId.IgniteGoo, ElementReactionPhase.Cancelled, 0f, 0f);

            _dirty = false;
            _extinguishActive = false;
            _extinguishFormalSignalled = false;
            _toxicActive = false;
            _toxicElapsed = 0f;
            _toxicPlannedConsume = 0f;
            _toxicConsumed = 0f;
            _igniteActive = false;
            _ignitePaused = false;
            return frame;
        }

        private bool TryStartExtinguish(in ElementStateSnapshot snapshot, ref ElementReactionFrame frame)
        {
            if (snapshot.Fire <= 0f || snapshot.Water <= 0f)
                return false;

            if (!_extinguishActive)
                _extinguishActive = true;

            bool formal = ElementReactionEvaluator.IsFormalExtinguish(in snapshot, in _tuning.Extinguish);
            if (!formal || _extinguishFormalSignalled)
                return false;

            _extinguishFormalSignalled = true;
            AddSignal(
                ref frame,
                ElementReactionId.Extinguish,
                ElementReactionPhase.Started,
                Min(snapshot.Fire, snapshot.Water) / MaximumIntensity,
                0f);
            return true;
        }

        private bool TryStartToxic(in ElementStateSnapshot snapshot, ref ElementReactionFrame frame)
        {
            if (_toxicActive || _toxicCooldown > 0f
                || !ElementReactionEvaluator.CanStartToxicCombustion(in snapshot, in _tuning.ToxicCombustion))
                return false;

            _toxicActive = true;
            _toxicElapsed = 0f;
            _toxicConsumed = 0f;
            _toxicPlannedConsume = Min(
                ClampIntensity(snapshot.Poison),
                MaxZero(_tuning.ToxicCombustion.MaxPoisonConsume));
            _toxicSource = _triggerSource;
            AddSignal(
                ref frame,
                ElementReactionId.ToxicCombustion,
                ElementReactionPhase.Started,
                _toxicPlannedConsume / MaxPositive(_tuning.ToxicCombustion.MaxPoisonConsume),
                MaxPositive(_tuning.ToxicCombustion.WindUpSeconds));
            return true;
        }

        private bool TryStartIgnite(in ElementStateSnapshot snapshot, ref ElementReactionFrame frame)
        {
            if (_igniteActive || _igniteCooldown > 0f
                || !ElementReactionEvaluator.CanStartIgniteGoo(in snapshot, in _tuning.IgniteGoo))
                return false;

            _igniteActive = true;
            _ignitePaused = false;
            _igniteSource = snapshot.FireSource;
            AddSignal(
                ref frame,
                ElementReactionId.IgniteGoo,
                ElementReactionPhase.Started,
                snapshot.Goo / MaximumIntensity,
                0f);
            return true;
        }

        private void TickExtinguish(
            ref float fire,
            ref float water,
            float deltaTime,
            ref ElementReactionFrame frame)
        {
            if (fire <= Epsilon || water <= Epsilon)
            {
                FinishExtinguish(ref frame);
                return;
            }

            // 一旦达到正式阈值并发布 Started，就锁定高速中和直到一方归零。
            // 否则 80 Fire + 30 Water 会在 Water 跌破 10 后突然降速，无法守恒地得到 50/0。
            float rate = _extinguishFormalSignalled
                ? MaxZero(_tuning.Extinguish.FormalRatePerSecond)
                : MaxZero(_tuning.Extinguish.LowRatePerSecond);
            float consumed = Min(Min(fire, water), rate * deltaTime);

            fire -= consumed;
            water -= consumed;
            frame.FireDelta -= consumed;
            frame.WaterDelta -= consumed;
            if (consumed > 0f)
                frame.SuppressNaturalDecay |= StatusMask.Fire | StatusMask.Water;

            if (fire <= Epsilon || water <= Epsilon)
                FinishExtinguish(ref frame);
        }

        private void FinishExtinguish(ref ElementReactionFrame frame)
        {
            if (_extinguishFormalSignalled)
                AddSignal(ref frame, ElementReactionId.Extinguish, ElementReactionPhase.Resolved, 0f, 0f);
            _extinguishActive = false;
            _extinguishFormalSignalled = false;
            _dirty = true;
        }

        private void TickToxic(ref float poison, float deltaTime, ref ElementReactionFrame frame)
        {
            float duration = MaxPositive(_tuning.ToxicCombustion.WindUpSeconds);
            float remainingPlanned = MaxZero(_toxicPlannedConsume - _toxicConsumed);
            float rate = _toxicPlannedConsume / duration;
            float consumed = Min(Min(poison, remainingPlanned), rate * deltaTime);

            poison -= consumed;
            _toxicConsumed += consumed;
            _toxicElapsed += deltaTime;
            frame.PoisonDelta -= consumed;
            if (consumed > 0f)
                frame.SuppressNaturalDecay |= StatusMask.Poison;

            if (_toxicElapsed + Epsilon < duration)
                return;

            bool effective = _toxicConsumed >= MaxZero(_tuning.ToxicCombustion.MinEffectiveConsume);
            if (effective)
            {
                frame.HasAreaDamage = true;
                frame.AreaDamage = new ReactionDamageCommand(
                    _toxicSource,
                    MaxZero(_tuning.ToxicCombustion.BaseDamage)
                        + _toxicConsumed * MaxZero(_tuning.ToxicCombustion.DamagePerPoison),
                    MaxZero(_tuning.ToxicCombustion.Radius));
                AddSignal(ref frame, ElementReactionId.ToxicCombustion, ElementReactionPhase.Resolved,
                    _toxicConsumed / MaxPositive(_tuning.ToxicCombustion.MaxPoisonConsume), 0f);
            }
            else
            {
                AddSignal(ref frame, ElementReactionId.ToxicCombustion, ElementReactionPhase.Cancelled, 0f, 0f);
            }

            _toxicActive = false;
            _toxicCooldown = MaxZero(_tuning.ToxicCombustion.CooldownSeconds);
            _dirty = true;
        }

        private void TickIgnite(
            ref float fire,
            float water,
            ref float goo,
            float deltaTime,
            ref ElementReactionFrame frame)
        {
            float inhibitThreshold = MaxZero(_tuning.IgniteGoo.WetInhibitThreshold);
            if (water >= inhibitThreshold)
            {
                if (!_ignitePaused)
                {
                    _ignitePaused = true;
                    AddSignal(ref frame, ElementReactionId.IgniteGoo, ElementReactionPhase.Paused, 0f, 0f);
                }
                return;
            }

            if (_ignitePaused)
            {
                _ignitePaused = false;
                AddSignal(ref frame, ElementReactionId.IgniteGoo, ElementReactionPhase.Resumed,
                    goo / MaximumIntensity, 0f);
                return;
            }

            float ratio = MaxZero(_tuning.IgniteGoo.FirePerGoo);
            if (ratio <= 0f)
            {
                FinishIgnite(ref frame);
                return;
            }

            float fireCapacityAsGoo = MaxZero(MaximumIntensity - fire) / ratio;
            float consumed = Min(
                Min(goo, fireCapacityAsGoo),
                MaxZero(_tuning.IgniteGoo.GooConsumePerSecond) * deltaTime);
            float fireAdded = consumed * ratio;

            goo -= consumed;
            fire += fireAdded;
            frame.GooDelta -= consumed;
            frame.FireDelta += fireAdded;
            if (consumed > 0f)
                frame.SuppressNaturalDecay |= StatusMask.Fire | StatusMask.Goo;

            if (goo <= Epsilon || MaximumIntensity - fire <= Epsilon)
                FinishIgnite(ref frame);
        }

        private void FinishIgnite(ref ElementReactionFrame frame)
        {
            AddSignal(ref frame, ElementReactionId.IgniteGoo, ElementReactionPhase.Resolved, 0f, 0f);
            _igniteActive = false;
            _ignitePaused = false;
            _igniteCooldown = MaxZero(_tuning.IgniteGoo.CooldownSeconds);
            _dirty = true;
        }

        private static void AddSignal(
            ref ElementReactionFrame frame,
            ElementReactionId reaction,
            ElementReactionPhase phase,
            float strength,
            float expectedDuration)
        {
            var signal = new ReactionSignal(
                reaction,
                phase,
                Clamp01(strength),
                MaxZero(expectedDuration));
            frame.AddSignal(in signal);
        }

        private static float ClampIntensity(float value)
        {
            if (value < 0f)
                return 0f;
            return value > MaximumIntensity ? MaximumIntensity : value;
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
                return 0f;
            return value > 1f ? 1f : value;
        }

        private static float MaxZero(float value)
        {
            return value > 0f ? value : 0f;
        }

        private static float MaxPositive(float value)
        {
            return value > Epsilon ? value : Epsilon;
        }

        private static float Min(float a, float b)
        {
            return a < b ? a : b;
        }
    }
}

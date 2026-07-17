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

        // dirty flag 表示“状态刚收到新输入，需要重新检查启动门槛”。
        // 没有新输入时不重复触发 Started，避免一组持续存在的状态每帧重启反应。
        private bool _dirty;
        private StatusSource _triggerSource;

        // Extinguish 没有计时 Wind-up，但需要记住是否已经跨过正式反应门槛并发过 Started。
        private bool _extinguishActive;
        private bool _extinguishFormalSignalled;

        // Toxic Combustion 是“预热期间逐步消耗 Poison，结束时再结算伤害”的有时长 Process。
        // planned 与 consumed 分开保存，因为 Wet Cleanse 可能抢先洗掉 Poison，实际消耗会低于计划值。
        private bool _toxicActive;
        private float _toxicElapsed;
        private float _toxicPlannedConsume;
        private float _toxicConsumed;
        private float _toxicCooldown;
        private StatusSource _toxicSource;

        // Ignite Goo 可被 Wet 暂停，因此 Active 与 Paused 必须是两个正交状态。
        private bool _igniteActive;
        private bool _ignitePaused;
        private float _igniteCooldown;
        private StatusSource _igniteSource;

        public bool IsExtinguishActive => _extinguishActive;
        public bool IsToxicActive => _toxicActive;
        public bool IsIgniteActive => _igniteActive;

        public ElementReactionRuntime(in ElementReactionTuningSnapshot tuning)
        {
            // 构造时复制纯值快照，运行期间不再读取可变 ScriptableObject，保证一次模拟规则稳定。
            _tuning = tuning;
        }

        /// <summary>
        /// 通知 Runtime 有新的元素输入，并记录这次输入的来源。
        /// 真正的门槛检查延迟到 Tick，以便同一帧多个 ApplyStatus 先汇总成完整 Snapshot。
        /// </summary>
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

            // fire/water/poison/goo 是本模拟步的“工作副本”。前一个高优先级规则修改它后，
            // 后一个规则会看到更新后的值；最终只把差值写进 frame，不直接改调用者状态。
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

            // 固定优先级：Extinguish → Wet Cleanse → Toxic Combustion → Ignite Goo。
            // 因此 Wet 会先削减 Poison/Goo，后续反应只能消费清洗后的剩余量。
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
            // 生命周期结束（例如组件 Disable/死亡）时必须清空尚未完成的 Process，
            // 否则 Toxic 的 Wind-up 可能在对象重新启用后错误地补发一次范围伤害。
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

            // Fire 与 Water 同时大于 0 即可低速中和；只有双方都达到 FormalThreshold
            // 才把它视为需要视觉反馈的正式 Extinguish，并发送 Started。
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

            // 爆炸伤害归因于最后触发本轮门槛检查的输入来源，而不是每帧重新查询施加者。
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

            // 黏液被已有火焰点燃，因此归因使用 FireSource；它与 Toxic 的 triggerSource 语义不同。
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

            // 一旦达到正式阈值并发布 Started，就把 true 传给共享公式，锁定高速中和直到一方归零。
            // 角色 Runtime 负责这个跨帧 Latch；纯公式本身不记忆状态，因此也能被 P6 Cell 复用。
            float consumed = ElementReactionEvaluator.CalculateExtinguishConsumption(
                fire,
                water,
                deltaTime,
                _extinguishFormalSignalled,
                in _tuning.Extinguish);

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

            // 匀速消耗公式：rate = plannedConsume / windUpSeconds，
            // 本步消耗 = min(当前 Poison, 剩余计划量, rate * deltaTime)。
            // deltaTime 积分让 30 FPS 与 120 FPS 在相同总时间后得到相同结果。
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
                    // Damage = BaseDamage + ActualConsumedPoison * DamagePerPoison。
                    // 使用实际消耗量，使 Wet Cleanse 确实能够降低爆炸威力。
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
                // Resumed 帧只发布离散通知而不立即转换，表现层可先恢复 VFX，
                // 同时避免同一 Tick 既解除暂停又跳过一段可观察的生命周期。
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

            // 先把剩余 Fire 容量换算成最多可消耗的 Goo：
            // gooCapacity = (100 - fire) / FirePerGoo。
            // 再取 Goo 存量、容量和本帧速率预算三者最小值，防止 Fire 超过 100。
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
            // 在数值核心边界统一归一化，表现层无需为非法强度或负时长重复防御。
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
            // 参与除法的参数至少为 Epsilon，避免错误配置 0 秒导致 NaN/Infinity 扩散。
            return value > Epsilon ? value : Epsilon;
        }

        private static float Min(float a, float b)
        {
            return a < b ? a : b;
        }
    }
}

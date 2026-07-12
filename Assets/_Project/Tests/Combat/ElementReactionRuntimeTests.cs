using NUnit.Framework;

namespace Game.Combat.Tests
{
    public sealed class ElementReactionRuntimeTests
    {
        private static readonly StatusSource TriggerSource = new StatusSource(99, 2);
        private static readonly StatusSource FireSource = new StatusSource(10, 1);

        [TestCase(1f / 30f)]
        [TestCase(1f / 120f)]
        public void Extinguish_ConsumesEightyThirtyToFiftyZero(float step)
        {
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 80f, water: 30f);
            runtime.MarkDirty(TriggerSource);

            state = Step(runtime, state, step); // 启动帧只发 Started，下一帧才推进。
            float elapsed = 0f;
            while (elapsed < 0.6f - 1e-5f)
            {
                float delta = Min(step, 0.6f - elapsed);
                state = Step(runtime, state, delta);
                elapsed += delta;
            }

            Assert.AreEqual(50f, state.Fire, 1e-3f);
            Assert.AreEqual(0f, state.Water, 1e-3f);
        }

        [Test]
        public void Extinguish_BelowFormalThresholdUsesLowRate()
        {
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 5f, water: 5f);
            runtime.MarkDirty(TriggerSource);

            state = Step(runtime, state, 0.01f);
            state = Step(runtime, state, 0.5f);

            Assert.AreEqual(0f, state.Fire, 1e-4f);
            Assert.AreEqual(0f, state.Water, 1e-4f);
        }

        [Test]
        public void ToxicCombustion_ResolvesOnlyAfterWindUp()
        {
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 30f, poison: 40f);
            runtime.MarkDirty(TriggerSource);

            ElementReactionFrame started = runtime.Tick(in state, 0.01f);
            state = Apply(state, started);
            Assert.AreEqual(ElementReactionPhase.Started, started.Signal0.Phase);
            Assert.IsFalse(started.HasAreaDamage);

            ElementReactionFrame half = runtime.Tick(in state, 0.1f);
            state = Apply(state, half);
            Assert.IsFalse(half.HasAreaDamage);
            Assert.AreEqual(20f, state.Poison, 1e-4f);

            ElementReactionFrame resolved = runtime.Tick(in state, 0.1f);
            state = Apply(state, resolved);
            Assert.IsTrue(resolved.HasAreaDamage);
            Assert.AreEqual(ElementReactionPhase.Resolved, resolved.Signal0.Phase);
            Assert.AreEqual(24f, resolved.AreaDamage.Amount, 1e-4f);
            Assert.AreEqual(2.5f, resolved.AreaDamage.Radius, 1e-4f);
            Assert.AreEqual(TriggerSource.Id, resolved.AreaDamage.Source.Id);
            Assert.AreEqual(0f, state.Poison, 1e-4f);
        }

        [Test]
        public void WetCleanse_ReducesToxicActualConsumptionAndDamage()
        {
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 30f, water: 100f, poison: 20f);
            runtime.MarkDirty(TriggerSource);

            state = Step(runtime, state, 0.01f);
            ElementReactionFrame resolved = runtime.Tick(in state, 0.2f);
            state = Apply(state, resolved);

            Assert.IsTrue(resolved.HasAreaDamage);
            Assert.Less(resolved.AreaDamage.Amount, 16f);
            Assert.GreaterOrEqual(state.Poison, 0f);
        }

        [Test]
        public void IgniteGoo_ConvertsFortyGooIntoFiftyFirePerSecond()
        {
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 20f, goo: 40f);
            runtime.MarkDirty(TriggerSource);

            state = Step(runtime, state, 0.01f);
            ElementReactionFrame frame = runtime.Tick(in state, 1f);
            state = Apply(state, frame);

            Assert.AreEqual(70f, state.Fire, 1e-4f);
            Assert.AreEqual(0f, state.Goo, 1e-4f);
            Assert.IsTrue((frame.SuppressNaturalDecay & StatusMask.Fire) != 0);
            Assert.IsTrue((frame.SuppressNaturalDecay & StatusMask.Goo) != 0);
        }

        [Test]
        public void IgniteGoo_PausesAtWetThresholdAndResumesBelowIt()
        {
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 20f, goo: 40f);
            runtime.MarkDirty(TriggerSource);
            state = Step(runtime, state, 0.01f);

            state = Snapshot(state.Fire, water: 10f, poison: state.Poison, goo: state.Goo);
            ElementReactionFrame paused = runtime.Tick(in state, 0.1f);
            Assert.AreEqual(ElementReactionPhase.Paused, paused.Signal0.Phase);
            Assert.AreEqual(0f, paused.FireDelta, 1e-4f,
                "暂停后 Ignite 不能继续把 Goo 转成 Fire。");
            Assert.Less(paused.GooDelta, 0f,
                "Wet Cleanse 优先于 Ignite；暂停点燃时仍应继续清洗 Goo。");

            state = Snapshot(state.Fire, water: 9.99f, poison: state.Poison, goo: state.Goo);
            ElementReactionFrame resumed = runtime.Tick(in state, 0.1f);
            Assert.AreEqual(ElementReactionPhase.Resumed, resumed.Signal0.Phase);
            Assert.AreEqual(0f, resumed.FireDelta, 1e-4f,
                "Resumed 是离散阶段通知，下一帧才恢复转换。");
            Assert.Less(resumed.GooDelta, 0f,
                "恢复通知帧仍会执行优先级更高的 Wet Cleanse。");

            ElementReactionFrame progress = runtime.Tick(in state, 0.1f);
            Assert.Less(progress.GooDelta, 0f);
            Assert.Greater(progress.FireDelta, 0f);
        }

        [Test]
        public void MarkDirty_DoesNotDuplicateAnActiveReaction()
        {
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 80f, water: 30f);
            runtime.MarkDirty(TriggerSource);
            ElementReactionFrame first = runtime.Tick(in state, 0.01f);

            runtime.MarkDirty(TriggerSource);
            ElementReactionFrame second = runtime.Tick(in state, 0.01f);

            Assert.AreEqual(1, first.SignalCount);
            Assert.AreEqual(ElementReactionPhase.Started, first.Signal0.Phase);
            Assert.AreEqual(0, second.SignalCount);
        }

        [Test]
        public void ThreeDifferentProcesses_CanBeActiveWithoutBreakingSignalBudget()
        {
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 80f, water: 9f, poison: 40f, goo: 40f);
            runtime.MarkDirty(TriggerSource);

            ElementReactionFrame frame = runtime.Tick(in state, 0.01f);

            // Water=9 只启动无正式事件的低速中和；Toxic 与 Ignite 各发布 Started。
            Assert.IsTrue(runtime.IsExtinguishActive);
            Assert.IsTrue(runtime.IsToxicActive);
            Assert.IsTrue(runtime.IsIgniteActive);
            Assert.AreEqual(2, frame.SignalCount);
            Assert.AreEqual(ElementReactionId.ToxicCombustion, frame.Signal0.Reaction);
            Assert.AreEqual(ElementReactionId.IgniteGoo, frame.Signal1.Reaction);
        }

        [Test]
        public void CancelAll_PreventsPendingToxicDamage()
        {
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 30f, poison: 40f);
            runtime.MarkDirty(TriggerSource);
            state = Step(runtime, state, 0.01f);

            ElementReactionFrame cancelled = runtime.CancelAll();
            ElementReactionFrame later = runtime.Tick(in state, 1f);

            Assert.GreaterOrEqual(cancelled.SignalCount, 1);
            Assert.IsFalse(later.HasAreaDamage);
        }

        [Test]
        public void ZeroDeltaTime_DoesNotAdvanceProcesses()
        {
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 80f, water: 30f);
            runtime.MarkDirty(TriggerSource);

            ElementReactionFrame frame = runtime.Tick(in state, 0f);

            Assert.AreEqual(0f, frame.FireDelta, 1e-4f);
            Assert.AreEqual(0f, frame.WaterDelta, 1e-4f);
        }

        private static ElementReactionRuntime CreateRuntime()
        {
            return new ElementReactionRuntime(new ElementReactionTuningSnapshot(
                new ExtinguishTuning
                {
                    FormalThreshold = 10f,
                    LowRatePerSecond = 10f,
                    FormalRatePerSecond = 50f,
                },
                new WetCleanseTuning
                {
                    BudgetPerSecondAtFullWet = 30f,
                },
                new ToxicCombustionTuning
                {
                    FireThreshold = 25f,
                    PoisonThreshold = 20f,
                    WindUpSeconds = 0.2f,
                    MaxPoisonConsume = 40f,
                    MinEffectiveConsume = 5f,
                    BaseDamage = 8f,
                    DamagePerPoison = 0.4f,
                    Radius = 2.5f,
                    CooldownSeconds = 1f,
                },
                new IgniteGooTuning
                {
                    FireThreshold = 15f,
                    GooThreshold = 20f,
                    WetInhibitThreshold = 10f,
                    RequiredFireCapacity = 10f,
                    GooConsumePerSecond = 40f,
                    FirePerGoo = 1.25f,
                    CooldownSeconds = 0.5f,
                },
                poisonWetCleanseMultiplier: 0.75f,
                gooWetCleanseMultiplier: 1.25f));
        }

        private static ElementStateSnapshot Step(
            ElementReactionRuntime runtime,
            ElementStateSnapshot state,
            float deltaTime)
        {
            ElementReactionFrame frame = runtime.Tick(in state, deltaTime);
            return Apply(state, frame);
        }

        private static ElementStateSnapshot Apply(
            ElementStateSnapshot state,
            ElementReactionFrame frame)
        {
            return Snapshot(
                Clamp(state.Fire + frame.FireDelta),
                Clamp(state.Water + frame.WaterDelta),
                Clamp(state.Poison + frame.PoisonDelta),
                Clamp(state.Goo + frame.GooDelta));
        }

        private static ElementStateSnapshot Snapshot(
            float fire = 0f,
            float water = 0f,
            float poison = 0f,
            float goo = 0f)
        {
            return new ElementStateSnapshot(
                fire,
                water,
                poison,
                goo,
                FireSource,
                TriggerSource,
                TriggerSource,
                TriggerSource);
        }

        private static float Clamp(float value)
        {
            if (value < 0f)
                return 0f;
            return value > 100f ? 100f : value;
        }

        private static float Min(float a, float b)
        {
            return a < b ? a : b;
        }
    }
}

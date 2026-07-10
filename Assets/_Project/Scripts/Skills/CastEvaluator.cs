using System;
using System.Collections.Generic;
using Game.Combat;
using Unity.Profiling;

namespace Game.Skills
{
    /// <summary>求值结果摘要（产出列表通过 output 参数回填，避免每次施法都分配新 List）。</summary>
    public readonly struct CastSummary
    {
        public readonly float ManaSpent;
        public readonly bool Fizzled; // 因法力不足提前中断

        public CastSummary(float manaSpent, bool fizzled)
        {
            ManaSpent = manaSpent;
            Fizzled = fizzled;
        }
    }

    /// <summary>
    /// 法术编程系统的解释器内核：从左到右"运行"一段法杖序列，算出本次施法该产出哪些投射物。
    /// 纯逻辑、不碰 Unity 实例化（对标 Combat.DamagePipeline），可 EditMode 单测。运行时由 SpellCaster（阶段 B）把 EmitCommand 变成真实投射物。
    /// 语义：Emit 产出并消耗投射物预算；Modify 累积修正（影响其后）；Multicast 增大投射物预算；
    /// 投射物预算耗尽或法力不足即停。单遍读取、不回绕。
    /// incomingMods 让本方法可被递归调用（后期触发：命中时以快照为起点再跑子序列）。
    /// </summary>
    public static class CastEvaluator
    {
        private static readonly ProfilerMarker s_evaluateMarker = new ProfilerMarker("Spell.Evaluate");

        public static CastSummary Evaluate(
            IReadOnlyList<SpellDefinition> spells,      // 法术序列
            int baseDraws,                              // 投射物释放数
            float availableMana,                        // 当前可用法力值
            CastModifierState incomingMods,             // 外部传入的修正状态
            List<EmitCommand> output)                   // 输出列表
        {
            return EvaluateCore(spells, baseDraws, availableMana, incomingMods, output, null);
        }

        public static CastSummary EvaluateWithTrace(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            float availableMana,
            CastModifierState incomingMods,
            List<EmitCommand> output,
            CastTraceCollector trace)
        {
            if (trace == null)
                throw new ArgumentNullException(nameof(trace));

            trace.Clear();
            return EvaluateCore(spells, baseDraws, availableMana, incomingMods, output, trace);
        }

        private static CastSummary EvaluateCore(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            float availableMana,
            CastModifierState incomingMods,
            List<EmitCommand> output,
            CastTraceCollector trace)
        {
            using (s_evaluateMarker.Auto())
            {
                output.Clear();
                int drawBudget = baseDraws;
                float manaLeft = availableMana;
                float manaSpent = 0f;
                bool fizzled = false;
                CastModifierState mods = incomingMods;

                trace?.Record(new CastTraceStep(
                    CastTraceStepKind.CastStarted, -1, null,
                    drawBudget, drawBudget, manaLeft, manaLeft, mods, mods));

                if (spells == null || spells.Count == 0)
                {
                    trace?.Record(new CastTraceStep(
                        CastTraceStepKind.CastCompleted, -1, null,
                        drawBudget, drawBudget, manaLeft, manaLeft, mods, mods));
                    return new CastSummary(0f, false);
                }

                for (int i = 0; i < spells.Count; i++)
                {
                    SpellDefinition spell = spells[i];
                    int budgetBefore = drawBudget;
                    float manaBefore = manaLeft;
                    CastModifierState modsBefore = mods;

                    if (spell == null)
                    {
                        trace?.Record(new CastTraceStep(
                            CastTraceStepKind.NullSpellSkipped, i, null,
                            budgetBefore, drawBudget, manaBefore, manaLeft,
                            modsBefore, mods));
                        continue;
                    }

                    bool ended = false;
                    switch (spell.Kind)
                    {
                        case SpellKind.Modify:
                            if (!TrySpend(spell, ref manaLeft, ref manaSpent))
                            {
                                fizzled = true;
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.ManaFizzle, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods));
                                break;
                            }

                            mods = mods.Apply(spell);
                            trace?.Record(new CastTraceStep(
                                CastTraceStepKind.ModifyApplied, i, spell,
                                budgetBefore, drawBudget, manaBefore, manaLeft,
                                modsBefore, mods));
                            break;

                        case SpellKind.Multicast:
                            if (!TrySpend(spell, ref manaLeft, ref manaSpent))
                            {
                                fizzled = true;
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.ManaFizzle, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods));
                                break;
                            }

                            drawBudget += spell.ExtraDraws;
                            trace?.Record(new CastTraceStep(
                                CastTraceStepKind.MulticastApplied, i, spell,
                                budgetBefore, drawBudget, manaBefore, manaLeft,
                                modsBefore, mods));
                            break;

                        case SpellKind.Emit:
                        case SpellKind.StaticProjectile:
                            if (drawBudget <= 0)
                            {
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.DrawBudgetBlocked, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods));
                                break;
                            }

                            if (!TrySpend(spell, ref manaLeft, ref manaSpent))
                            {
                                fizzled = true;
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.ManaFizzle, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods));
                                break;
                            }

                            IReadOnlyList<SpellDefinition> payload =
                                spell.PayloadTrigger != PayloadTriggerMode.None
                                    ? CaptureSuffix(spells, i + 1)
                                    : null;
                            EmitCommand command = BakeEmit(spell, mods, payload);
                            int emitIndex = output.Count;
                            output.Add(command);
                            drawBudget--;

                            trace?.Record(new CastTraceStep(
                                CastTraceStepKind.EmitProduced, i, spell,
                                budgetBefore, drawBudget, manaBefore, manaLeft,
                                modsBefore, mods, emitIndex, command));

                            if (spell.PayloadTrigger != PayloadTriggerMode.None)
                            {
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.PayloadCaptured, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods, emitIndex, command,
                                    i + 1,
                                    command.HasPayload ? command.Payload.Count : 0,
                                    command.PayloadTrigger));
                                ended = true;
                            }
                            break;
                    }

                    if (fizzled || ended)
                        break;
                }

                trace?.Record(new CastTraceStep(
                    CastTraceStepKind.CastCompleted, -1, null,
                    drawBudget, drawBudget, manaLeft, manaLeft, mods, mods));
                return new CastSummary(manaSpent, fizzled);
            }
        }

        /// <summary>
        /// 估算当前求值层会读取到的法术 Mana 成本。它不实例化、不扣真实资源，只镜像 Evaluate 的读法。
        /// payload suffix 不在本层预付费；触发投射物读到后，本层结束，suffix 留到触发时作为新层再估算。
        /// </summary>
        public static float EstimateManaCost(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            CastModifierState incomingMods)
        {
            if (spells == null || spells.Count == 0)
                return 0f;

            int drawBudget = baseDraws;
            float manaCost = 0f;
            CastModifierState mods = incomingMods;

            for (int i = 0; i < spells.Count; i++)
            {
                SpellDefinition spell = spells[i];
                if (spell == null) continue;

                switch (spell.Kind)
                {
                    case SpellKind.Modify:
                        manaCost += SanitizedManaCost(spell);
                        mods = mods.Apply(spell);
                        break;

                    case SpellKind.Multicast:
                        manaCost += SanitizedManaCost(spell);
                        drawBudget += spell.ExtraDraws;
                        break;

                    case SpellKind.Emit:
                    case SpellKind.StaticProjectile:
                        if (drawBudget <= 0)
                            break;

                        manaCost += SanitizedManaCost(spell);
                        drawBudget--;

                        if (spell.PayloadTrigger != PayloadTriggerMode.None)
                            return manaCost;
                        break;
                }
            }

            return manaCost;
        }

        private static bool TrySpend(SpellDefinition spell, ref float manaLeft, ref float manaSpent)
        {
            float cost = SanitizedManaCost(spell);
            if (cost > manaLeft)
                return false;

            manaLeft -= cost;
            manaSpent += cost;
            return true;
        }

        private static float SanitizedManaCost(SpellDefinition spell)
        {
            return spell != null && spell.ManaCost > 0f ? spell.ManaCost : 0f;
        }

        /// <summary>
        /// 把一个 Emit 法术按当前修正快照算出最终产出指令。
        /// “本次发射命令”的纯数据快照
        /// </summary>
        private static EmitCommand BakeEmit(SpellDefinition spell, CastModifierState mods, IReadOnlyList<SpellDefinition> payload)
        {
            float damage = (spell.BaseDamage + mods.DamageAddFlat) * mods.DamageMul;
            float speed = spell.BaseSpeed * mods.SpeedMul;
            PayloadTriggerMode trigger = payload != null && payload.Count > 0
                ? spell.PayloadTrigger
                : PayloadTriggerMode.None;
            float delay = trigger == PayloadTriggerMode.AfterDelay ? spell.PayloadDelaySeconds : 0f;
            return new EmitCommand(spell.ProjectilePrefab, spell.SpawnMode, spell.LandingSitePrefab,
                                   spell.SkyfallHeight, spell.SkyfallBackOffset, spell.LandingSiteDuration,
                                   spell.ShieldReflectCount,
                                   damage, speed, spell.DamageType,
                                   mods.SpreadDegrees, mods.BounceCount, mods.UseGravity,
                                   mods.HomingRadius, mods.HomingDuration, mods.HomingTurnRateDegrees,
                                   mods.OrbitRadius, mods.OrbitAngularSpeedDegrees, mods.OrbitPhaseOffsetDegrees,
                                   mods.OrbitPlaneTiltDegrees,
                                   mods.MotionMode,
                                   spell.CastSfx, payload, mods, trigger, delay);
        }

        /// <summary>
        /// 捕获触发的载荷 = 序列中 start 起的后缀（跳过 null）。这是一条"比当前序列更短的后缀"——
        /// 递归（链式触发）据此天然收敛（每深一层、待处理序列更短），所以无需递归护栏。别把它改成整根序列。
        /// 在"命中"这种离散事件触发，一次性分配可接受。
        /// </summary>
        private static List<SpellDefinition> CaptureSuffix(IReadOnlyList<SpellDefinition> spells, int start)
        {
            var payload = new List<SpellDefinition>();
            for (int j = start; j < spells.Count; j++)
                if (spells[j] != null) payload.Add(spells[j]);
            return payload;
        }
    }
}

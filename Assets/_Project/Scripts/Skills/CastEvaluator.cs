using System;
using System.Collections.Generic;
using Game.Combat;
using Unity.Profiling;
using UnityEngine;

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
        public const int MaxActionDepth = 16;
        public const int MaxEmitCommands = 64;

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

                int i = 0;
                // Draw Budget 只阻止当前 Emit，不代表解释器应停止读取后续指令。
                // 例如 Emit → Multicast → Emit：第一个 Emit 把预算降到 0，
                // 但后面的 Multicast 可以重新增加预算，让第二个 Emit 成为有效输出。
                while (i < spells.Count && output.Count < MaxEmitCommands)
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
                        i++;
                        continue;
                    }

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
                            i++;
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
                            i++;
                            break;

                        case SpellKind.Emit:
                        case SpellKind.StaticProjectile:
                            if (drawBudget <= 0)
                            {
                                trace?.Record(new CastTraceStep(
                                    CastTraceStepKind.DrawBudgetBlocked, i, spell,
                                    budgetBefore, drawBudget, manaBefore, manaLeft,
                                    modsBefore, mods));
                                // 被预算阻止的 Emit 已经“读过”，必须前进；否则移除 while
                                // 的 drawBudget Gate 后会永远停在同一个 Slot。
                                i++;
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

                            int payloadStart = i + 1;
                            int payloadEnd = payloadStart;
                            IReadOnlyList<SpellDefinition> payload = null;
                            if (spell.PayloadTrigger != PayloadTriggerMode.None)
                            {
                                payloadEnd = FindActionEnd(
                                    spells,
                                    payloadStart,
                                    0);
                                payload = CaptureRange(
                                    spells,
                                    payloadStart,
                                    payloadEnd);
                            }

                            EmitCommand command = BakeEmit(spell, mods, payload);
                            int emitIndex = output.Count;
                            output.Add(command);
                            drawBudget--;
                            i = spell.PayloadTrigger != PayloadTriggerMode.None
                                ? payloadEnd
                                : i + 1;

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
                                    payloadStart,
                                    command.HasPayload ? command.Payload.Count : 0,
                                    command.PayloadTrigger));
                            }
                            break;
                    }

                    if (fizzled)
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

            int i = 0;
            // 必须镜像 Evaluate：零预算 Emit 不计费但会被跳过，后续 Multicast
            // 仍可能重新开放预算，因此不能在 drawBudget==0 时提前结束估算。
            while (i < spells.Count)
            {
                SpellDefinition spell = spells[i];
                if (spell == null)
                {
                    i++;
                    continue;
                }

                switch (spell.Kind)
                {
                    case SpellKind.Modify:
                        manaCost += SanitizedManaCost(spell);
                        mods = mods.Apply(spell);
                        i++;
                        break;

                    case SpellKind.Multicast:
                        manaCost += SanitizedManaCost(spell);
                        drawBudget += spell.ExtraDraws;
                        i++;
                        break;

                    case SpellKind.Emit:
                    case SpellKind.StaticProjectile:
                        if (drawBudget <= 0)
                        {
                            i++;
                            break;
                        }

                        manaCost += SanitizedManaCost(spell);
                        drawBudget--;

                        if (spell.PayloadTrigger != PayloadTriggerMode.None)
                        {
                            i = FindActionEnd(spells, i + 1, 0);
                        }
                        else
                        {
                            i++;
                        }
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
            float damageMultiplier = Mathf.Max(0f, mods.DamageMul);
            float damage = Mathf.Max(
                0f,
                (spell.BaseDamage + mods.DamageAddFlat) * damageMultiplier);

            // Flat Bonus 只增加一次性直击；否则同一个 Flat 值会被爆炸和每一次 DoT Tick 重复结算。
            // Multiplier 表示整颗复合法术的强度缩放，因此同时作用于直击、爆炸和火场每跳。
            float explosionDamage = Mathf.Max(0f, spell.ExplosionDamage * damageMultiplier);
            float fireFieldDamagePerTick = Mathf.Max(
                0f,
                spell.FireFieldDamagePerTick * damageMultiplier);
            float fireFieldTickInterval = Mathf.Max(0.05f, spell.FireFieldTickInterval);
            float fireFieldDuration = Mathf.Max(0f, spell.FireFieldDuration);
            float speed = spell.BaseSpeed * mods.SpeedMul;
            PayloadTriggerMode trigger = payload != null && payload.Count > 0
                ? spell.PayloadTrigger
                : PayloadTriggerMode.None;
            float delay = trigger == PayloadTriggerMode.AfterDelay ? spell.PayloadDelaySeconds : 0f;
            return new EmitCommand(spell.ProjectilePrefab, spell.SpawnMode, spell.LandingSitePrefab,
                                   spell.SkyfallHeight, spell.SkyfallBackOffset, spell.LandingSiteDuration,
                                   spell.ShieldReflectCount,
                                   damage, explosionDamage,
                                   fireFieldDamagePerTick, fireFieldTickInterval, fireFieldDuration,
                                   speed, spell.DamageType,
                                   mods.SpreadDegrees, mods.BounceCount, mods.UseGravity,
                                   mods.HomingRadius, mods.HomingDuration, mods.HomingTurnRateDegrees,
                                   mods.OrbitRadius, mods.OrbitAngularSpeedDegrees, mods.OrbitPhaseOffsetDegrees,
                                   mods.OrbitPlaneTiltDegrees,
                                   mods.MotionMode,
                                   spell.CastSfx, payload, mods, trigger, delay);
        }

        /// <summary>
        /// 从 start 开始只解析一个完整 Action 的边界。Multicast 扩大该 Action 内部的 Draw Budget；
        /// Trigger 自己只占一个 Draw，但它的 Payload Action 必须递归越过，外层才能继续抽取下一条并行 Action。
        /// 深度上限是防御性护栏；正常输入仍因每层至少越过一个 Trigger 而天然收敛。
        /// </summary>
        private static int FindActionEnd(
            IReadOnlyList<SpellDefinition> spells,
            int start,
            int depth)
        {
            if (spells == null || start >= spells.Count || depth >= MaxActionDepth)
                return start;

            int cursor = start;
            int drawBudget = 1;
            while (cursor < spells.Count && drawBudget > 0)
            {
                SpellDefinition spell = spells[cursor];
                cursor++;
                if (spell == null)
                    continue;

                switch (spell.Kind)
                {
                    case SpellKind.Multicast:
                        drawBudget += Math.Max(0, spell.ExtraDraws);
                        break;

                    case SpellKind.Emit:
                    case SpellKind.StaticProjectile:
                        drawBudget--;
                        if (spell.PayloadTrigger != PayloadTriggerMode.None)
                        {
                            cursor = FindActionEnd(
                                spells,
                                cursor,
                                depth + 1);
                        }
                        break;
                }
            }

            return cursor;
        }

        /// <summary>
        /// Trigger Payload 保存一个 Action 的独立指令切片，而不是吞掉整段剩余 Wand。
        /// 该分配发生在离散施法求值，不位于 Update/FixedUpdate 热路径。
        /// </summary>
        private static List<SpellDefinition> CaptureRange(
            IReadOnlyList<SpellDefinition> spells,
            int start,
            int end)
        {
            var payload = new List<SpellDefinition>();
            int clampedEnd = Math.Min(end, spells.Count);
            for (int j = start; j < clampedEnd; j++)
                if (spells[j] != null) payload.Add(spells[j]);
            return payload;
        }
    }
}

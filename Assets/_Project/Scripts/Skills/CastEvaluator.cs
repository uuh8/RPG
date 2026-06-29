using System.Collections.Generic;
using Game.Combat;

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
    /// 语义：Emit 产出（消耗预算）；Modify 累积修正（影响其后）；Multicast 增大预算；预算耗尽或法力不足即停。单遍读取、不回绕。
    /// incomingMods 让本方法可被递归调用（后期触发：命中时以快照为起点再跑子序列）。
    /// </summary>
    public static class CastEvaluator
    {
        public static CastSummary Evaluate(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            float availableMana,
            CastModifierState incomingMods,
            List<EmitCommand> output)
        {
            output.Clear();
            if (spells == null || spells.Count == 0)
                return new CastSummary(0f, false);

            int drawBudget = baseDraws;
            float manaLeft = availableMana;
            float manaSpent = 0f;
            bool fizzled = false;
            CastModifierState mods = incomingMods;

            for (int i = 0; i < spells.Count; i++)
            {
                SpellDefinition spell = spells[i];
                if (spell == null) continue;

                bool ended = false;

                switch (spell.Kind)
                {
                    case SpellKind.Modify:
                        mods = mods.Apply(spell);
                        break;

                    case SpellKind.Multicast:
                        drawBudget += spell.ExtraDraws;
                        break;

                    case SpellKind.Emit:
                        if (drawBudget <= 0) break;          // 预算用尽：本发不产出
                        if (spell.ManaCost > manaLeft)        // 法力不足：中断本次施法
                        {
                            fizzled = true;
                            break;
                        }
                        manaLeft -= spell.ManaCost;
                        manaSpent += spell.ManaCost;
                        if (spell.IsTrigger)
                        {
                            // 触发：把"其后的后缀"捕获为载荷（命中时再跑），并结束本层求值（后缀不在本层单独产出）
                            output.Add(BakeEmit(spell, mods, CaptureSuffix(spells, i + 1)));
                            drawBudget--;
                            ended = true;
                            break;
                        }
                        output.Add(BakeEmit(spell, mods, null)); // 普通产出：无载荷
                        drawBudget--;
                        break;
                }

                if (fizzled || ended) break; // 法力不足 或 触发结束本层 → 跳出读取循环
            }

            return new CastSummary(manaSpent, fizzled);
        }

        /// <summary>把一个 Emit 法术按当前修正快照算出最终产出指令。payload 仅触发投射物非空。</summary>
        private static EmitCommand BakeEmit(SpellDefinition spell, CastModifierState mods, IReadOnlyList<SpellDefinition> payload)
        {
            float damage = (spell.BaseDamage + mods.DamageAddFlat) * mods.DamageMul;
            float speed = spell.BaseSpeed * mods.SpeedMul;
            return new EmitCommand(spell.ProjectilePrefab, damage, speed, spell.DamageType, mods.SpreadDegrees, spell.CastSfx, payload, mods);
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

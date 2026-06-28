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
            CastModifierState mods = incomingMods;

            for (int i = 0; i < spells.Count; i++)
            {
                SpellDefinition spell = spells[i];
                if (spell == null) continue;

                switch (spell.Kind)
                {
                    case SpellKind.Modify:
                        mods = mods.Apply(spell);
                        break;

                    case SpellKind.Multicast:
                        drawBudget += spell.ExtraDraws; // 双重 +1 / 三重 +2，扩大本次预算
                        break;

                    case SpellKind.Emit:
                        if (drawBudget <= 0) break;
                        output.Add(BakeEmit(spell, mods));
                        drawBudget--;
                        break;
                }
            }

            return new CastSummary(0f, false);
        }

        /// <summary>把一个 Emit 法术按当前修正快照算出最终产出指令。伤害=(基础+平铺加)×倍率；速度=基础×倍率。</summary>
        private static EmitCommand BakeEmit(SpellDefinition spell, CastModifierState mods)
        {
            float damage = (spell.BaseDamage + mods.DamageAddFlat) * mods.DamageMul;
            float speed = spell.BaseSpeed * mods.SpeedMul;
            return new EmitCommand(spell.ProjectilePrefab, damage, speed, spell.DamageType, mods.SpreadDegrees);
        }
    }
}

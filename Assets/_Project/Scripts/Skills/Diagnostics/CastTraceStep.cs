namespace Game.Skills
{
    public readonly struct CastTraceStep
    {
        public readonly CastTraceStepKind Kind;
        public readonly int SpellIndex;
        public readonly SpellDefinition Spell;
        public readonly int DrawBudgetBefore;
        public readonly int DrawBudgetAfter;
        public readonly float ManaLeftBefore;
        public readonly float ManaLeftAfter;
        public readonly CastModifierState ModifiersBefore;
        public readonly CastModifierState ModifiersAfter;
        public readonly int EmitIndex;
        public readonly EmitCommand Emit;
        public readonly int PayloadStartIndex;
        public readonly int PayloadCount;
        public readonly PayloadTriggerMode PayloadTrigger;

        public CastTraceStep(
            CastTraceStepKind kind,
            int spellIndex,
            SpellDefinition spell,
            int drawBudgetBefore,
            int drawBudgetAfter,
            float manaLeftBefore,
            float manaLeftAfter,
            CastModifierState modifiersBefore,
            CastModifierState modifiersAfter,
            int emitIndex = -1,
            EmitCommand emit = default,
            int payloadStartIndex = -1,
            int payloadCount = 0,
            PayloadTriggerMode payloadTrigger = PayloadTriggerMode.None)
        {
            Kind = kind;
            SpellIndex = spellIndex;
            Spell = spell;
            DrawBudgetBefore = drawBudgetBefore;
            DrawBudgetAfter = drawBudgetAfter;
            ManaLeftBefore = manaLeftBefore;
            ManaLeftAfter = manaLeftAfter;
            ModifiersBefore = modifiersBefore;
            ModifiersAfter = modifiersAfter;
            EmitIndex = emitIndex;
            Emit = emit;
            PayloadStartIndex = payloadStartIndex;
            PayloadCount = payloadCount;
            PayloadTrigger = payloadTrigger;
        }
    }
}

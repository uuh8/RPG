using System;
using Game.Skills;

namespace Game.Run
{
    /// <summary>
    /// 进入 Boss 区域时的法杖快照。数组容器由 Snapshot 独立持有，
    /// 但 SpellDefinition 仍共享只读 Asset 引用，这是刻意采用的 shallow copy。
    /// </summary>
    public readonly struct BossCheckpointSnapshot
    {
        private readonly SpellDefinition[] _spells;

        public int BaseDraws { get; }
        public int SpellCount => _spells?.Length ?? 0;
        public bool IsValid { get; }

        public BossCheckpointSnapshot(SpellDefinition[] spells, int baseDraws)
        {
            if (spells == null)
                throw new ArgumentNullException(nameof(spells));

            _spells = RunSpellInventoryOps.CopyEntries(spells);
            BaseDraws = baseDraws;
            IsValid = true;
        }

        /// <summary>
        /// 每次恢复都返回新数组，避免 Runtime Wand 的后续编辑反向污染 Checkpoint。
        /// </summary>
        public SpellDefinition[] CopySpells()
        {
            return IsValid
                ? RunSpellInventoryOps.CopyEntries(_spells)
                : Array.Empty<SpellDefinition>();
        }
    }
}

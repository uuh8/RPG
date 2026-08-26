using System;
using Game.Combat;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>固定容量 World Reaction 状态表；以 Reaction+FireCell+LiquidCell 去重，不在 Tick 中分配。</summary>
    public sealed class MaterialReactionProcessTable
    {
        private struct Entry
        {
            public bool Used;
            public ElementReactionId Reaction;
            public Vector3Int FireCell;
            public Vector3Int LiquidCell;
            public ToxicCombustionProcess Toxic;
            public float CooldownRemaining;
        }

        private readonly Entry[] _entries;
        public MaterialReactionProcessTable(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _entries = new Entry[capacity];
        }

        public bool TryStartToxic(
            Vector3Int fireCell,
            Vector3Int poisonCell,
            int fireGmu,
            int poisonGmu,
            in ToxicCombustionTuning tuning)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].Used && _entries[i].Reaction == ElementReactionId.ToxicCombustion
                    && _entries[i].FireCell == fireCell && _entries[i].LiquidCell == poisonCell)
                    return false;
            }
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].Used) continue;
                var process = new ToxicCombustionProcess();
                if (!process.TryStart(fireGmu, poisonGmu, in tuning)) return false;
                _entries[i] = new Entry { Used = true, Reaction = ElementReactionId.ToxicCombustion,
                    FireCell = fireCell, LiquidCell = poisonCell, Toxic = process };
                return true;
            }
            return false;
        }

        public int TickToxic(
            float deltaTime,
            ILiquidOccupancyReadOnly occupancy,
            in ToxicCombustionTuning tuning,
            ToxicWorldReactionResolution[] destination)
        {
            int count = 0;
            for (int i = 0; i < _entries.Length && count < destination.Length; i++)
            {
                if (!_entries[i].Used) continue;
                Entry entry = _entries[i];
                if (entry.CooldownRemaining > 0f)
                {
                    entry.CooldownRemaining = Mathf.Max(0f, entry.CooldownRemaining - deltaTime);
                    if (entry.CooldownRemaining <= 0f)
                        entry.Used = false;
                    _entries[i] = entry;
                    continue;
                }
                occupancy.TryGetAmount(entry.LiquidCell, Game.Materials.MaterialId.Poison, out byte current);
                if (!entry.Toxic.Tick(deltaTime, current, in tuning, out int consumed, out float damage, out float radius))
                {
                    _entries[i] = entry;
                    continue;
                }
                destination[count++] = new ToxicWorldReactionResolution(entry.LiquidCell, consumed, damage, radius);
                // 反应完成后保留同一 Contact Key 到 Cooldown 结束，防止持续接触每 Tick 重复毒爆。
                entry.Toxic = default;
                entry.CooldownRemaining = Mathf.Max(0f, tuning.CooldownSeconds);
                entry.Used = entry.CooldownRemaining > 0f;
                _entries[i] = entry;
            }
            return count;
        }
    }

    public readonly struct ToxicWorldReactionResolution
    {
        public readonly Vector3Int LiquidCell;
        public readonly int ConsumedGmu;
        public readonly float Damage;
        public readonly float Radius;
        public ToxicWorldReactionResolution(Vector3Int liquidCell, int consumedGmu, float damage, float radius)
        { LiquidCell = liquidCell; ConsumedGmu = consumedGmu; Damage = damage; Radius = radius; }
    }
}

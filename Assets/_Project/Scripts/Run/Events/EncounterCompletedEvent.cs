using Game.Core;
using Game.Skills;

namespace Game.Run
{
    /// <summary>
    /// 当前 Encounter 被纯 Tracker 判定为清场后发布一次。
    /// RewardSpell 是只读资产引用；真正把奖励加入本局法术库由后续 RunSpellSession 负责。
    /// </summary>
    public struct EncounterCompletedEvent : IGameEvent
    {
        public int EncounterIndex;
        public int EncounterCount;
        public SpellDefinition RewardSpell;
    }
}

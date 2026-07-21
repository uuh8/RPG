using Game.Core;
using Game.Skills;

namespace Game.Run
{
    /// <summary>
    /// 单局法术背包成功获得一个新法术后发布。
    /// 事件只携带结果快照，不让 UI 或其他 Presentation 反向修改 RunSpellSession。
    /// </summary>
    public struct SpellRewardGrantedEvent : IGameEvent
    {
        public SpellDefinition Spell;
        public int LibraryCount;
    }
}

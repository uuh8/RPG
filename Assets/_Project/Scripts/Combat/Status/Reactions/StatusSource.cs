namespace Game.Combat
{
    /// <summary>
    /// 状态来源的值快照。只保存伤害归属需要的事实，不持有可能被销毁的攻击者对象。
    /// </summary>
    public readonly struct StatusSource
    {
        public readonly int Id;
        public readonly byte Team;

        public StatusSource(int id, byte team)
        {
            Id = id;
            Team = team;
        }
    }
}

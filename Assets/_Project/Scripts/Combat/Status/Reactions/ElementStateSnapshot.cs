namespace Game.Combat
{
    /// <summary>
    /// 某个求值时刻的只读元素事实。
    /// Snapshot 让纯规则先完整读取旧状态，再由 Adapter 统一写回结果，避免边判断边修改导致顺序漂移。
    /// </summary>
    public readonly struct ElementStateSnapshot
    {
        public readonly float Fire;
        public readonly float Water;
        public readonly float Poison;
        public readonly float Goo;

        public readonly StatusSource FireSource;
        public readonly StatusSource WaterSource;
        public readonly StatusSource PoisonSource;
        public readonly StatusSource GooSource;

        public ElementStateSnapshot(
            float fire,
            float water,
            float poison,
            float goo,
            StatusSource fireSource,
            StatusSource waterSource,
            StatusSource poisonSource,
            StatusSource gooSource)
        {
            Fire = fire;
            Water = water;
            Poison = poison;
            Goo = goo;
            FireSource = fireSource;
            WaterSource = waterSource;
            PoisonSource = poisonSource;
            GooSource = gooSource;
        }
    }
}

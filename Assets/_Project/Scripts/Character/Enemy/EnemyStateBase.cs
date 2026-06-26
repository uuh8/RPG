namespace Game.Character
{
    /// <summary>敌人状态抽象基类。平行于 PlayerStateBase：普通 C# 对象，持有具体 EnemyController。</summary>
    public abstract class EnemyStateBase
    {
        protected readonly EnemyController _enemy;
        protected EnemyStateBase(EnemyController enemy) { _enemy = enemy; }

        public abstract void Enter();
        public abstract void Update();
        public abstract void Exit();
    }
}

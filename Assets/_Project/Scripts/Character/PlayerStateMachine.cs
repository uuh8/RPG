namespace Game.Character
{
    /// <summary>
    /// 状态机调度员。普通 C# 类，不是 MonoBehaviour。
    /// 由 PlayerController 持有，PlayerController.Update() 手动驱动它。
    /// 只负责"持有当前状态"和"切换状态"，不含游戏逻辑。
    /// </summary>
    public class PlayerStateMachine
    {
        // 当前正在运行的状态，外部只读
        public PlayerStateBase CurrentState { get; private set; }

        public void ChangeState(PlayerStateBase newState)
        {
            // ?. 是空条件运算符，CurrentState 为 null（初始化时）时不调用 Exit
            CurrentState?.Exit();
            CurrentState = newState;
            CurrentState.Enter();
        }

        /// <summary>
        /// 每帧推进当前状态，由 PlayerController.Update() 调用。
        /// 状态机自己不知道 Unity 的生命周期，需要外部手动驱动。
        /// </summary>
        public void Update()
        {
            CurrentState?.Update();
        }
    }
}

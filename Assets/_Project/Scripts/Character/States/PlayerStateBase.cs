using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 职责：所有状态的抽象基类。
    /// 定义"每个状态必须实现什么（生命周期三方法）"，以及"每个状态都能拿到什么（Context 引用 + 公共 Hash）"。
    /// 它本身不做任何事，只定义规范。
    /// </summary>
    public abstract class PlayerStateBase
    {
        // Context：所有 State 通过这个引用访问角色数据
        // protected = 子类可访问，外部不能访问
        protected readonly PlayerController _player;

        // JumpHash 保留在这里：jump 是"事件型"Trigger，由 State 在起跳时主动触发
        // SpeedHash / IsGroundedHash 已移至 PlayerController.SyncAnimatorParameters()
        // 原因：它们是持续状态型参数，由 Controller 每帧统一同步，State 不再负责维护
        protected static readonly int JumpHash = Animator.StringToHash("jump");

        // 构造函数：创建 State 时必须传入 PlayerController
        // 这是依赖注入，State 自己不找组件，由外部传进来
        protected PlayerStateBase(PlayerController player)
        {
            _player = player;
        }

        // 三个方法对应状态生命周期的三个时机：
        public abstract void Enter();
        public abstract void Update();
        public abstract void Exit();

        /// <summary>
        /// 朝移动方向平滑转向。地面和空中逻辑完全一致，提到基类共享，避免重复。
        /// 无输入时（MoveDirection ≈ 0）直接返回，不强制朝向。
        /// </summary>
        protected void HandleRotation()
        {
            if (_player.MoveDirection.sqrMagnitude < 0.01f) return;

            /*Quaternion.LookRotation 是 Unity 提供的一个极其重要的 API。
             它接收一个三维向量（MoveDirection，即玩家想要移动的方向），
             然后计算并返回一个四元数（Quaternion）。
             这个四元数代表了“让物体的正前方（Z轴）指向该向量所需的目标旋转角度”。*/
            Quaternion targetRotation = Quaternion.LookRotation(_player.MoveDirection);

            // Slerp：球面线性插值，用于在两个旋转状态之间进行平滑过渡，且帧率无关
            _player.transform.rotation = Quaternion.Slerp(
                _player.transform.rotation,                 // 当前旋转
                targetRotation,                             // 目标旋转
                _player.RotationSpeed * Time.deltaTime    // 插值比例
            );
        }
    }
}

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
        // 类型是基类 PlayerControllerBase：共享状态（Grounded/Airborne/Sliding/Dash）只需基类成员，
        // 角色专属状态（如 Warrior 的 PlayerAttackState）另存一份 typed 子类引用拿专属成员。
        protected readonly PlayerControllerBase _player;

        // JumpHash 保留在这里：jump 是"事件型"Trigger，由 State 在起跳时主动触发
        // SpeedHash / IsGroundedHash 已移至 PlayerControllerBase.SyncAnimatorParameters()
        // 原因：它们是持续状态型参数，由 Controller 每帧统一同步，State 不再负责维护
		protected static readonly int JumpHash   = Animator.StringToHash("jump");

        // 构造函数：创建 State 时必须传入 PlayerControllerBase
        // 这是依赖注入，State 自己不找组件，由外部传进来
        protected PlayerStateBase(PlayerControllerBase player)
        {
            _player = player;
        }

        // 三个方法对应状态生命周期的三个时机：
        public abstract void Enter();
        public abstract void Update();
        public abstract void Exit();

        /// <summary>
        /// 朝"锁存的目标朝向"平滑转向。地面和空中逻辑完全一致，提到基类共享。
        /// 关键：有移动输入时把目标朝向锁存为该方向；之后**即使松开输入也继续转到位**，
        /// 避免"按一下很短就松手 → 角色停在转身中途"的问题。无输入也只是朝最后锁存方向转完后静止。
        /// </summary>
        protected void HandleRotation()
        {
            // 有移动输入：把目标朝向锁存为当前移动方向（水平）
            if (_player.MoveDirection.sqrMagnitude >= 0.01f)
                _player.TargetFacing = _player.MoveDirection;

            // 始终朝锁存的目标朝向转，直到对齐——这一步与"是否仍按住"无关，所以转身一定会转完
            Vector3 facing = _player.TargetFacing;
            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-6f) return; // 目标退化（理论上不会）才跳过

            // Quaternion.LookRotation：让物体 +Z 指向 facing 的目标旋转
            Quaternion targetRotation = Quaternion.LookRotation(facing);

            // Slerp：球面线性插值，平滑过渡；已对齐时本身就是无操作，不会抖
            _player.transform.rotation = Quaternion.Slerp(
                _player.transform.rotation,              // 当前旋转
                targetRotation,                          // 目标旋转
                _player.RotationSpeed * Time.deltaTime   // 插值比例
            );
        }

        /// <summary>
        /// 远程瞄准：求屏幕中心（轨道相机朝向）的瞄准点。结果含俯仰，调用方据此算发射方向（瞄准点 - 生成点）。
        /// 实现已上移到 PlayerControllerBase，供状态与控制器（点按瞬间锁存准心）共用；此处仅转发。
        /// buffer 由调用状态预分配复用（零每帧 GC）。
        /// </summary>
        protected Vector3 ResolveAimTargetPoint(LayerMask aimMask, float maxDistance, RaycastHit[] buffer)
            => _player.ResolveAimTargetPoint(aimMask, maxDistance, buffer);

        /// <summary>把角色平滑转向相机的水平朝向（只 yaw）。远程攻击期间用：身体朝准心方向，"朝哪瞄就朝哪打"。</summary>
        protected void HandleAimRotation()
        {
            Camera cam = _player.MainCamera;
            if (cam == null) return;
            Vector3 f = cam.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) return;
            Quaternion target = Quaternion.LookRotation(f);
            _player.transform.rotation = Quaternion.Slerp(
                _player.transform.rotation, target, _player.RotationSpeed * Time.deltaTime);
        }
    }
}

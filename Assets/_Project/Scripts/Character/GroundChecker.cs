using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 独立的地面检测组件，挂在 Player GO 上。
    /// 每帧执行 SphereCast，对外暴露 IsGrounded / GroundNormal / GroundAngle。
    /// 状态机里所有 State 通过这三个属性读地面数据，不直接用 isGrounded。
    /// </summary>
    public class GroundChecker : MonoBehaviour
    {
        // 检测球半径：略小于 CharacterController.radius（0.3）
        // 太大会误检测到侧面的墙，太小退化成 Raycast 的"线"
        [SerializeField] private float _sphereRadius = 0.3f;

        // 从脚底往下最多探这么远，范围内有地面 = 接地
        // 不能太大（会在半空中就判接地），不能太小（会漏检轻微凹凸地面）
        [SerializeField] private float _groundCheckDistance = 0.2f;

        // 只检测这些 Layer 上的碰撞体，LayerMask 在底层是一个 32 位的整数（int），每一bit对应一个 Layer
        // 用 LayerMask 的意义：防止把其他角色、可拾取物等误判为地面
        [SerializeField] private LayerMask _groundLayer;

        // ── 对外只读属性，State 直接读这三个，不允许外部写 ──────────────

        // 是否接地
        public bool IsGrounded { get; private set; }

        // 脚下地面的法线方向（垂直平地时 = Vector3.up）
        // 斜坡时法线会倾斜，M2-3 斜坡滑落靠这个计算滑动方向
        public Vector3 GroundNormal { get; private set; } = Vector3.up;

        // 地面法线与世界 Up 的夹角（平地 = 0°，45°斜坡 = 45°）
        // 与 CharacterController.slopeLimit 对比，判断是否超坡
        public float GroundAngle { get; private set; }

        private void Update()
        {
            CheckGround();
        }

        private void CheckGround()
        {
            // 起点从脚底上方一个球半径处开始
            // 原因：脚底(transform.position)恰好贴着地面
            // 如果从脚底直接发射，球的起点已经和地面重叠，SphereCast 无法正确检测
            // 上移一个球半径，保证球的初始位置在地面以上
            Vector3 origin = transform.position + Vector3.up * _sphereRadius;

            if (Physics.SphereCast(
                    origin,
                    _sphereRadius,
                    Vector3.down,    // 向下扫
                    out RaycastHit hit,     // 撞到了什么（含法线、距离、碰撞点）
                    _groundCheckDistance,   // 最多扫这么远
                    _groundLayer,        // 只检测地面层
                    QueryTriggerInteraction.Ignore)) // 忽略 Trigger（只检实体碰撞体）
            {
                IsGrounded = true;
                GroundNormal = hit.normal; // 记录地面法线，斜坡处理要用
                GroundAngle = Vector3.Angle(hit.normal, Vector3.up);
            }
            else
            {
                IsGrounded = false;
                GroundNormal = Vector3.up; // 空中时法线默认朝上，避免 null
                GroundAngle = 0f;
            }
        }

        // 在 Scene 视图中可视化检测范围（只在选中 Player 时显示）
        // 绿色 = 接地，红色 = 悬空
        // 这不影响任何游戏逻辑，只是 Debug 用的辅助线
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = IsGrounded ? Color.green : Color.red;

            Vector3 origin = transform.position + Vector3.up * _sphereRadius;

            // 画起点的球（检测起始位置）
            Gizmos.DrawWireSphere(origin, _sphereRadius);

            // 画终点的球（检测最远到哪里）
            Gizmos.DrawWireSphere(
                origin + Vector3.down * _groundCheckDistance,
                _sphereRadius);
        }
    }
}

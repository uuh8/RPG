using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// 飞行箭矢。投射物基类的最简实现：仅命中结算 + 销毁，无额外命中表现（不重写 OnImpact）。
    /// 抛物线箭矢，生成时 Init 用默认 useGravity = true。
    /// 注意：模型箭尖沿 +Y，预制体上 _modelForwardOffsetEuler 应保持 (90,0,0)
    /// （基类字段默认 0；既有 Arrow 预制体已序列化该值，按字段名迁移后保留）。
    /// </summary>
    public class Arrow : ProjectileBase
    {
        // 命中表现为空：基类已处理 同阵营穿过 / 敌方 ReceiveHit / 命中销毁。
    }
}

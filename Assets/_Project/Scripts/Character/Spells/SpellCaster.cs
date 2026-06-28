using System.Collections.Generic;
using UnityEngine;
using Game.Core;
using Game.Combat;
using Game.Skills;

namespace Game.Character
{
    /// <summary>
    /// 法术施放器：把"纯求值结果"落地成真实投射物。挂在施法者（法师，未来也可敌人）身上。
    /// CastWand：运行当前法杖 → CastEvaluator 求值 → 按 EmitCommand 逐个 Instantiate ProjectileBase，
    /// 朝 aimPoint 用 SpellAiming 散成扇形，复用现有 ProjectileBase.Init（直线投射物关重力）。
    /// 纯求值在 Game.Skills；本组件是"数据 → Unity 实例化"的唯一桥。
    /// </summary>
    public class SpellCaster : MonoBehaviour
    {
        [SerializeField] private WandLoadout _wand;
        [Tooltip("可用法力（占位）。资源系统是后续阶段；现给一个大值，求值器不会 fizzle。")]
        [SerializeField] private float _availableMana = 9999f;

        // 求值产出缓冲（预分配复用，求值器内 Clear()）——离散输入触发，避免每次施放新建 List
        private readonly List<EmitCommand> _emits = new List<EmitCommand>(16);

        public WandLoadout Wand => _wand;

        /// <summary>
        /// 运行当前法杖：求值 → 生成投射物。返回产出数量。
        /// spawnPos 投射物生成点；aimPoint 准心瞄准点（方向 = aimPoint - spawnPos）；team/attackerId 阵营快照；
        /// casterCollider 施法者碰撞体（忽略自撞）。
        /// </summary>
        public int CastWand(Vector3 spawnPos, Vector3 aimPoint, byte team, int attackerId, Collider casterCollider)
        {
            if (_wand == null || _wand.Spells == null || _wand.Spells.Length == 0)
            {
                GameLog.Warn("SpellCaster 未配置 WandLoadout 或法杖为空，无法施放", "Skills");
                return 0;
            }

            CastEvaluator.Evaluate(_wand.Spells, _wand.BaseDraws, _availableMana, CastModifierState.Default, _emits);

            Vector3 baseDir = aimPoint - spawnPos;
            if (baseDir.sqrMagnitude < 1e-6f) baseDir = transform.forward; // 退化兜底
            baseDir.Normalize();

            int count = _emits.Count;
            for (int i = 0; i < count; i++)
            {
                EmitCommand cmd = _emits[i];
                if (cmd.ProjectilePrefab == null)
                {
                    GameLog.Warn("EmitCommand.ProjectilePrefab 为空（法术未配置预制体），跳过该发", "Skills");
                    continue;
                }

                float yaw = SpellAiming.SpreadOffsetDegrees(i, count, cmd.SpreadDegrees);
                Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * baseDir;

                GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, Quaternion.LookRotation(dir));
                ProjectileBase proj = go.GetComponent<ProjectileBase>();
                if (proj == null)
                {
                    GameLog.Warn($"法术预制体 {cmd.ProjectilePrefab.name} 上没有 ProjectileBase 组件", "Skills");
                    Object.Destroy(go);
                    continue;
                }

                // 直线投射物关重力（与现有火球一致）；命中走标准 ProjectileBase → ReceiveHit
                proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, dir * cmd.Speed, casterCollider, useGravity: false);
            }

            return count;
        }
    }
}

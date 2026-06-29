using System.Collections.Generic;
using UnityEngine;
using Game.Core;
using Game.Combat;
using Game.Skills;

namespace Game.Character
{
    /// <summary>
    /// 法术施放器：把"纯求值结果"落地成真实投射物。挂在施法者（法师，未来也可敌人）身上。
    /// CastWand 跑当前法杖；RunCast 是可复用/递归的核心——触发投射物命中时，以预算 1 在命中点再 RunCast 它的载荷（链式触发）。
    /// 递归由有限法杖天然收敛（载荷是更短后缀），无需护栏。纯求值在 Game.Skills；本组件是"数据 → Unity 实例化"的唯一桥。
    /// </summary>
    public class SpellCaster : MonoBehaviour
    {
        [SerializeField] private WandLoadout _wand;
        [Tooltip("可用法力（占位）。资源系统是后续阶段；现给一个大值，求值器不会 fizzle。")]
        [SerializeField] private float _availableMana = 9999f;

        // 求值产出缓冲（预分配复用，求值器内 Clear()）
        private readonly List<EmitCommand> _emits = new List<EmitCommand>(16);
        // 本次施法已播过的音效：去重，多重/连发的同一音效只响一次
        private readonly HashSet<AudioClip> _playedSfx = new HashSet<AudioClip>();

        public WandLoadout Wand => _wand;

        /// <summary>运行当前法杖：朝 aimPoint 从 spawnPos 施放。返回产出数。</summary>
        public int CastWand(Vector3 spawnPos, Vector3 aimPoint, byte team, int attackerId, Collider casterCollider)
        {
            if (_wand == null || _wand.Spells == null || _wand.Spells.Length == 0)
            {
                GameLog.Warn("SpellCaster 未配置 WandLoadout 或法杖为空，无法施放", "Skills");
                return 0;
            }

            Vector3 baseDir = aimPoint - spawnPos;
            if (baseDir.sqrMagnitude < 1e-6f) baseDir = transform.forward; // 退化兜底
            baseDir.Normalize();

            return RunCast(_wand.Spells, _wand.BaseDraws, CastModifierState.Default,
                           spawnPos, baseDir, team, attackerId, casterCollider);
        }

        /// <summary>
        /// 运行一段法术序列，在 spawnPos 沿 baseDir 生成投射物。返回求值产出数。
        /// 触发投射物订阅命中事件：命中时以"预算 1"在命中点再 RunCast 它的载荷（链式触发递归）。
        /// 命中回调发生在之后的物理帧、不在本循环内重入，故复用 _emits 安全。
        /// </summary>
        private int RunCast(IReadOnlyList<SpellDefinition> spells, int baseDraws, CastModifierState incomingMods,
                            Vector3 spawnPos, Vector3 baseDir, byte team, int attackerId, Collider casterCollider)
        {
            CastEvaluator.Evaluate(spells, baseDraws, _availableMana, incomingMods, _emits);
            _playedSfx.Clear();

            int count = _emits.Count;
            for (int i = 0; i < count; i++)
            {
                EmitCommand cmd = _emits[i];

                // 施放音效：一次施法里同一音效只播一次
                if (cmd.CastSfx != null && _playedSfx.Add(cmd.CastSfx))
                    AudioSource.PlayClipAtPoint(cmd.CastSfx, spawnPos);

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

                // 触发：命中时在命中点以预算 1 跑载荷（载荷里再有触发 → 自然链式；有限后缀 → 自然收敛）
                if (cmd.HasPayload)
                {
                    IReadOnlyList<SpellDefinition> payload = cmd.Payload;
                    CastModifierState payloadMods = cmd.PayloadMods;
                    proj.Impacted += (hitPoint, hitDir) =>
                        RunCast(payload, 1, payloadMods, hitPoint, hitDir, team, attackerId, casterCollider);
                }

                // 直线投射物关重力；命中走标准 ProjectileBase → ReceiveHit
                proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, dir * cmd.Speed, casterCollider, useGravity: false);
            }

            return count;
        }
    }
}

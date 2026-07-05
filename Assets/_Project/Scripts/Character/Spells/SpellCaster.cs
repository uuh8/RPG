using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Game.Core;
using Game.Combat;
using Game.Skills;

namespace Game.Character
{
    /// <summary>
    /// 法术施放器：把 Game.Skills 的纯求值结果转换成 Unity 运行时对象。
    /// CastEvaluator 只负责解释 wand；这里负责 Instantiate、ProjectileBase.Init 和 payload 递归触发。
    /// </summary>
    public class SpellCaster : MonoBehaviour
    {
        [SerializeField] private WandLoadout _wand;
        [Tooltip("可用法力值")]
        [SerializeField] private ManaComponent _mana;

        private readonly List<EmitCommand> _emits = new List<EmitCommand>(16);
        private readonly HashSet<AudioClip> _playedSfx = new HashSet<AudioClip>();
        private readonly RaycastHit[] _landingHits = new RaycastHit[8];

        public WandLoadout Wand => _wand;

        private void Awake()
        {
            if (_mana == null)
                _mana = GetComponent<ManaComponent>();
        }

        /// <summary>运行当前法杖：从 spawnPos 朝 aimPoint 施放。返回本次求值产出数量。</summary>
        public int CastWand(Vector3 spawnPos, Vector3 aimPoint, byte team, int attackerId, Collider casterCollider)
        {
            if (_wand == null || _wand.Spells == null || _wand.Spells.Length == 0)
            {
                GameLog.Warn("SpellCaster 未配置 WandLoadout 或法杖为空，无法施放", "Skills");
                return 0;
            }

            Vector3 baseDir = aimPoint - spawnPos;
            if (baseDir.sqrMagnitude < 1e-6f)
                baseDir = transform.forward;
            baseDir.Normalize();

            return RunCast(_wand.Spells, _wand.BaseDraws, CastModifierState.Default,
                           spawnPos, baseDir, team, attackerId, casterCollider);
        }

        /// <summary>
        /// 运行一段法术序列。spawnPos 对普通 Emit 是发射点；对 SkyfallAtPoint 是落点。
        /// 触发 payload 时会用命中点作为新的 spawnPos，再以预算 1 运行 payload。
        /// </summary>
        private int RunCast(IReadOnlyList<SpellDefinition> spells, int baseDraws, CastModifierState incomingMods,
                            Vector3 spawnPos, Vector3 baseDir, byte team, int attackerId, Collider casterCollider)
        {
            float requiredMana = CastEvaluator.EstimateManaCost(spells, baseDraws, incomingMods);
            if (!TrySpendMana(requiredMana))
            {
                _emits.Clear();
                return 0;
            }

            CastEvaluator.Evaluate(spells, baseDraws, float.PositiveInfinity, incomingMods, _emits);
            _playedSfx.Clear();

            int count = _emits.Count;
            for (int i = 0; i < count; i++)
            {
                EmitCommand cmd = _emits[i];

                if (cmd.CastSfx != null && _playedSfx.Add(cmd.CastSfx))
                    AudioSource.PlayClipAtPoint(cmd.CastSfx, spawnPos);

                if (cmd.ProjectilePrefab == null)
                {
                    GameLog.Warn("EmitCommand.ProjectilePrefab 为空，跳过该法术产出", "Skills");
                    continue;
                }

                switch (cmd.SpawnMode)
                {
                    case SpellSpawnMode.SkyfallAtPoint:
                        SpawnSkyfallProjectile(cmd, spawnPos, baseDir, i, count, team, attackerId, casterCollider);
                        break;

                    case SpellSpawnMode.StaticAtPoint:
                        SpawnStaticProjectileAtPoint(cmd, spawnPos, baseDir, team, attackerId, casterCollider);
                        break;

                    default:
                        SpawnForwardProjectile(cmd, spawnPos, baseDir, i, count, team, attackerId, casterCollider);
                        break;
                }
            }

            return count;
        }

        private bool TrySpendMana(float requiredMana)
        {
            if (requiredMana <= 0f)
                return true;

            if (_mana == null)
            {
                GameLog.Warn("SpellCaster 未配置 ManaComponent，本次施法按无限法力处理", "Skills");
                return true;
            }

            if (!_mana.CanSpend(requiredMana))
            {
                GameLog.Info($"法力不足：需要 {requiredMana:0.#}，当前 {_mana.CurrentMana:0.#}", "Skills");
                return false;
            }

            return _mana.Spend(requiredMana);
        }

        private void SpawnForwardProjectile(EmitCommand cmd, Vector3 spawnPos, Vector3 baseDir, int index, int count,
                                            byte team, int attackerId, Collider casterCollider)
        {
            float yaw = SpellAiming.SpreadOffsetDegrees(index, count, cmd.SpreadDegrees);
            Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * baseDir;

            GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, Quaternion.LookRotation(dir));
            ProjectileBase proj = go.GetComponent<ProjectileBase>();
            if (proj == null)
            {
                GameLog.Warn($"法术预制体 {cmd.ProjectilePrefab.name} 上没有 ProjectileBase 组件", "Skills");
                Object.Destroy(go);
                return;
            }

            WirePayload(proj, cmd, team, attackerId, casterCollider);
            ConfigureProjectileMotion(proj, cmd, index, count);
            if (proj is ShieldProjectile shieldProjectile)
                shieldProjectile.ConfigureShield(cmd.ShieldReflectCount);
            proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, dir * cmd.Speed, casterCollider, useGravity: cmd.UseGravity);
        }

        private void SpawnStaticProjectileAtPoint(EmitCommand cmd, Vector3 spawnPos, Vector3 baseDir,
                                                  byte team, int attackerId, Collider casterCollider)
        {
            Quaternion rotation = cmd.ProjectilePrefab.transform.rotation;
            if (baseDir.sqrMagnitude > 1e-6f)
                rotation = Quaternion.LookRotation(baseDir.normalized) * rotation;

            GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, rotation);
            ProjectileShield shield = go.GetComponent<ProjectileShield>();
            if (shield == null)
                shield = go.GetComponentInChildren<ProjectileShield>();
            if (shield != null)
            {
                shield.Init(team, attackerId, cmd.ShieldReflectCount, casterCollider);
                return;
            }

            GameLog.Warn($"StaticAtPoint prefab {cmd.ProjectilePrefab.name} has no ProjectileShield component", "Skills");
        }

        private void WirePayload(ProjectileBase proj, EmitCommand cmd, byte team, int attackerId, Collider casterCollider)
        {
            if (!cmd.HasPayload)
                return;

            IReadOnlyList<SpellDefinition> payload = cmd.Payload;
            CastModifierState payloadMods = cmd.PayloadMods;

            switch (cmd.PayloadTrigger)
            {
                case PayloadTriggerMode.OnImpact:
                    proj.Impacted += (hitPoint, hitDir) =>
                        RunCast(payload, 1, payloadMods, hitPoint, hitDir, team, attackerId, casterCollider);
                    break;

                case PayloadTriggerMode.AfterDelay:
                    proj.TimedTriggerElapsed += (position, direction) =>
                        RunCast(payload, 1, payloadMods, position, direction, team, attackerId, casterCollider);
                    proj.ArmTimedTrigger(cmd.PayloadDelaySeconds);
                    break;
            }
        }

        private void ConfigureProjectileMotion(ProjectileBase proj, EmitCommand cmd, int index, int count)
        {
            proj.ConfigureBounce(cmd.BounceCount);

            switch (cmd.MotionMode)
            {
                case ProjectileMotionMode.Homing:
                    proj.ConfigureHoming(cmd.HomingRadius, cmd.HomingDuration, cmd.HomingTurnRateDegrees);
                    proj.ConfigureOrbit(0f, 0f, 0f, 0f);
                    break;

                case ProjectileMotionMode.Orbit:
                    proj.ConfigureHoming(0f, 0f, 0f);
                    float orbitPhase = cmd.OrbitPhaseOffsetDegrees + SpellAiming.PhaseOffsetDegrees(index, count);
                    float orbitPlaneTilt = SpellAiming.PlaneTiltDegrees(index, count, cmd.OrbitPlaneTiltDegrees);
                    proj.ConfigureOrbit(cmd.OrbitRadius, cmd.OrbitAngularSpeedDegrees, orbitPhase, orbitPlaneTilt);
                    break;

                default:
                    proj.ConfigureHoming(0f, 0f, 0f);
                    proj.ConfigureOrbit(0f, 0f, 0f, 0f);
                    break;
            }
        }

        private void SpawnSkyfallProjectile(EmitCommand cmd, Vector3 landingPoint, Vector3 baseDir, int index, int count,
                                            byte team, int attackerId, Collider casterCollider)
        {
            landingPoint = ResolveSkyfallLandingPoint(landingPoint, casterCollider);

            if (cmd.LandingSitePrefab != null)
            {
                Quaternion markerRotation = cmd.LandingSitePrefab.transform.rotation;
                GameObject marker = Object.Instantiate(cmd.LandingSitePrefab, landingPoint, markerRotation);
                Object.Destroy(marker, Mathf.Max(0.05f, cmd.LandingSiteDuration + 0.5f));
            }

            if (cmd.LandingSiteDuration > 0f)
            {
                StartCoroutine(SpawnSkyfallProjectileAfterDelay(cmd, landingPoint, baseDir, index, count, team, attackerId, casterCollider));
            }
            else
            {
                SpawnSkyfallProjectileNow(cmd, landingPoint, baseDir, index, count, team, attackerId, casterCollider);
            }
        }

        private IEnumerator SpawnSkyfallProjectileAfterDelay(EmitCommand cmd, Vector3 landingPoint, Vector3 baseDir,
                                                             int index, int count,
                                                             byte team, int attackerId, Collider casterCollider)
        {
            yield return new WaitForSeconds(cmd.LandingSiteDuration);
            SpawnSkyfallProjectileNow(cmd, landingPoint, baseDir, index, count, team, attackerId, casterCollider);
        }

        private void SpawnSkyfallProjectileNow(EmitCommand cmd, Vector3 landingPoint, Vector3 baseDir, int index, int count,
                                               byte team, int attackerId, Collider casterCollider)
        {
            Vector3 horizontalDir = baseDir;
            horizontalDir.y = 0f;
            if (horizontalDir.sqrMagnitude < 1e-6f)
                horizontalDir = transform.forward;
            horizontalDir.Normalize();

            float height = Mathf.Max(0f, cmd.SkyfallHeight);
            Vector3 spawnPos = landingPoint + Vector3.up * height - horizontalDir * cmd.SkyfallBackOffset;
            Vector3 fallDir = landingPoint - spawnPos;
            if (fallDir.sqrMagnitude < 1e-6f)
                fallDir = Vector3.down;
            fallDir.Normalize();

            GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, Quaternion.LookRotation(fallDir));
            ProjectileBase proj = go.GetComponent<ProjectileBase>();
            if (proj == null)
            {
                GameLog.Warn($"静态投射物预制体 {cmd.ProjectilePrefab.name} 上没有 ProjectileBase 组件", "Skills");
                Object.Destroy(go);
                return;
            }

            WirePayload(proj, cmd, team, attackerId, casterCollider);
            ConfigureProjectileMotion(proj, cmd, index, count);
            proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, fallDir * cmd.Speed, casterCollider, useGravity: cmd.UseGravity);
        }

        private Vector3 ResolveSkyfallLandingPoint(Vector3 sourcePoint, Collider casterCollider)
        {
            Vector3 origin = sourcePoint + Vector3.up * 2f;
            int hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, _landingHits, 50f, ~0, QueryTriggerInteraction.Ignore);
            if (hitCount <= 0)
                return sourcePoint;

            float bestDistance = float.MaxValue;
            Vector3 bestPoint = sourcePoint;
            bool found = false;

            for (int i = 0; i < hitCount; i++)
            {
                Collider hitCollider = _landingHits[i].collider;
                if (hitCollider == null || hitCollider == casterCollider)
                    continue;

                if (_landingHits[i].distance >= bestDistance)
                    continue;

                bestDistance = _landingHits[i].distance;
                bestPoint = _landingHits[i].point;
                found = true;
            }

            return found ? bestPoint : sourcePoint;
        }
    }
}

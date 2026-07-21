using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;
using Game.Core;
using Game.Combat;
using Game.Run;
using Game.Skills;

namespace Game.Character
{
    /// <summary>
    /// 法术施放器：把 Game.Skills 的纯求值结果转换成 Unity 运行时对象。
    /// CastEvaluator 只负责解释 wand；这里负责 Instantiate、ProjectileBase.Init 和 payload 递归触发。
    /// </summary>
    public class SpellCaster : MonoBehaviour
    {
        [Header("Run Data Source")]
        [SerializeField]
        [Tooltip("P7 单局模式的数据源；配置后优先使用它的 Runtime Wand，旧场景仍可回退到下方 Wand。")]
        private RunSpellSession _runSpellSession;

        [SerializeField] private WandLoadout _wand;
        [Tooltip("可用法力值")]
        [SerializeField] private ManaComponent _mana;
        [Header("开发诊断")]
        [SerializeField] private CastTraceLevel _traceLevel = CastTraceLevel.Off;

        private static int s_nextCastId;
        private static readonly ProfilerMarker s_runtimeSpawnMarker = new ProfilerMarker("Spell.RuntimeSpawn");
        private readonly List<EmitCommand> _emits = new List<EmitCommand>(16);
        private readonly HashSet<AudioClip> _playedSfx = new HashSet<AudioClip>();
        private readonly RaycastHit[] _landingHits = new RaycastHit[8];
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private readonly CastTraceCollector _traceCollector = new CastTraceCollector(64);
#endif

        /// <summary>
        /// 返回当前真正参与求值的法杖。P7 使用 Runtime Clone，旧实验场景仍兼容 Inspector 中的模板 Wand。
        /// 这里按需解析而不在 Awake 缓存，避免依赖不同 GameObject 之间不可保证的 Awake 执行顺序。
        /// </summary>
        public WandLoadout Wand => ResolveWand();
        public CastTraceLevel TraceLevel
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return _traceLevel;
#else
                return CastTraceLevel.Off;
#endif
            }
        }

        private void Awake()
        {
            if (_mana == null)
                _mana = GetComponent<ManaComponent>();
        }

        /// <summary>运行当前法杖：从 spawnPos 朝 aimPoint 施放。返回本次求值产出数量。</summary>
        public int CastWand(Vector3 spawnPos, Vector3 aimPoint, byte team, int attackerId, Collider casterCollider)
        {
            WandLoadout activeWand = ResolveWand();
            if (activeWand == null || activeWand.Spells == null || activeWand.Spells.Length == 0)
            {
                GameLog.Warn("SpellCaster 未配置 WandLoadout 或法杖为空，无法施放", "Skills");
                return 0;
            }

            Vector3 baseDir = aimPoint - spawnPos;
            if (baseDir.sqrMagnitude < 1e-6f)
                baseDir = transform.forward;
            baseDir.Normalize();

            int castId = ++s_nextCastId;
            return RunCast(activeWand.Spells, activeWand.BaseDraws, CastModifierState.Default,
                           spawnPos, baseDir, team, attackerId, casterCollider, castId, 0);
        }

        private WandLoadout ResolveWand()
        {
            if (_runSpellSession != null &&
                _runSpellSession.IsInitialized &&
                _runSpellSession.RuntimeWand != null)
            {
                return _runSpellSession.RuntimeWand;
            }

            return _wand;
        }

        /// <summary>
        /// 运行一段法术序列。spawnPos 对普通 Emit 是发射点；对 SkyfallAtPoint 是落点。
        /// 触发 payload 时会用命中点作为新的 spawnPos，再以预算 1 运行 payload。
        /// </summary>
        private int RunCast(IReadOnlyList<SpellDefinition> spells, int baseDraws, CastModifierState incomingMods,
                            Vector3 spawnPos, Vector3 baseDir, byte team, int attackerId, Collider casterCollider,
                            int castId, int depth)
        {
            float requiredMana = CastEvaluator.EstimateManaCost(spells, baseDraws, incomingMods);
            float availableMana = _mana != null ? _mana.CurrentMana : float.PositiveInfinity;
            if (!TrySpendMana(requiredMana))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                LogManaPreflightFailure(
                    spells, baseDraws, incomingMods,
                    castId, depth, requiredMana, availableMana);
#endif
                _emits.Clear();
                return 0;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            CastSummary summary;
            if (_traceLevel == CastTraceLevel.Detailed)
            {
                summary = CastEvaluator.EvaluateWithTrace(
                    spells, baseDraws, float.PositiveInfinity,
                    incomingMods, _emits, _traceCollector);
            }
            else
            {
                summary = CastEvaluator.Evaluate(
                    spells, baseDraws, float.PositiveInfinity, incomingMods, _emits);
            }
            LogTrace(castId, depth, requiredMana, summary);
#else
            CastEvaluator.Evaluate(spells, baseDraws, float.PositiveInfinity, incomingMods, _emits);
#endif
            _playedSfx.Clear();

            int count = _emits.Count;
            using (s_runtimeSpawnMarker.Auto())
            {
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
                            SpawnSkyfallProjectile(cmd, spawnPos, baseDir, i, count, team, attackerId, casterCollider, castId, depth);
                            break;

                        case SpellSpawnMode.StaticAtPoint:
                            SpawnStaticProjectileAtPoint(cmd, spawnPos, baseDir, team, attackerId, casterCollider);
                            break;

                        default:
                            SpawnForwardProjectile(cmd, spawnPos, baseDir, i, count, team, attackerId, casterCollider, castId, depth);
                            break;
                    }
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
                                            byte team, int attackerId, Collider casterCollider, int castId, int depth)
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

            WirePayload(proj, cmd, team, attackerId, casterCollider, castId, depth);
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

        private void WirePayload(ProjectileBase proj, EmitCommand cmd, byte team, int attackerId, Collider casterCollider,
                                 int castId, int depth)
        {
            if (!cmd.HasPayload)
                return;

            IReadOnlyList<SpellDefinition> payload = cmd.Payload;
            CastModifierState payloadMods = cmd.PayloadMods;

            switch (cmd.PayloadTrigger)
            {
                case PayloadTriggerMode.OnImpact:
                    proj.Impacted += (hitPoint, hitDir) =>
                        RunCast(payload, 1, payloadMods, hitPoint, hitDir, team, attackerId, casterCollider, castId, depth + 1);
                    break;

                case PayloadTriggerMode.AfterDelay:
                    proj.TimedTriggerElapsed += (position, direction) =>
                        RunCast(payload, 1, payloadMods, position, direction, team, attackerId, casterCollider, castId, depth + 1);
                    proj.ArmTimedTrigger(cmd.PayloadDelaySeconds);
                    break;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void LogManaPreflightFailure(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            CastModifierState incomingMods,
            int castId,
            int depth,
            float requiredMana,
            float availableMana)
        {
            if (_traceLevel == CastTraceLevel.Off)
                return;

            if (_traceLevel == CastTraceLevel.Detailed)
            {
                CastSummary summary = CastEvaluator.EvaluateWithTrace(
                    spells, baseDraws, availableMana,
                    incomingMods, _emits, _traceCollector);
                LogTrace(castId, depth, requiredMana, summary);
                _emits.Clear();
                return;
            }

            GameLog.Info(
                $"Cast #{castId} Depth={depth} PRECHECK FAILED Emits=0 " +
                $"RequiredMana={requiredMana:0.##} AvailableMana={availableMana:0.##} Fizzled=True",
                "SpellTrace");
        }

        private void LogTrace(int castId, int depth, float requiredMana, CastSummary summary)
        {
            if (_traceLevel == CastTraceLevel.Off)
                return;

            GameLog.Info(
                $"Cast #{castId} Depth={depth} Emits={_emits.Count} " +
                $"RequiredMana={requiredMana:0.##} EvaluatedMana={summary.ManaSpent:0.##} " +
                $"Fizzled={summary.Fizzled}",
                "SpellTrace");

            if (_traceLevel != CastTraceLevel.Detailed)
                return;

            for (int i = 0; i < _traceCollector.Count; i++)
                LogTraceStep(castId, depth, _traceCollector[i]);

            if (_traceCollector.ExpandedDuringLastCollection)
            {
                GameLog.Warn(
                    $"Cast #{castId} Trace 超出预分配容量，本次诊断发生 List 扩容",
                    "SpellTrace");
            }
        }

        private static void LogTraceStep(int castId, int depth, CastTraceStep step)
        {
            string spellName = step.Spell != null
                ? step.Spell.DisplayName
                : "<none>";

            switch (step.Kind)
            {
                case CastTraceStepKind.CastStarted:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} START Draw={step.DrawBudgetBefore} " +
                        FormatModifiers(step.ModifiersBefore),
                        "SpellTrace");
                    break;
                case CastTraceStepKind.ModifyApplied:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] {spellName} MODIFY " +
                        $"Before[{FormatModifiers(step.ModifiersBefore)}] " +
                        $"After[{FormatModifiers(step.ModifiersAfter)}]",
                        "SpellTrace");
                    break;
                case CastTraceStepKind.MulticastApplied:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] {spellName} MULTICAST " +
                        $"Draw {step.DrawBudgetBefore}->{step.DrawBudgetAfter}", "SpellTrace");
                    break;
                case CastTraceStepKind.DrawBudgetBlocked:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] {spellName} BLOCKED Draw=0",
                        "SpellTrace");
                    break;
                case CastTraceStepKind.EmitProduced:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] {spellName} EMIT#{step.EmitIndex} " +
                        FormatEmit(step.Emit) + " " +
                        $"Draw {step.DrawBudgetBefore}->{step.DrawBudgetAfter}",
                        "SpellTrace");
                    break;
                case CastTraceStepKind.PayloadCaptured:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] PAYLOAD " +
                        $"Trigger={step.PayloadTrigger} Delay={step.Emit.PayloadDelaySeconds:0.##} " +
                        $"Start={step.PayloadStartIndex} Count={step.PayloadCount}",
                        "SpellTrace");
                    break;
                case CastTraceStepKind.ManaFizzle:
                    GameLog.Info(
                        $"Cast #{castId} D{depth} [{step.SpellIndex}] {spellName} FIZZLE " +
                        $"ManaLeft={step.ManaLeftBefore:0.##}",
                        "SpellTrace");
                    break;
                case CastTraceStepKind.NullSpellSkipped:
                    GameLog.Info($"Cast #{castId} D{depth} [{step.SpellIndex}] NULL SKIPPED", "SpellTrace");
                    break;
                case CastTraceStepKind.CastCompleted:
                    GameLog.Info($"Cast #{castId} D{depth} COMPLETE DrawLeft={step.DrawBudgetAfter}", "SpellTrace");
                    break;
            }
        }

        private static string FormatModifiers(CastModifierState modifiers)
        {
            return $"DamageAddFlat={modifiers.DamageAddFlat:0.##} DamageMul={modifiers.DamageMul:0.##} " +
                   $"SpeedMul={modifiers.SpeedMul:0.##} Spread={modifiers.SpreadDegrees:0.##} " +
                   $"Bounce={modifiers.BounceCount} UseGravity={modifiers.UseGravity} " +
                   $"Homing(R={modifiers.HomingRadius:0.##},Duration={modifiers.HomingDuration:0.##},Turn={modifiers.HomingTurnRateDegrees:0.##}) " +
                   $"Orbit(R={modifiers.OrbitRadius:0.##},Angular={modifiers.OrbitAngularSpeedDegrees:0.##},Phase={modifiers.OrbitPhaseOffsetDegrees:0.##},Tilt={modifiers.OrbitPlaneTiltDegrees:0.##}) " +
                   $"Motion={modifiers.MotionMode}";
        }

        private static string FormatEmit(EmitCommand emit)
        {
            return $"Projectile={(emit.ProjectilePrefab != null ? emit.ProjectilePrefab.name : "<none>")} " +
                   $"LandingSite={(emit.LandingSitePrefab != null ? emit.LandingSitePrefab.name : "<none>")} " +
                   $"CastSfx={(emit.CastSfx != null ? emit.CastSfx.name : "<none>")} " +
                   $"Damage={emit.Damage:0.##} Speed={emit.Speed:0.##} DamageType={emit.DamageType} " +
                   $"Spread={emit.SpreadDegrees:0.##} Bounce={emit.BounceCount} UseGravity={emit.UseGravity} " +
                   $"Homing(R={emit.HomingRadius:0.##},Duration={emit.HomingDuration:0.##},Turn={emit.HomingTurnRateDegrees:0.##}) " +
                   $"Orbit(R={emit.OrbitRadius:0.##},Angular={emit.OrbitAngularSpeedDegrees:0.##},Phase={emit.OrbitPhaseOffsetDegrees:0.##},Tilt={emit.OrbitPlaneTiltDegrees:0.##}) " +
                   $"Motion={emit.MotionMode} SpawnMode={emit.SpawnMode} " +
                   $"Skyfall(Height={emit.SkyfallHeight:0.##},Back={emit.SkyfallBackOffset:0.##},LandingDuration={emit.LandingSiteDuration:0.##}) " +
                   $"ShieldReflect={emit.ShieldReflectCount} " +
                   $"Payload(Has={emit.HasPayload},Count={(emit.Payload != null ? emit.Payload.Count : 0)},Trigger={emit.PayloadTrigger},Delay={emit.PayloadDelaySeconds:0.##}," +
                   $"Mods=[{FormatModifiers(emit.PayloadMods)}])";
        }
#endif

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
                                            byte team, int attackerId, Collider casterCollider, int castId, int depth)
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
                StartCoroutine(SpawnSkyfallProjectileAfterDelay(
                    cmd, landingPoint, baseDir, index, count, team, attackerId, casterCollider, castId, depth));
            }
            else
            {
                SpawnSkyfallProjectileNow(cmd, landingPoint, baseDir, index, count, team, attackerId, casterCollider, castId, depth);
            }
        }

        private IEnumerator SpawnSkyfallProjectileAfterDelay(EmitCommand cmd, Vector3 landingPoint, Vector3 baseDir,
                                                             int index, int count,
                                                             byte team, int attackerId, Collider casterCollider,
                                                             int castId, int depth)
        {
            yield return new WaitForSeconds(cmd.LandingSiteDuration);
            SpawnSkyfallProjectileNow(cmd, landingPoint, baseDir, index, count, team, attackerId, casterCollider, castId, depth);
        }

        private void SpawnSkyfallProjectileNow(EmitCommand cmd, Vector3 landingPoint, Vector3 baseDir, int index, int count,
                                               byte team, int attackerId, Collider casterCollider, int castId, int depth)
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

            WirePayload(proj, cmd, team, attackerId, casterCollider, castId, depth);
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

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
    /// 法术施放器（Runtime Adapter）：把 Game.Skills 的纯数据求值结果转换成 Unity 场景对象。
    /// CastEvaluator 只把有序法术序列解释为 List&lt;EmitCommand&gt;；这里才负责 Instantiate、
    /// ProjectileBase.Init、音效播放以及 Payload 的命中/定时递归，因此纯逻辑层不需要依赖场景和 Physics。
    ///
    /// 可以把完整职责链理解为：
    /// 有序法术配置 → CastEvaluator 纯求值 → EmitCommand 值快照 → SpellCaster 创建/配置对象
    /// → ProjectileBase 接管 Rigidbody、碰撞与伤害入口。
    /// SpellCaster 只连接这些模块，不在这里实现法术排列规则，也不直接修改目标 HP。
    /// </summary>
    public class SpellCaster : MonoBehaviour
    {
        // Runtime Trigger 可能在投射物命中后的未来帧再次调用 RunCast。
        // 这里与纯解释器使用同一深度上限，形成第二道防线，防止错误数据或未来规则扩展造成无界递归。
        private const int MaxRuntimeTriggerDepth = CastEvaluator.MaxActionDepth;

        // Header/Tooltip/SerializeField 都是 Unity 序列化与 Inspector 元数据：
        // 它们让 private 字段可以在 Inspector 中配置，但不会改变字段的 C# 可见性。
        [Header("Run Data Source")]
        [SerializeField]
        [Tooltip("P7 单局模式的数据源；配置后优先使用它的 Runtime Wand，旧场景仍可回退到下方 Wand。")]
        // 单局 Session 持有玩家本局可编辑的 Runtime 配置，避免直接修改 Project 中的模板资产。
        private RunSpellSession _runSpellSession;

        // Inspector 模板/旧实验场景的回退数据源。正式单局中优先读取 _runSpellSession.RuntimeWand。
        [SerializeField] private WandLoadout _wand;
        [Tooltip("可用法力值")]
        // ManaComponent 持有当前角色独立的可变法力值；SpellDefinition/WandLoadout 不保存本次运行资源。
        [SerializeField] private ManaComponent _mana;
        [Header("开发诊断")]
        // 诊断等级只控制 Editor/Development Build 日志，不改变求值、扣费或生成结果。
        [SerializeField] private CastTraceLevel _traceLevel = CastTraceLevel.Off;

        // castId 只用于把同一次施法及其后续 Payload 的诊断日志串起来，不参与玩法计算。
        private static int s_nextCastId;
        // ProfilerMarker 会在 Unity Profiler 中形成可独立观察的采样区间，用于定位“求值”之后的生成成本。
        private static readonly ProfilerMarker s_runtimeSpawnMarker = new ProfilerMarker("Spell.RuntimeSpawn");
        // 复用容器，避免每次施法都 new List/HashSet；Clear 只清元素，通常会保留已扩展的容量。
        private readonly List<EmitCommand> _emits = new List<EmitCommand>(16);
        private readonly HashSet<AudioClip> _playedSfx = new HashSet<AudioClip>();
        // RaycastNonAlloc 由调用方提供数组接收结果，避免每次落点检测创建新数组。
        private readonly RaycastHit[] _landingHits = new RaycastHit[8];
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Trace 仅在开发构建中存在；固定初始 Capacity 让常见施法不必为诊断步骤频繁扩容。
        private readonly CastTraceCollector _traceCollector = new CastTraceCollector(64);
#endif

        /// <summary>
        /// 返回当前真正参与求值的法杖。P7 使用 Runtime Clone，旧实验场景仍兼容 Inspector 中的模板 Wand。
        /// 这里按需解析而不在 Awake 缓存，避免依赖不同 GameObject 之间不可保证的 Awake 执行顺序。
        /// 表达式体属性只把读取转发给 ResolveWand；外部不能通过该属性替换内部数据源。
        /// </summary>
        public WandLoadout Wand => ResolveWand();

        /// <summary>
        /// 对外暴露当前有效的诊断等级。Release Build 直接返回 Off，调用方不能误以为详细 Trace 仍会运行。
        /// </summary>
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
            // Awake 在该 MonoBehaviour 实例启用前由 Unity 调用一次，适合缓存同一 GameObject 上的组件引用。
            // GetComponent 只在当前 GameObject 上查找指定组件；Inspector 未手动绑定时才自动补齐引用。
            if (_mana == null)
                _mana = GetComponent<ManaComponent>();
        }

        /// <summary>
        /// 运行当前有序法术序列：从 spawnPos 朝 aimPoint 施放，返回解释器产出的命令数量。
        /// 这里的“运行”不是逐条直接生成对象，而是先完整求值得到命令，再统一进入 Runtime Spawn 阶段。
        /// </summary>
        /// <param name="spawnPos">本次攻击的世界空间发射点，来自角色身上的发射 Transform。</param>
        /// <param name="aimPoint">玩家按下攻击时锁定的世界空间目标点，不是一个方向向量。</param>
        /// <param name="team">攻击者阵营快照，供投射物过滤自身和友方。</param>
        /// <param name="attackerId">攻击者实例 Id，这次伤害归属于哪个 Runtime 对象，用于伤害归属和事件过滤。</param>
        /// <param name="casterCollider">施法者碰撞体；投射物出生后应该忽略施法者 Collider，防止出生即撞到自己。</param>
        /// <returns>解释器产出的 EmitCommand 数量；它不等同于最终成功 Instantiate 的对象数量。</returns>
        public int CastWand(Vector3 spawnPos, Vector3 aimPoint, byte team, int attackerId, Collider casterCollider)
        {
            // ResolveWand 隔离“当前配置来自单局 Runtime Clone 还是 Inspector 模板”的差异，后续流程只认结果。
            WandLoadout activeWand = ResolveWand();
            if (activeWand == null || activeWand.Spells == null || activeWand.Spells.Length == 0)
            {
                GameLog.Warn("SpellCaster 未配置 WandLoadout 或法杖为空，无法施放", "Skills");
                return 0;
            }

            // 目标点减发射点得到朝向向量；sqrMagnitude 不开平方，比 magnitude 更适合只做“是否接近零”的比较。
            Vector3 baseDir = aimPoint - spawnPos;
            if (baseDir.sqrMagnitude < 1e-6f) baseDir = transform.forward;
            baseDir.Normalize();    // Normalize 只保留方向、把长度变成 1，之后再乘速度即可得到初始 velocity。

            // 前置递增让 0 保留为“未分配”语义；该 Id 只串联日志，不参与 Gameplay 身份判断。
            int castId = ++s_nextCastId;
            return RunCast(activeWand.Spells, activeWand.BaseDraws, CastModifierState.Default,
                           spawnPos, baseDir, team, attackerId, casterCollider, castId, 0,
                           SpellManaPolicy.SpendCasterMana);
        }

        /// <summary>
        /// 运行调用方显式提供的 Wand Program。Boss 使用 IgnoreMana，但仍复用同一个
        /// CastEvaluator、EmitCommand、Projectile 与 Payload 递归链路。
        /// </summary>
        /// <remarks>
        /// 与 CastWand 的区别只在“配置来源”和 Mana Policy；方向计算、求值、生成与 Payload 行为完全复用 RunCast。
        /// </remarks>
        public int CastProgram(
            WandLoadout program,
            Vector3 spawnPos,
            Vector3 aimPoint,
            byte team,
            int attackerId,
            Collider casterCollider,
            SpellManaPolicy manaPolicy)
        {
            if (program == null ||
                program.Spells == null ||
                program.Spells.Length == 0)
            {
                GameLog.Warn(
                    "SpellCaster 收到空的显式 Wand Program，无法施放",
                    "Skills");
                return 0;
            }

            // CastProgram 也接收“点”而不是“方向”，因此必须执行与玩家入口相同的世界空间方向换算。
            Vector3 baseDir = aimPoint - spawnPos;
            if (baseDir.sqrMagnitude < 1e-6f)
                baseDir = transform.forward;
            baseDir.Normalize();

            int castId = ++s_nextCastId;
            return RunCast(
                program.Spells,
                program.BaseDraws,
                CastModifierState.Default,
                spawnPos,
                baseDir,
                team,
                attackerId,
                casterCollider,
                castId,
                0,
                manaPolicy);
        }

        /// <summary>
        /// 解析当前权威的有序法术配置：优先使用单局 Runtime Clone，无法绑定时才回退到 Inspector 模板。
        /// 返回的是 ScriptableObject 引用，不会在这里复制数组或重新创建资产。
        /// </summary>
        private WandLoadout ResolveWand()
        {
            if (TryBindCurrentRunSession())
                return _runSpellSession.RuntimeWand;

            return _wand;
        }

        /// <summary>
        /// 跨 Scene 后重新绑定由 DontDestroyOnLoad 保留的单局数据。
        /// Scene Asset 不能序列化指向运行时对象的引用，因此后续关卡必须通过 Current 找回同一局 Session。
        /// </summary>
        /// <returns>找到且已初始化、同时拥有 RuntimeWand 的 Session 时返回 true。</returns>
        public bool TryBindCurrentRunSession()
        {
            // 已持有可用引用时直接复用，避免每次 Cast 都重新访问静态 Current。
            if (IsUsableRunSession(_runSpellSession))
                return true;

            // Current 是跨 Scene 的 Runtime 入口；它不是 Project Asset 中可序列化的对象引用。
            RunSpellSession current = RunSpellSession.Current;
            if (!IsUsableRunSession(current))
                return false;

            _runSpellSession = current;
            return true;
        }

        /// <summary>
        /// 集中定义 Session 可用条件，避免 Resolve、重绑和将来的调用方分别维护不一致的 null/初始化判断。
        /// </summary>
        private static bool IsUsableRunSession(RunSpellSession session)
        {
            return session != null &&
                   session.IsInitialized &&
                   session.RuntimeWand != null;
        }

        /// <summary>
        /// SpellCaster 的核心管线：先对一层法术 Action 做 Mana 预检和纯求值，再把输出命令翻译成 Unity 对象。
        /// spells 是只读访问接口；baseDraws 是本层初始产出预算；incomingMods 是进入本层前已经形成的修正快照。
        /// spawnPos 对普通 Emit 是发射点，对 SkyfallAtPoint 是目标落点；Trigger 会把命中点/定时点作为下一层 spawnPos。
        /// team、attackerId、casterCollider 是攻击归属与碰撞过滤上下文；castId/depth 只服务诊断和递归护栏。
        /// manaPolicy 决定是否操作玩家的 ManaComponent，但不会改变解释器的法术排列语义。
        /// </summary>
        private int RunCast(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            CastModifierState incomingMods,
            Vector3 spawnPos,
            Vector3 baseDir,
            byte team,
            int attackerId,
            Collider casterCollider,
            int castId,
            int depth,
            SpellManaPolicy manaPolicy)
        {
            if (depth >= MaxRuntimeTriggerDepth)
            {
                // 每层 Payload 都是父序列后面的一个更短 Action 切片，但运行时仍设置硬上限：
                // 即使未来 Authoring 语义扩展，也不会让命中回调形成无界递归。
                GameLog.Warn(
                    $"Cast #{castId} 的 Trigger 深度达到 {MaxRuntimeTriggerDepth}，已停止继续展开 Payload。",
                    "Skills");
                return 0;
            }

            // EstimateManaCost 镜像解释器的读取边界，但不 Instantiate、也不修改 ManaComponent。
            // 当前层采用“全有或全无”的整笔支付：资源不足时不生成任何对象，避免只释放出一半组合。
            // Trigger 捕获的 Payload 属于未来的下一层，此时不预付；真正触发时会再次进入 RunCast 并单独扣费。
            float requiredMana = CastEvaluator.EstimateManaCost(spells, baseDraws, incomingMods);
            // availableMana 供失败 Trace 记录现场；没有 ManaComponent 时与 TrySpendMana 的“无限法力回退”语义一致。
            float availableMana = _mana != null ? _mana.CurrentMana : float.PositiveInfinity;
            if (!TrySpendMana(requiredMana, manaPolicy))
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
            // 条件编译块只进入 Unity Editor 或 Development Build；Release 不包含详细 Trace 的调用和字符串成本。
            CastSummary summary;
            if (_traceLevel == CastTraceLevel.Detailed)
            {
                summary = CastEvaluator.EvaluateWithTrace(
                    spells,
                    baseDraws,
                    float.PositiveInfinity,
                    incomingMods,
                    _emits, _traceCollector);
            }
            else
            {
                summary = CastEvaluator.Evaluate(
                    spells,
                    baseDraws,
                    float.PositiveInfinity,
                    incomingMods,
                    _emits);
            }
            // Summary/Detailed 最终走同一 Gameplay 求值器；Detailed 只额外记录每条指令前后的状态快照。
            LogTrace(castId, depth, requiredMana, summary);
#else
            // 法力已经由上面的 preflight 统一支付，因此解释器收到正无穷，这里只负责确定语义与产出。
            CastEvaluator.Evaluate(spells, baseDraws, float.PositiveInfinity, incomingMods, _emits);
#endif
            // 音效去重的作用域是“当前求值层”。未来 Payload 触发会作为新层重新 Clear，可以播放自己的施法音效。
            _playedSfx.Clear();

            // 先保存 Count，明确本轮只消费当前 Evaluate 的结果；事件回调要到未来命中/计时帧才可能再次复用 _emits。
            int count = _emits.Count;
            // Auto() 返回一个可释放的采样作用域；离开 using 块时自动结束 Profiler 采样。
            using (s_runtimeSpawnMarker.Auto())
            {
                for (int i = 0; i < count; i++)
                {
                    EmitCommand cmd = _emits[i];

                    // HashSet.Add 只有首次加入时返回 true，避免同一次 Multicast 重复播放同一个 Cast SFX。
                    // PlayClipAtPoint 会在指定世界坐标创建临时 AudioSource，播放结束后由 Unity 清理。
                    if (cmd.CastSfx != null && _playedSfx.Add(cmd.CastSfx))
                        AudioSource.PlayClipAtPoint(cmd.CastSfx, spawnPos);

                    if (cmd.ProjectilePrefab == null)
                    {
                        GameLog.Warn("EmitCommand.ProjectilePrefab 为空，跳过该法术产出", "Skills");
                        continue;
                    }

                    // 求值层只输出 SpawnMode；Runtime Adapter 再把不同模式翻译成具体场景行为。
                    switch (cmd.SpawnMode)
                    {
                        case SpellSpawnMode.SkyfallAtPoint:
                            SpawnSkyfallProjectile(
                                cmd, spawnPos, baseDir, i, count, team, attackerId,
                                casterCollider, castId, depth, manaPolicy);
                            break;

                        case SpellSpawnMode.StaticAtPoint:
                            SpawnStaticProjectileAtPoint(cmd, spawnPos, baseDir, team, attackerId, casterCollider);
                            break;

                        default:
                            SpawnForwardProjectile(
                                cmd, spawnPos, baseDir, i, count, team, attackerId,
                                casterCollider, castId, depth, manaPolicy);
                            break;
                    }
                }
            }

            // 返回的是求值命令数。若某条命令 Prefab 缺失或组件不合法，实际生成数可能更少。
            return count;
        }

        private bool TrySpendMana(
            float requiredMana,
            SpellManaPolicy manaPolicy)
        {
            if (manaPolicy == SpellManaPolicy.IgnoreMana)
                return true;

            if (requiredMana <= 0f)
                return true;

            if (_mana == null)
            {
                GameLog.Warn("SpellCaster 未配置 ManaComponent，本次施法按无限法力处理", "Skills");
                return true;
            }

            // CanSpend 是无副作用查询，Spend 才真正修改 Runtime State；二者都采用整笔支付语义。
            if (!_mana.CanSpend(requiredMana))
            {
                GameLog.Info($"法力不足：需要 {requiredMana:0.#}，当前 {_mana.CurrentMana:0.#}", "Skills");
                return false;
            }

            return _mana.Spend(requiredMana);
        }

        /// <summary>
        /// 把一条 ForwardProjectile 命令落地为真实 Prefab，并在启动 Rigidbody 生命周期前注入全部快照配置。
        /// index/count 用于同一层多个产出的散射、轨道相位等确定性排布。
        /// </summary>
        private void SpawnForwardProjectile(
            EmitCommand cmd,
            Vector3 spawnPos,
            Vector3 baseDir,
            int index,
            int count,
            byte team,
            int attackerId,
            Collider casterCollider,
            int castId,
            int depth,
            SpellManaPolicy manaPolicy)
        {
            // AngleAxis 创建“绕世界 Y 轴旋转 yaw 度”的 Quaternion；乘以向量后得到该发射物的扇形方向。
            float yaw = SpellAiming.SpreadOffsetDegrees(index, count, cmd.SpreadDegrees);
            Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * baseDir;

            // Instantiate 根据 Prefab 在运行时创建一个独立 GameObject；LookRotation 让其 forward 朝向发射方向。
            GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, Quaternion.LookRotation(dir));
            // GetComponent 只查 Prefab 根节点；投射物契约要求 ProjectileBase 必须位于根节点。
            ProjectileBase proj = go.GetComponent<ProjectileBase>();
            if (proj == null)
            {
                GameLog.Warn($"法术预制体 {cmd.ProjectilePrefab.name} 上没有 ProjectileBase 组件", "Skills");
                // Destroy 在本帧结束前安排销毁，避免无运行脚本的错误对象残留在场景中。
                Object.Destroy(go);
                return;
            }

            // 顺序上先注入 Payload/运动/复合伤害配置，最后 Init：Init 会真正写入 Rigidbody 初速度并开始生命周期。
            WirePayload(
                proj,
                cmd,
                team,
                attackerId,
                casterCollider,
                castId,
                depth,
                manaPolicy);
            ConfigureCompositeDamage(proj, in cmd);
            ConfigureProjectileMotion(proj, cmd, index, count);
            // ShieldProjectile 是仍沿 ProjectileBase 飞行链路工作的特殊产出，反射次数同样来自本次 EmitCommand 快照。
            if (proj is ShieldProjectile shieldProjectile)
                shieldProjectile.ConfigureShield(cmd.ShieldReflectCount);

            // Init 是投射物生命周期的统一启动点：保存伤害/阵营，忽略施法者碰撞，并写入 Rigidbody.linearVelocity。
            // dir 是单位方向，乘以 cmd.Speed 后才得到带“米/秒”量纲的速度向量。
            proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, dir * cmd.Speed, casterCollider, useGravity: cmd.UseGravity);
        }

        /// <summary>
        /// 在给定世界坐标创建不依靠 Rigidbody 飞行的静态攻击对象。当前 StaticAtPoint Runtime 合同由 ProjectileShield 实现。
        /// </summary>
        private void SpawnStaticProjectileAtPoint(EmitCommand cmd, Vector3 spawnPos, Vector3 baseDir,
                                                  byte team, int attackerId, Collider casterCollider)
        {
            // 先保留 Prefab 作者设置的基础旋转，再把它组合到瞄准方向旋转后，避免模型本地前轴补偿丢失。
            Quaternion rotation = cmd.ProjectilePrefab.transform.rotation;
            if (baseDir.sqrMagnitude > 1e-6f)
                rotation = Quaternion.LookRotation(baseDir.normalized) * rotation;

            // 静态对象依然通过 Instantiate 创建独立场景实例，但不会调用 ProjectileBase.Init。
            GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, rotation);
            ProjectileShield shield = go.GetComponent<ProjectileShield>();
            if (shield == null)
                // GetComponentInChildren 会继续检查子节点，兼容碰撞/表现组件不在 Prefab 根节点的静态对象。
                shield = go.GetComponentInChildren<ProjectileShield>();
            if (shield != null)
            {
                // 静态护盾自己持有阵营、归属、剩余反射次数与 Collider 忽略规则，因此走专用 Init。
                shield.Init(team, attackerId, cmd.ShieldReflectCount, casterCollider);
                return;
            }

            // 只报告 Authoring 错误；这里不猜测未知 StaticAtPoint Prefab 应该由哪个组件接管。
            GameLog.Warn($"StaticAtPoint prefab {cmd.ProjectilePrefab.name} has no ProjectileShield component", "Skills");
        }

        /// <summary>
        /// 把 EmitCommand 中的 Payload 数据与某一个 ProjectileBase 实例的生命周期事件连接起来。
        /// 这里使用实例 C# event，而不是全局 EventBus：回调只属于这枚投射物，不需要额外用 Projectile Id 做匹配。
        /// </summary>
        private void WirePayload(
            ProjectileBase proj,
            EmitCommand cmd,
            byte team,
            int attackerId,
            Collider casterCollider,
            int castId,
            int depth,
            SpellManaPolicy manaPolicy)
        {
            // HasPayload 同时检查引用和元素数量；空载荷不订阅事件，也不会建立无意义的闭包。
            if (!cmd.HasPayload)
                return;

            // 把当前命令捕获的“后续一个完整 Action”与 Modifier 快照保存到闭包中。
            // 投射物未来命中或计时结束时，回调仍能用本次施法的上下文运行下一层，而不再读取已变化的序列。
            // 闭包会随 delegate 一起被投射物事件持有；该分配发生在离散施法，不位于每帧 Hot Path。
            IReadOnlyList<SpellDefinition> payload = cmd.Payload;
            CastModifierState payloadMods = cmd.PayloadMods;

            switch (cmd.PayloadTrigger)
            {
                case PayloadTriggerMode.OnImpact:
                    // += 向 C# event 订阅回调；ProjectileBase 在完成碰撞处理后 Invoke，命中点成为下一层发射点。
                    // Payload 表示一个 Action，因此新层的基础 Draw Budget 重置为 1；depth + 1 记录递归层级。
                    proj.Impacted += (hitPoint, hitDir) =>
                        RunCast(
                            payload,
                            1,
                            payloadMods,
                            hitPoint,
                            hitDir,
                            team,
                            attackerId,
                            casterCollider,
                            castId,
                            depth + 1,
                            manaPolicy);
                    break;

                case PayloadTriggerMode.AfterDelay:
                    // 定时模式订阅另一个实例事件，再由 ArmTimedTrigger 开始投射物内部倒计时。
                    // position/direction 取触发瞬间的投射物状态，不重新读取角色或 Camera，因此转镜头不会改变后续方向。
                    proj.TimedTriggerElapsed += (position, direction) =>
                        RunCast(
                            payload,
                            1,
                            payloadMods,
                            position,
                            direction,
                            team,
                            attackerId,
                            casterCollider,
                            castId,
                            depth + 1,
                            manaPolicy);
                    proj.ArmTimedTrigger(cmd.PayloadDelaySeconds);
                    break;
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Mana 预检失败时补充诊断证据。Detailed 会用真实可用 Mana 再跑一次纯求值，定位具体在哪条指令 Fizzle；
        /// 这次求值只写入诊断 List，不会扣第二次 Mana，也不会生成场景对象。
        /// </summary>
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
                // EvaluateWithTrace 会把失败前的临时 Emit 写入复用 List；清空它，保证失败施法不被后续逻辑误消费。
                _emits.Clear();
                return;
            }

            GameLog.Info(
                $"Cast #{castId} Depth={depth} PRECHECK FAILED Emits=0 " +
                $"RequiredMana={requiredMana:0.##} AvailableMana={availableMana:0.##} Fizzled=True",
                "SpellTrace");
        }

        /// <summary>
        /// 输出一层施法的 Summary；Detailed 模式再按 CastTraceCollector 的发生顺序展开每一步。
        /// GameLog 调用只在 Editor/Development Build 有效，这整个方法在 Release 也不会参与编译。
        /// </summary>
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

        /// <summary>把不可变 CastTraceStep 快照翻译成人能阅读的日志，不反向改变解释器状态。</summary>
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

        // 字符串插值会产生诊断字符串分配，所以格式化函数严格位于开发条件编译块内。
        private static string FormatModifiers(CastModifierState modifiers)
        {
            return $"DamageAddFlat={modifiers.DamageAddFlat:0.##} DamageMul={modifiers.DamageMul:0.##} " +
                   $"SpeedMul={modifiers.SpeedMul:0.##} Spread={modifiers.SpreadDegrees:0.##} " +
                   $"Bounce={modifiers.BounceCount} UseGravity={modifiers.UseGravity} " +
                   $"Homing(R={modifiers.HomingRadius:0.##},Duration={modifiers.HomingDuration:0.##},Turn={modifiers.HomingTurnRateDegrees:0.##}) " +
                   $"Orbit(R={modifiers.OrbitRadius:0.##},Angular={modifiers.OrbitAngularSpeedDegrees:0.##},Phase={modifiers.OrbitPhaseOffsetDegrees:0.##},Tilt={modifiers.OrbitPlaneTiltDegrees:0.##}) " +
                   $"Motion={modifiers.MotionMode}";
        }

        /// <summary>把一条已经烘焙完成的 EmitCommand 展开为日志，便于核对 Authoring Data 最终合成了什么。</summary>
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

        /// <summary>
        /// 把命令中的运动快照写入投射物。Bounce 可与主运动并存；Homing 与 Orbit 都会接管主要方向，因此互斥。
        /// 每个分支都会显式关闭另一模式，避免 Prefab 上的序列化默认值污染本次命令语义。
        /// </summary>
        private void ConfigureProjectileMotion(ProjectileBase proj, EmitCommand cmd, int index, int count)
        {
            // 所有运动参数都是 CastEvaluator 已经烘焙好的快照；投射物不再回查 SpellDefinition。
            proj.ConfigureBounce(cmd.BounceCount);

            switch (cmd.MotionMode)
            {
                case ProjectileMotionMode.Homing:
                    proj.ConfigureHoming(cmd.HomingRadius, cmd.HomingDuration, cmd.HomingTurnRateDegrees);
                    proj.ConfigureOrbit(0f, 0f, 0f, 0f);
                    break;

                case ProjectileMotionMode.Orbit:
                    proj.ConfigureHoming(0f, 0f, 0f);
                    // 同层多个投射物均匀错开轨道初相位，并让轨道平面倾角正负交替，避免完全重叠。
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

        /// <summary>
        /// 执行 SkyfallAtPoint 的“两阶段生成”：先解析真实落点并显示预警，再按配置延迟从上空生成投射物。
        /// landingPoint 是目标位置而不是实际出生位置，真正 spawnPos 在 SpawnSkyfallProjectileNow 中计算。
        /// </summary>
        private void SpawnSkyfallProjectile(EmitCommand cmd, Vector3 landingPoint, Vector3 baseDir, int index, int count,
                                            byte team, int attackerId, Collider casterCollider, int castId, int depth,
                                            SpellManaPolicy manaPolicy)
        {
            // 屏幕瞄准点可能位于空中，先向下做 Physics 查询，把它吸附到最近的真实碰撞表面。
            landingPoint = ResolveSkyfallLandingPoint(landingPoint, casterCollider);

            if (cmd.LandingSitePrefab != null)
            {
                // 预警 Prefab 使用作者保存的旋转，不强行朝向施法方向，避免地面贴花被竖起来。
                Quaternion markerRotation = cmd.LandingSitePrefab.transform.rotation;
                GameObject marker = Object.Instantiate(cmd.LandingSitePrefab, landingPoint, markerRotation);
                // Destroy(Object, delay) 延迟销毁落点提示；额外 0.5 秒给结束动画留出时间。
                Object.Destroy(marker, Mathf.Max(0.05f, cmd.LandingSiteDuration + 0.5f));
            }

            if (cmd.LandingSiteDuration > 0f)
            {
                // Coroutine 可跨多帧暂停并恢复该流程，不会阻塞主线程；StartCoroutine 负责交给 Unity 调度。
                StartCoroutine(SpawnSkyfallProjectileAfterDelay(
                    cmd, landingPoint, baseDir, index, count, team, attackerId,
                    casterCollider, castId, depth, manaPolicy));
            }
            else
            {
                SpawnSkyfallProjectileNow(
                    cmd, landingPoint, baseDir, index, count, team, attackerId,
                    casterCollider, castId, depth, manaPolicy);
            }
        }

        /// <summary>
        /// 把解释器生成的复合伤害纯数据交给具体 Combat Runtime。
        /// SpellCaster 只做 Assembly 边界上的 Adapter，不在这里查询范围或直接扣血。
        /// in 以只读引用传递较大的 EmitCommand struct，避免复制并禁止方法修改调用方快照。
        /// </summary>
        private static void ConfigureCompositeDamage(
            ProjectileBase projectile,
            in EmitCommand command)
        {
            if (projectile is NovaFireball meteor)
            {
                meteor.ConfigureImpactDamage(
                    command.ExplosionDamage,
                    command.FireFieldDamagePerTick,
                    command.FireFieldTickInterval,
                    command.FireFieldDuration);
            }
        }

        /// <summary>
        /// Skyfall 预警阶段的 Coroutine。IEnumerator 由 Unity 保存并跨帧恢复；这里只在一次离散施法时创建，
        /// 不属于 Update/FixedUpdate 的逐帧分配 Hot Path。
        /// </summary>
        private IEnumerator SpawnSkyfallProjectileAfterDelay(EmitCommand cmd, Vector3 landingPoint, Vector3 baseDir,
                                                             int index, int count,
                                                             byte team, int attackerId, Collider casterCollider,
                                                             int castId, int depth,
                                                             SpellManaPolicy manaPolicy)
        {
            // WaitForSeconds 使用受 Time.timeScale 影响的游戏时间；yield 时当前方法暂停，但其他 Update/渲染继续运行。
            yield return new WaitForSeconds(cmd.LandingSiteDuration);
            SpawnSkyfallProjectileNow(
                cmd, landingPoint, baseDir, index, count, team, attackerId,
                casterCollider, castId, depth, manaPolicy);
        }

        /// <summary>
        /// 预警结束后计算天空出生点与实际下落方向，并复用普通投射物相同的 Payload、运动、伤害和 Init 链路。
        /// </summary>
        private void SpawnSkyfallProjectileNow(EmitCommand cmd, Vector3 landingPoint, Vector3 baseDir, int index, int count,
                                               byte team, int attackerId, Collider casterCollider, int castId, int depth,
                                               SpellManaPolicy manaPolicy)
        {
            // 去掉 Y 分量得到地面平面上的瞄准方向，用它把天空生成点向后偏移。
            Vector3 horizontalDir = baseDir;
            horizontalDir.y = 0f;
            if (horizontalDir.sqrMagnitude < 1e-6f)
                horizontalDir = transform.forward;
            horizontalDir.Normalize();

            float height = Mathf.Max(0f, cmd.SkyfallHeight);
            Vector3 spawnPos = landingPoint + Vector3.up * height - horizontalDir * cmd.SkyfallBackOffset;
            // 从生成点指向落点，得到真正的下落方向，而不是简单使用 Vector3.down。
            Vector3 fallDir = landingPoint - spawnPos;
            if (fallDir.sqrMagnitude < 1e-6f)
                fallDir = Vector3.down;
            fallDir.Normalize();

            // LookRotation 让 Prefab 的 forward 指向“出生点 → 落点”，后续 Rigidbody 初速度沿同一方向。
            GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, Quaternion.LookRotation(fallDir));
            ProjectileBase proj = go.GetComponent<ProjectileBase>();
            if (proj == null)
            {
                GameLog.Warn($"静态投射物预制体 {cmd.ProjectilePrefab.name} 上没有 ProjectileBase 组件", "Skills");
                Object.Destroy(go);
                return;
            }

            WirePayload(
                proj,
                cmd,
                team,
                attackerId,
                casterCollider,
                castId,
                depth,
                manaPolicy);
            ConfigureCompositeDamage(proj, in cmd);
            ConfigureProjectileMotion(proj, cmd, index, count);
            proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, fallDir * cmd.Speed, casterCollider, useGravity: cmd.UseGravity);
        }

        /// <summary>
        /// 把屏幕射线得到的候选世界点向下投影到最近实体表面。
        /// 查询失败时保留 sourcePoint，使调用方仍有确定的降级位置，而不是返回 Vector3.zero。
        /// </summary>
        private Vector3 ResolveSkyfallLandingPoint(Vector3 sourcePoint, Collider casterCollider)
        {
            Vector3 origin = sourcePoint + Vector3.up * 2f;
            // RaycastNonAlloc 沿 Vector3.down 最多检测 50 米，把命中写入容量为 8 的复用数组；~0 表示所有 Layer。
            // 若同一条射线超过 8 个命中，只能检查数组实际接收到的结果；这是用固定内存换取可控 GC 的取舍。
            // QueryTriggerInteraction.Ignore 排除 Trigger，避免把仅用于范围检测的体积误当作地面。
            int hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, _landingHits, 50f, ~0, QueryTriggerInteraction.Ignore);
            if (hitCount <= 0)
                return sourcePoint;

            float bestDistance = float.MaxValue;
            Vector3 bestPoint = sourcePoint;
            bool found = false;

            // NonAlloc 版本不承诺按距离排序，因此手动选择最近且不是施法者自己的命中点。
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

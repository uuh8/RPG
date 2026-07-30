using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 当前应用进程内唯一的跨 Scene Run Session Root。
    /// 这里只持有轻量数据与 Runtime ScriptableObject Clone；Player、Boss、UI 和 ElementWorld
    /// 必须保持 Scene-local，才能通过 Scene Reload 得到可靠、低耦合的重置行为。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RunSpellSession))]
    public sealed class RunSessionCoordinator : MonoBehaviour
    {
        private static RunSessionCoordinator s_current;

        [SerializeField]
        [Tooltip("与 Coordinator 一同跨 Scene 保留的 Runtime Spell State Owner。")]
        private RunSpellSession _spellSession;

        private BossCheckpointSnapshot _bossCheckpoint;

        public static RunSessionCoordinator Current => s_current;
        public RunStage Stage { get; private set; } = RunStage.MapOne;
        public RunEntryMode EntryMode { get; private set; } = RunEntryMode.NewRun;
        public bool HasBossCheckpoint => _bossCheckpoint.IsValid;
        public bool HasShownGameplayGuide { get; private set; }

        private void Awake()
        {
            if (s_current != null && !ReferenceEquals(s_current, this))
            {
                // 每张 Scene 都可能误放一份 Session Root；后出现的实例必须自毁，
                // 不能覆盖仍持有 Runtime Wand 与 Checkpoint 的权威实例。
                GameLog.Warn(
                    $"检测到重复 RunSessionCoordinator '{name}'，已保留现有 Session Root。",
                    "Run");
                Destroy(gameObject);
                return;
            }

            s_current = this;
            ResolveSpellSession();

            // Unity 只允许 Scene Root 进入 DontDestroyOnLoad Scene。Authoring 时即使误把
            // Session Root 放进 _Gameplay，也先主动解除父子关系，避免切场景后 Runtime Wand
            // 与 Coordinator 一起被卸载。世界 Transform 对纯数据 Root 没有业务含义。
            if (transform.parent != null)
            {
                transform.SetParent(null, true);
            }

            DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// 开始新 Run：明确丢弃上一局的 Runtime 修改和 Boss Checkpoint。
        /// 普通 Scene Load 不调用该方法，因此不会意外重置跨 Scene 数据。
        /// </summary>
        public void BeginNewRun()
        {
            ResolveSpellSession().ResetFromTemplates();
            _bossCheckpoint = default;
            Stage = RunStage.MapOne;
            EntryMode = RunEntryMode.NewRun;
            HasShownGameplayGuide = false;
        }

        public void PrepareBossFieldEntry()
        {
            EnsureSpellSessionReady();
            Stage = RunStage.BossField;
            EntryMode = RunEntryMode.EnterBossField;
        }

        public bool CaptureBossCheckpoint()
        {
            RunSpellSession session = ResolveSpellSession();
            if (!session.IsInitialized || session.RuntimeWand == null)
            {
                return false;
            }

            _bossCheckpoint = new BossCheckpointSnapshot(
                session.RuntimeWand.Spells ?? System.Array.Empty<Game.Skills.SpellDefinition>(),
                session.RuntimeWand.BaseDraws);
            return true;
        }

        public bool RestoreBossCheckpoint()
        {
            if (!_bossCheckpoint.IsValid)
            {
                return false;
            }

            RunSpellSession session = ResolveSpellSession();
            if (!session.IsInitialized)
            {
                return false;
            }

            if (!session.RestoreBossCheckpoint(in _bossCheckpoint))
            {
                return false;
            }

            Stage = RunStage.BossField;
            EntryMode = RunEntryMode.RetryBoss;
            return true;
        }

        /// <summary>
        /// 结束当前 Run，但保留空的 Coordinator 组件供 Main Menu 后续启动新局。
        /// Runtime Clone 与静态 RunSpellSession.Current 会立即失效，不能成为 stale reference。
        /// </summary>
        public void ClearSession()
        {
            RunSpellSession session = ResolveSpellSession();
            session.ClearRuntime();
            _bossCheckpoint = default;
            Stage = RunStage.MapOne;
            EntryMode = RunEntryMode.NewRun;
            HasShownGameplayGuide = false;
        }

        /// <summary>
        /// 原子地完成“是否第一次展示”的检查与标记，避免同一 Scene 误放两个 Controller 时重复弹窗。
        /// 返回 true 的调用者获得本次 Run 的展示权。
        /// </summary>
        public bool TryMarkGameplayGuideShown()
        {
            if (HasShownGameplayGuide)
            {
                return false;
            }

            HasShownGameplayGuide = true;
            return true;
        }

        /// <summary>
        /// 彻底结束当前 Run，并移除跨 Scene Authority。
        /// Restart/Main Menu 不能只 Clear Runtime 后保留空 Root，否则 P7 新 Scene
        /// 中配置完整的 Session Root 会被重复单例 Gate 销毁，下一局反而没有有效模板。
        /// </summary>
        public bool EndRun()
        {
            if (!ReferenceEquals(s_current, this))
            {
                return false;
            }

            ClearSession();
            if (Application.isPlaying)
            {
                Destroy(gameObject);
            }
            else
            {
                DestroyImmediate(gameObject);
            }

            return true;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(s_current, this))
            {
                s_current = null;
            }
        }

        private RunSpellSession ResolveSpellSession()
        {
            if (_spellSession == null)
            {
                _spellSession = GetComponent<RunSpellSession>();
            }

            if (_spellSession == null)
            {
                throw new MissingComponentException(
                    "RunSessionCoordinator requires RunSpellSession on the same GameObject.");
            }

            return _spellSession;
        }

        private void EnsureSpellSessionReady()
        {
            if (!ResolveSpellSession().IsInitialized)
            {
                throw new System.InvalidOperationException(
                    "RunSpellSession must be initialized before changing Run Stage.");
            }
        }
    }
}

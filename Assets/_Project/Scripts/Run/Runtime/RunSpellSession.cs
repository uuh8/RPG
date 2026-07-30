using System;
using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 管理“一局游戏”拥有的法术库与法杖数据。
    /// Project 中的 SpellLibrary / WandLoadout 是只读 Authoring Template；本组件在运行时克隆它们，
    /// 从而让奖励拾取和法杖编辑只影响当前这一局，重开 Scene 后自然恢复模板初始值。
    /// </summary>
    public sealed class RunSpellSession : MonoBehaviour
    {
        [Header("Authoring Templates (Read Only)")]
        [SerializeField]
        [Tooltip("本局初始可用法术。运行时不会直接修改该 Project Asset。")]
        private SpellLibrary _startingLibrary;

        [SerializeField]
        [Tooltip("本局初始法杖。运行时不会直接修改该 Project Asset。")]
        private WandLoadout _startingWand;

        [SerializeField]
        [Tooltip("进入地图二前必须补齐的完整法术列表。该 Project Asset 只作为只读 Authoring Template。")]
        private SpellLibrary _fullLibrary;

        public static RunSpellSession Current { get; private set; }

        public SpellLibrary RuntimeLibrary { get; private set; }
        public WandLoadout RuntimeWand { get; private set; }
        public bool IsInitialized { get; private set; }
        public SpellLibrary FullLibrary => _fullLibrary;

        private void Awake()
        {
            if (_startingLibrary == null || _startingWand == null)
            {
                GameLog.Warn(
                    "RunSpellSession 未配置 Starting Library 或 Starting Wand，单局法术状态尚未初始化。",
                    "Run");
                return;
            }

            Initialize();
        }

        /// <summary>
        /// 从 Inspector 模板创建当前局的 Runtime Clone。返回 false 表示已经初始化过。
        /// 该操作只在开局发生，允许创建 ScriptableObject 与数组；它不属于逐帧 Hot Path。
        /// </summary>
        public bool Initialize()
        {
            if (IsInitialized)
            {
                return false;
            }

            if (_startingLibrary == null)
            {
                throw new InvalidOperationException("Starting SpellLibrary is not configured.");
            }

            if (_startingWand == null)
            {
                throw new InvalidOperationException("Starting WandLoadout is not configured.");
            }

            if (!TryRegisterAsCurrent())
            {
                return false;
            }

            RuntimeLibrary = Instantiate(_startingLibrary);
            RuntimeLibrary.name = $"{_startingLibrary.name} (Runtime)";
            RuntimeLibrary.hideFlags = HideFlags.DontSave;
            // Unity 克隆 ScriptableObject 后仍显式复制数组，明确保证 Template 与 Runtime 不共享可变容器。
            RuntimeLibrary.Available = RunSpellInventoryOps.CopyEntries(_startingLibrary.Available);

            RuntimeWand = Instantiate(_startingWand);
            RuntimeWand.name = $"{_startingWand.name} (Runtime)";
            RuntimeWand.hideFlags = HideFlags.DontSave;
            RuntimeWand.Spells = RunSpellInventoryOps.CopyEntries(_startingWand.Spells);

            IsInitialized = true;
            return true;
        }

        /// <summary>
        /// 提供给自动化测试或非 Scene Bootstrap 的依赖注入入口。
        /// 正常游戏仍应在 Inspector 配置模板，避免运行时到处查找 Asset。
        /// </summary>
        public bool ConfigureAndInitialize(SpellLibrary startingLibrary, WandLoadout startingWand)
        {
            if (IsInitialized)
            {
                return false;
            }

            _startingLibrary = startingLibrary;
            _startingWand = startingWand;
            return Initialize();
        }

        /// <summary>
        /// 从 Authoring Template 重建一份全新的本局数据。
        /// New Run 可以显式调用它；普通 Scene Load 不调用，因此 Runtime 数据会跨 Scene 保留。
        /// </summary>
        public void ResetFromTemplates()
        {
            ClearRuntime();
            Initialize();
        }

        /// <summary>
        /// 将完整 Library 中缺失的法术按 Authoring 顺序补入当前 Runtime Library。
        /// 返回 false 表示已经完整；Reward 与 Portal 重复调用不会产生重复条目或替换数组。
        /// </summary>
        public bool EnsureAllSpellsUnlocked(SpellLibrary fullLibrary)
        {
            if (fullLibrary == null)
                throw new ArgumentNullException(nameof(fullLibrary));

            EnsureInitialized();

            bool changed = RunSpellInventoryOps.TryMergeUniqueInOrder(
                RuntimeLibrary.Available,
                fullLibrary.Available ?? Array.Empty<SpellDefinition>(),
                out SpellDefinition[] merged);

            if (changed)
            {
                RuntimeLibrary.Available = merged;
            }

            return changed;
        }

        public bool EnsureAllSpellsUnlocked()
        {
            if (_fullLibrary == null)
                throw new InvalidOperationException("Full SpellLibrary is not configured.");

            return EnsureAllSpellsUnlocked(_fullLibrary);
        }

        /// <summary>
        /// 使用独立数组恢复进入 Boss 区域时的法杖程序。
        /// Checkpoint 不持有 Runtime Wand 对象本身，避免跨 Scene 保存 Scene/Runtime Object 引用。
        /// </summary>
        public bool RestoreBossCheckpoint(in BossCheckpointSnapshot snapshot)
        {
            EnsureInitialized();
            if (!snapshot.IsValid)
            {
                return false;
            }

            RuntimeWand.Spells = snapshot.CopySpells();
            RuntimeWand.BaseDraws = snapshot.BaseDraws;
            return true;
        }

        /// <summary>
        /// 把奖励加入当前局背包。重复法术不会产生第二份，也不会发布成功事件。
        /// </summary>
        public bool TryGrantSpell(SpellDefinition reward)
        {
            EnsureInitialized();

            bool added = RunSpellInventoryOps.TryAppendUnique(
                RuntimeLibrary.Available,
                reward,
                out SpellDefinition[] updatedLibrary);

            if (!added)
            {
                return false;
            }

            RuntimeLibrary.Available = updatedLibrary;
            EventBus<SpellRewardGrantedEvent>.Publish(new SpellRewardGrantedEvent
            {
                Spell = reward,
                LibraryCount = updatedLibrary.Length
            });
            return true;
        }

        /// <summary>
        /// 显式结束当前 Session 的 Runtime 所有权。
        /// 清理后静态 Current 同步置空，避免 Main Menu 或下一局读取已经失效的 Clone。
        /// </summary>
        public void ClearRuntime()
        {
            DestroyRuntimeClone(RuntimeLibrary);
            DestroyRuntimeClone(RuntimeWand);
            RuntimeLibrary = null;
            RuntimeWand = null;
            IsInitialized = false;

            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }
        }

        private void OnDestroy()
        {
            ClearRuntime();
        }

        private bool TryRegisterAsCurrent()
        {
            if (Current != null && !ReferenceEquals(Current, this))
            {
                GameLog.Warn(
                    $"RunSpellSession '{name}' 未取得当前 Run 所有权，等待重复 Session Root 被回收。",
                    "Run");
                return false;
            }

            Current = this;
            return true;
        }

        private void EnsureInitialized()
        {
            if (!IsInitialized || RuntimeLibrary == null || RuntimeWand == null)
            {
                throw new InvalidOperationException(
                    "RunSpellSession must be initialized before accessing Runtime data.");
            }
        }

        private static void DestroyRuntimeClone(UnityEngine.Object runtimeClone)
        {
            if (runtimeClone == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(runtimeClone);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(runtimeClone);
            }
        }
    }
}

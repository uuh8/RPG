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

        public SpellLibrary RuntimeLibrary { get; private set; }
        public WandLoadout RuntimeWand { get; private set; }
        public bool IsInitialized { get; private set; }

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
        /// 把奖励加入当前局背包。重复法术不会产生第二份，也不会发布成功事件。
        /// </summary>
        public bool TryGrantSpell(SpellDefinition reward)
        {
            if (!IsInitialized || RuntimeLibrary == null)
            {
                throw new InvalidOperationException("RunSpellSession must be initialized before granting rewards.");
            }

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

        private void OnDestroy()
        {
            // Runtime Clone 不属于 Project Asset，也不应跨局常驻；Scene 卸载时主动回收所有权。
            DestroyRuntimeClone(RuntimeLibrary);
            DestroyRuntimeClone(RuntimeWand);
            RuntimeLibrary = null;
            RuntimeWand = null;
            IsInitialized = false;
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

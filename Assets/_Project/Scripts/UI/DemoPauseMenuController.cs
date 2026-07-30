using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Pause、Settings 与 Victory/Defeat Panel 的局内 UI 协调器。
    /// 胜负来自 RunStateChangedEvent；本组件不读取 Enemy，也不自行判定一局是否结束。
    /// </summary>
    public sealed class DemoPauseMenuController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private RunPauseCoordinator _pauseCoordinator;
        [SerializeField] private WandEditorController _wandEditor;
        [SerializeField] private InputActionReference _pauseAction;
        [SerializeField] private DemoSceneTransitionController _sceneTransition;
        [SerializeField] private RunSessionCoordinator _sessionCoordinator;

        [Header("Panels")]
        [SerializeField] private GameObject _pausePanel;
        [SerializeField] private GameObject _settingsPanel;
        [SerializeField] private GameObject _victoryPanel;
        [SerializeField] private GameObject _defeatPanel;
        [SerializeField] private Text _victoryTimeText;

        [Header("Scene Navigation")]
        [SerializeField] private string _mainMenuSceneName = "P7_MainMenu";
        [SerializeField] private string _runSceneName = "P7_DemoRun";
        [SerializeField] private string _bossSceneName = "P8_BossField";

        private float _runStartedRealtime;
        private bool _isLoadingScene;

        private void OnEnable()
        {
            EventBus<RunStateChangedEvent>.Subscribe(OnRunStateChanged);
            if (_pauseAction != null && _pauseAction.action != null)
            {
                _pauseAction.action.performed += OnPausePerformed;
                _pauseAction.action.Enable();
            }
        }

        private void Start()
        {
            _runStartedRealtime = Time.realtimeSinceStartup;
            SetPanelActive(_pausePanel, false);
            SetPanelActive(_settingsPanel, false);
            SetPanelActive(_victoryPanel, false);
            SetPanelActive(_defeatPanel, false);
        }

        private void OnDisable()
        {
            EventBus<RunStateChangedEvent>.Unsubscribe(OnRunStateChanged);
            if (_pauseAction != null && _pauseAction.action != null)
            {
                _pauseAction.action.performed -= OnPausePerformed;
            }

            if (!_isLoadingScene && _pauseCoordinator != null)
            {
                _pauseCoordinator.ReleasePause(RunPauseReason.PauseMenu);
                _pauseCoordinator.ReleasePause(RunPauseReason.TerminalResult);
            }
        }

        public void TogglePauseMenu()
        {
            if (_pauseCoordinator == null ||
                _pauseCoordinator.HasReason(RunPauseReason.TerminalResult))
            {
                return;
            }

            if (_pauseCoordinator.HasReason(RunPauseReason.PauseMenu))
            {
                Resume();
                return;
            }

            // Escape 从 Wand Editor 切换到 Pause Menu，而不是让两个全屏交互层同时争抢 Cursor。
            if (_wandEditor != null)
            {
                _wandEditor.ForceClose();
            }

            _pauseCoordinator.RequestPause(RunPauseReason.PauseMenu);
            SetPanelActive(_settingsPanel, false);
            SetPanelActive(_pausePanel, true);
        }

        public void Resume()
        {
            if (_pauseCoordinator == null ||
                _pauseCoordinator.HasReason(RunPauseReason.TerminalResult))
            {
                return;
            }

            SetPanelActive(_settingsPanel, false);
            SetPanelActive(_pausePanel, false);
            _pauseCoordinator.ReleasePause(RunPauseReason.PauseMenu);
        }

        public void OpenSettings()
        {
            if (_pauseCoordinator == null ||
                !_pauseCoordinator.HasReason(RunPauseReason.PauseMenu))
            {
                return;
            }

            SetPanelActive(_pausePanel, false);
            SetPanelActive(_settingsPanel, true);
        }

        public void CloseSettings()
        {
            if (_pauseCoordinator == null ||
                !_pauseCoordinator.HasReason(RunPauseReason.PauseMenu))
            {
                return;
            }

            SetPanelActive(_settingsPanel, false);
            SetPanelActive(_pausePanel, true);
        }

        public void RestartRun()
        {
            if (!CanBeginNavigation(_runSceneName))
            {
                return;
            }

            RunSessionCoordinator coordinator = ResolveSessionCoordinator();
            if (coordinator != null)
            {
                coordinator.EndRun();
            }

            BeginSceneLoad(_runSceneName);
        }

        public void RetryBoss()
        {
            if (!CanBeginNavigation(_bossSceneName))
            {
                return;
            }

            RunSessionCoordinator coordinator = ResolveSessionCoordinator();
            if (coordinator == null || !coordinator.RestoreBossCheckpoint())
            {
                GameLog.Error(
                    "Retry Boss 失败：没有可恢复的 Run Session 或 Boss Checkpoint。",
                    "UI");
                return;
            }

            BeginSceneLoad(_bossSceneName);
        }

        public void ReturnToMainMenu()
        {
            if (!CanBeginNavigation(_mainMenuSceneName))
            {
                return;
            }

            RunSessionCoordinator coordinator = ResolveSessionCoordinator();
            if (coordinator != null)
            {
                coordinator.EndRun();
            }

            BeginSceneLoad(_mainMenuSceneName);
        }

        private void OnPausePerformed(InputAction.CallbackContext context)
        {
            TogglePauseMenu();
        }

        private void OnRunStateChanged(RunStateChangedEvent runEvent)
        {
            if (runEvent.CurrentState == RunState.Running)
            {
                _runStartedRealtime = Time.realtimeSinceStartup;
                return;
            }

            if (runEvent.CurrentState != RunState.Completed &&
                runEvent.CurrentState != RunState.Failed)
            {
                return;
            }

            if (_wandEditor != null)
            {
                _wandEditor.ForceClose();
            }

            if (_pauseCoordinator != null)
            {
                _pauseCoordinator.ReleasePause(RunPauseReason.PauseMenu);
                _pauseCoordinator.RequestPause(RunPauseReason.TerminalResult);
            }

            SetPanelActive(_pausePanel, false);
            SetPanelActive(_settingsPanel, false);
            bool isVictory = runEvent.CurrentState == RunState.Completed;
            SetPanelActive(_victoryPanel, isVictory);
            SetPanelActive(_defeatPanel, !isVictory);

            if (isVictory && _victoryTimeText != null)
            {
                float elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - _runStartedRealtime);
                int totalSeconds = Mathf.FloorToInt(elapsed);
                _victoryTimeText.text = $"通关时间  {totalSeconds / 60:00}:{totalSeconds % 60:00}";
            }
        }

        private void BeginSceneLoad(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                GameLog.Error("DemoPauseMenuController received an empty Scene name.", "UI");
                return;
            }

            if (_sceneTransition != null && !_sceneTransition.LoadScene(sceneName))
            {
                return;
            }

            _isLoadingScene = true;
            if (_sceneTransition == null)
            {
                // 兼容尚未装配 Fade Prefab 的旧 Scene；P7_DemoRun 在 Task 7 Gate 必须绑定统一 Transition。
                if (_pauseCoordinator != null)
                {
                    _pauseCoordinator.PrepareForSceneTransition();
                }
                else
                {
                    Time.timeScale = 1f;
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }

                SceneManager.LoadSceneAsync(sceneName);
            }
        }

        private bool CanBeginNavigation(string sceneName)
        {
            return !_isLoadingScene &&
                   !string.IsNullOrWhiteSpace(sceneName) &&
                   (_sceneTransition == null ||
                    !_sceneTransition.IsTransitioning);
        }

        private RunSessionCoordinator ResolveSessionCoordinator()
        {
            if (_sessionCoordinator == null)
            {
                _sessionCoordinator = RunSessionCoordinator.Current;
            }

            return _sessionCoordinator;
        }

        private static void SetPanelActive(GameObject panel, bool active)
        {
            if (panel != null)
            {
                panel.SetActive(active);
            }
        }
    }
}

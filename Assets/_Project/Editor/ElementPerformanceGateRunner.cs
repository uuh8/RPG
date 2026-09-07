using System;
using System.IO;
using Game.Core;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 仅 Editor 的显式测试入口。外部工具写入一次性请求后由主线程调用 TestRunner，
    /// 避免关闭当前项目或在同一项目启动第二个 Unity；不自动修改场景和材料。
    /// </summary>
    [InitializeOnLoad]
    internal static class ElementPerformanceGateRunner
    {
        private const string RequestPath = "Temp/ElementPerformance/run-editmode.request";
        private const string ResultPath = "Temp/ElementPerformance/editmode-results.xml";
        private const string RunningKey = "ElementPerformance.GateRunning";
        private const string RefreshKey = "ElementPerformance.GateRefreshed";
        private const string ReadyAtKey = "ElementPerformance.GateReadyAt";
        private static TestRunnerApi _api;
        private static GateCallbacks _callbacks;
        private static double _nextPoll;

        static ElementPerformanceGateRunner()
        {
            EditorApplication.update += Poll;
            if (SessionState.GetBool(RunningKey, false)) Register();
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1d;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode
                || SessionState.GetBool(RunningKey, false) || !File.Exists(RequestPath)) return;
            // 请求可能紧跟源码写入。先完成 Asset Refresh / Domain Reload，防止执行仍装载的上一版测试。
            if (!SessionState.GetBool(RefreshKey, false))
            {
                SessionState.SetBool(RefreshKey, true);
                SessionState.SetFloat(ReadyAtKey, (float)EditorApplication.timeSinceStartup + 5f);
                AssetDatabase.Refresh();
                return;
            }
            if (EditorApplication.timeSinceStartup < SessionState.GetFloat(ReadyAtKey, 0f)) return;
            // 消费固定路径的一次性控制文件；不会删除测试结果或任何项目资产。
            File.Delete(RequestPath);
            SessionState.SetBool(RefreshKey, false);
            Run();
        }

        [MenuItem("Tools/Element Performance/Run Element/Rendering EditMode Gate")]
        private static void Run()
        {
            if (SessionState.GetBool(RunningKey, false)) return;
            SessionState.SetBool(RunningKey, true);
            Register();
            GameLog.Info("[ELEMENT-PERF-GATE] Started", "Editor");
            _api.Execute(new ExecutionSettings(new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "Game.ElementField.Tests", "Game.Rendering.Tests" }
            }));
        }

        private static void Register()
        {
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _callbacks = new GateCallbacks();
            _api.RegisterCallbacks(_callbacks);
        }

        private sealed class GateCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
            public void RunFinished(ITestResultAdaptor result)
            {
                try
                {
                    Directory.CreateDirectory("Temp/ElementPerformance");
                    TestRunnerApi.SaveResultToFile(result, ResultPath);
                    GameLog.Info($"[ELEMENT-PERF-GATE] Passed={result.PassCount} Failed={result.FailCount} "
                        + $"Skipped={result.SkipCount} Inconclusive={result.InconclusiveCount}", "Editor");
                }
                catch (Exception exception)
                {
                    GameLog.Error($"[ELEMENT-PERF-GATE] Result export failed: {exception.Message}", "Editor");
                }
                finally
                {
                    SessionState.SetBool(RunningKey, false);
                    _api.UnregisterCallbacks(_callbacks);
                    UnityEngine.Object.DestroyImmediate(_api);
                    _api = null;
                    _callbacks = null;
                }
            }
        }
    }
}

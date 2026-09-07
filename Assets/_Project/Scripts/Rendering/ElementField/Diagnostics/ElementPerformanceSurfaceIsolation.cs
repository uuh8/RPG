using System;
using Game.Core;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>只挂在专用性能场景。隔离液面开销，不改变粒子模拟、查询或生产场景设置。</summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class ElementPerformanceSurfaceIsolation : MonoBehaviour
    {
        [SerializeField] private GpuLiquidSurfaceRenderer[] _surfaces;
        [SerializeField] private GpuLiquidSurfaceRenderer _water;

        private void Start()
        {
            string mode = "all";
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "-ewperfSurface" && i + 1 < args.Length) mode = args[++i];
            if (mode != "all" && mode != "water" && mode != "off")
            {
                GameLog.Error("[ELEMENT-PERF] Invalid surface isolation mode", "ElementPerformance");
                if (!Application.isEditor) Application.Quit(1);
                return;
            }
            // Start 早于 Lab 的 Warmup。资源释放与开关成本不计入 Measure；all 路径不触碰组件。
            if (mode != "all" && _surfaces != null)
                for (int i = 0; i < _surfaces.Length; i++)
                    if (_surfaces[i] != null) _surfaces[i].enabled = mode == "water" && _surfaces[i] == _water;
            GameLog.Info($"[ELEMENT-PERF] SurfaceMode={mode}", "ElementPerformance");
        }
    }
}

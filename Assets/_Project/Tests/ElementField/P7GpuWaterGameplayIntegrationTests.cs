using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 验证 P7 的 GPU 水不只是“能渲染”，还把异步 Readback 得到的 Occupancy
    /// 显式交给 Exposure System；否则 PBF 模式下角色脚下的 Water Amount 永远为 0。
    /// </summary>
    public sealed class P7GpuWaterGameplayIntegrationTests
    {
        private const string P7ScenePath = "Assets/_Project/Scenes/P7_DemoRun.unity";

        [Test]
        public void P7ExposureReferencesGpuWaterGameplayOccupancyBridge()
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(P7ScenePath, OpenSceneMode.Single);
                ElementWorldExposureSystem exposure = Object.FindFirstObjectByType<ElementWorldExposureSystem>(
                    FindObjectsInactive.Include);
                FluidGameplayOccupancyBridge bridge =
                    Object.FindFirstObjectByType<FluidGameplayOccupancyBridge>(FindObjectsInactive.Include);

                Assert.That(exposure, Is.Not.Null, "P7 缺少 ElementWorldExposureSystem。");
                Assert.That(bridge, Is.Not.Null, "P7 缺少 FluidGameplayOccupancyBridge。");

                var serializedExposure = new SerializedObject(exposure);
                SerializedProperty occupancyProperty =
                    serializedExposure.FindProperty("_liquidOccupancyComponent");
                Assert.That(occupancyProperty, Is.Not.Null,
                    "Exposure System 缺少 GPU Water Gameplay Occupancy 序列化入口。");
                Assert.That(occupancyProperty.objectReferenceValue, Is.SameAs(bridge),
                    "P7 的 Exposure System 必须显式引用 GPU 水 Bridge，不能依赖同物体 GetComponent 的偶然查找。");
            }
            finally
            {
                // Test Runner 可能在“没有任何已加载场景”的隔离环境启动；空 Setup 不能传回 Unity，
                // 否则既会遮住真正断言，也会把 P7 的 Runtime Component 留给后续测试造成污染。
                if (previousSetup.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
    }
}

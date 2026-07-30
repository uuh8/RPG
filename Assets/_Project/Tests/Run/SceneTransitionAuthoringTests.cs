using Game.UI;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Run.Tests
{
    /// <summary>
    /// Scene Transition 是常驻服务，不是“黑色图片是否显示”的开关。
    /// 因此 Fade Root 必须始终 active；真正的显隐由 CanvasGroup.alpha 管理。
    /// </summary>
    public sealed class SceneTransitionAuthoringTests
    {
        [TestCase("Assets/_Project/Scenes/P7_DemoRun.unity")]
        [TestCase("Assets/_Project/Scenes/P8_BossField.unity")]
        public void FadeControllerRoot_RemainsActiveAndRenderable(
            string scenePath)
        {
            // Preview Scene 不会替换开发者当前打开或尚未保存的 Scene，
            // 适合对磁盘上的 Authoring 数据执行自动化回归检查。
            Scene scene = EditorSceneManager.OpenPreviewScene(scenePath);
            try
            {
                DemoSceneTransitionController controller =
                    FindTransitionController(scene);

                Assert.That(
                    controller,
                    Is.Not.Null,
                    $"{scenePath} 缺少 DemoSceneTransitionController。");
                Assert.That(
                    controller.gameObject.activeSelf,
                    Is.True,
                    "Fade Root inactive 时 OnEnable 不会订阅 Scene Transition Event。");
                Assert.That(
                    controller.gameObject.activeInHierarchy,
                    Is.True,
                    "Fade Root 的父层级也必须保持 active。");
                Assert.That(
                    controller.transform.localScale.sqrMagnitude,
                    Is.GreaterThan(0f),
                    "Fade Canvas 的 Transform Scale 不能为零；隐藏应使用 CanvasGroup.alpha。");
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static DemoSceneTransitionController FindTransitionController(
            Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                DemoSceneTransitionController[] controllers =
                    roots[rootIndex]
                        .GetComponentsInChildren<DemoSceneTransitionController>(
                            true);
                if (controllers.Length > 0)
                {
                    return controllers[0];
                }
            }

            return null;
        }
    }
}

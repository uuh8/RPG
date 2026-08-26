using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI.Tests
{
    /// <summary>
    /// 锁定 Sticky UI 与 Projectile 的资源接线。这里验证“引用属于谁”，而不是只验证字段非空；
    /// 否则引用到场景里另一个有效 Image/Text 时，Null Check 仍会通过但画面不会更新。
    /// </summary>
    public sealed class StickyContentContractTests
    {
        private const string ScenePath = "Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string ProjectilePath =
            "Assets/_Project/Art/Elemental/Prefabs/PF_ElementStickyProjectile.prefab";

        [Test]
        public void EveryStatusViewReferencesItsOwnChildIconAndPercentageText()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                PlayerStatusBar bar = Object.FindFirstObjectByType<PlayerStatusBar>(
                    FindObjectsInactive.Include);
                Assert.That(bar, Is.Not.Null);
                AssertViewOwnsPresentation(bar, "_burningView");
                AssertViewOwnsPresentation(bar, "_wetView");
                AssertViewOwnsPresentation(bar, "_poisonedView");
                AssertViewOwnsPresentation(bar, "_stickyView");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void AssertViewOwnsPresentation(PlayerStatusBar bar, string viewFieldName)
        {
            StatusIconView view = GetField<StatusIconView>(bar, viewFieldName);
            Image icon = GetField<Image>(view, "_icon");
            Text percentage = GetField<Text>(view, "_percentageText");

            Assert.That(icon.transform.IsChildOf(view.transform), Is.True,
                $"{viewFieldName} 的 Icon 必须属于自己的 Slot，不能引用相邻状态的子对象。");
            Assert.That(percentage.transform.IsChildOf(view.transform), Is.True,
                $"{viewFieldName} 的百分比必须属于自己的 Slot，不能引用相邻状态的子对象。");
        }

        [Test]
        public void StickyProjectileUsesMeshCompatibleMaterialInsteadOfProceduralSurfaceShader()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectilePath);
            Assert.That(prefab, Is.Not.Null);
            MeshRenderer renderer = prefab.GetComponent<MeshRenderer>();
            Assert.That(renderer, Is.Not.Null);
            Assert.That(renderer.sharedMaterial, Is.Not.Null);
            Assert.That(renderer.sharedMaterial.shader.name,
                Is.Not.EqualTo("Game/Elemental/Liquid Procedural"));
        }

        private static T GetField<T>(object owner, string fieldName) where T : Object
        {
            FieldInfo field = owner.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(owner) as T;
        }
    }
}

using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Run.Tests
{
    public sealed class GameplayGuideControllerTests
    {
        [Test]
        public void NextPage_ResetsBodyScrollToTop()
        {
            GameObject root = new GameObject("GameplayGuideTestRoot", typeof(RectTransform));
            GameplayGuideController controller = root.AddComponent<GameplayGuideController>();
            GameObject panel = new GameObject("Panel");
            Text title = new GameObject("Title", typeof(RectTransform)).AddComponent<Text>();
            Text body = new GameObject("Body", typeof(RectTransform)).AddComponent<Text>();
            ScrollRect scrollRect = new GameObject("Scroll", typeof(RectTransform))
                .AddComponent<ScrollRect>();
            RectTransform content = new GameObject("Content", typeof(RectTransform))
                .GetComponent<RectTransform>();
            panel.transform.SetParent(root.transform, false);
            title.transform.SetParent(root.transform, false);
            body.transform.SetParent(content, false);
            scrollRect.transform.SetParent(root.transform, false);
            content.SetParent(scrollRect.transform, false);
            scrollRect.content = content;

            GameplayGuideDefinition definition =
                ScriptableObject.CreateInstance<GameplayGuideDefinition>();
            definition.Pages = new[]
            {
                new GameplayGuideDefinition.Page { Title = "第一页", Body = "第一页正文" },
                new GameplayGuideDefinition.Page { Title = "第二页", Body = "第二页正文" }
            };

            SetPrivateField(controller, "_definition", definition);
            SetPrivateField(controller, "_panel", panel);
            SetPrivateField(controller, "_titleText", title);
            SetPrivateField(controller, "_bodyText", body);
            SetPrivateField(controller, "_bodyScrollRect", scrollRect);
            SetPrivateField(controller, "_isOpen", true);

            scrollRect.verticalNormalizedPosition = 0f;
            controller.NextPageOrClose();

            Assert.That(body.text, Is.EqualTo("第二页正文"));
            Assert.That(scrollRect.verticalNormalizedPosition, Is.EqualTo(1f));

            Object.DestroyImmediate(definition);
            Object.DestroyImmediate(panel);
            Object.DestroyImmediate(root);
        }

        private static void SetPrivateField<TValue>(
            GameplayGuideController target,
            string fieldName,
            TValue value)
        {
            FieldInfo field = typeof(GameplayGuideController).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing serialized field '{fieldName}'.");
            field.SetValue(target, value);
        }
    }
}

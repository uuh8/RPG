using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Character;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// 角色吸入只属于视觉层级：Gameplay Root 与 Camera 必须留在原地，
    /// 否则关闭阶段会改变碰撞坐标或让镜头跟着模型钻进 Portal。
    /// </summary>
    public sealed class PlayerPortalTravelerTests
    {
        private readonly List<GameObject> _createdObjects =
            new List<GameObject>();

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = 1f;

            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                {
                    Object.Destroy(_createdObjects[i]);
                }
            }

            _createdObjects.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator TransitWhilePaused_MovesOnlyVisualRootAndHidesItAtDestination()
        {
            GameObject player = CreateGameObject("WizardPlayer");
            player.SetActive(false);
            player.transform.position = new Vector3(2f, 0f, -1f);

            GameObject cameraRoot = CreateGameObject("CameraRoot");
            cameraRoot.transform.SetParent(player.transform, false);
            cameraRoot.transform.localPosition = new Vector3(0f, 1.5f, 0f);

            GameObject visualRoot = CreateGameObject("MagicWand01");
            visualRoot.transform.SetParent(player.transform, false);
            visualRoot.transform.localPosition = new Vector3(0f, 1f, 0f);
            visualRoot.transform.localScale = new Vector3(2f, 2f, 2f);

            GameObject destination = CreateGameObject("TravelerAnchor");
            destination.transform.position = new Vector3(5f, 1.2f, 3f);

            PlayerPortalTraveler traveler =
                player.AddComponent<PlayerPortalTraveler>();
            SetPrivateField(traveler, "_visualRoot", visualRoot.transform);
            SetPrivateField(traveler, "_targetScaleMultiplier", 0.1f);
            player.SetActive(true);

            Vector3 playerPositionBefore = player.transform.position;
            Vector3 cameraPositionBefore = cameraRoot.transform.position;

            Time.timeScale = 0f;
            Assert.That(
                traveler.BeginPortalTransit(destination.transform, 0.05f),
                Is.True);
            Assert.That(
                traveler.BeginPortalTransit(destination.transform, 0.05f),
                Is.False,
                "同一个 Traveler 不能并行启动第二条吸入 Coroutine。");

            yield return new WaitForSecondsRealtime(0.08f);

            Assert.That(
                Vector3.Distance(player.transform.position, playerPositionBefore),
                Is.LessThan(0.0001f));
            Assert.That(
                Vector3.Distance(cameraRoot.transform.position, cameraPositionBefore),
                Is.LessThan(0.0001f));
            Assert.That(
                Vector3.Distance(
                    visualRoot.transform.position,
                    destination.transform.position),
                Is.LessThan(0.0001f));
            Assert.That(
                Vector3.Distance(
                    visualRoot.transform.localScale,
                    new Vector3(0.2f, 0.2f, 0.2f)),
                Is.LessThan(0.0001f));
            Assert.That(visualRoot.activeSelf, Is.False);
            Assert.That(traveler.IsTransiting, Is.False);
        }

        private GameObject CreateGameObject(string objectName)
        {
            GameObject gameObject = new GameObject(objectName);
            _createdObjects.Add(gameObject);
            return gameObject;
        }

        private static void SetPrivateField<T>(
            object target,
            string fieldName,
            T value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"找不到测试字段 {fieldName}。");
            field.SetValue(target, value);
        }
    }
}

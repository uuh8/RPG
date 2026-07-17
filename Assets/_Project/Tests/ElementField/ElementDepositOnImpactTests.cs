using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// Projectile 的真实 Collision 与事件订阅留给 Play Mode Gate 验证；这里先固定
    /// “Inspector 配置如何转换成值类型 ElementWriteRequest”的规则与非法输入边界。
    /// </summary>
    public sealed class ElementDepositOnImpactTests
    {
        private GameObject _gameObject;
        private ElementDepositOnImpact _deposit;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("ElementDepositOnImpactTests");
            _deposit = _gameObject.AddComponent<ElementDepositOnImpact>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_gameObject);
        }

        [Test]
        public void WaterConfigBuildsWaterRequest()
        {
            Configure(ElementMaterialKind.Water, totalAmount: 220, radius: 0.75f, linearFalloff: true);
            var worldPosition = new Vector3(1.25f, 2.5f, -3.75f);

            bool built = _deposit.TryBuildRequest(worldPosition, out ElementWriteRequest request);

            Assert.That(built, Is.True);
            Assert.That(request.WorldPosition, Is.EqualTo(worldPosition));
            Assert.That(request.MaterialKind, Is.EqualTo(ElementMaterialKind.Water));
            Assert.That(request.TotalAmount, Is.EqualTo(220));
            Assert.That(request.Radius, Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(request.UseLinearFalloff, Is.True);
        }

        [TestCase(0)]
        [TestCase(-5)]
        public void NonPositiveAmountDoesNotBuildRequest(int invalidAmount)
        {
            Configure(ElementMaterialKind.Fire, invalidAmount, radius: 0.5f, linearFalloff: false);

            bool built = _deposit.TryBuildRequest(Vector3.one, out ElementWriteRequest request);

            Assert.That(built, Is.False);
            Assert.That(request.TotalAmount, Is.Zero);
        }

        [Test]
        public void NegativeRadiusIsClampedToZero()
        {
            Configure(ElementMaterialKind.Fire, totalAmount: 180, radius: -2f, linearFalloff: true);

            bool built = _deposit.TryBuildRequest(Vector3.zero, out ElementWriteRequest request);

            Assert.That(built, Is.True);
            Assert.That(request.Radius, Is.Zero);
        }

        [Test]
        public void DepositWithoutActiveFieldFailsSafely()
        {
            Configure(ElementMaterialKind.Water, totalAmount: 220, radius: 0.75f, linearFalloff: true);

            Assert.That(ElementFieldRuntime.Active, Is.Null);
            LogAssert.Expect(
                LogType.Warning,
                "[ElementField] 元素沉积已忽略：当前场景中不存在可用的 ElementFieldRuntime。");
            Assert.That(_deposit.TryDepositAt(Vector3.zero), Is.False);
        }

        private void Configure(
            ElementMaterialKind materialKind,
            int totalAmount,
            float radius,
            bool linearFalloff)
        {
            SetPrivateField("_materialKind", materialKind);
            SetPrivateField("_totalAmount", totalAmount);
            SetPrivateField("_radius", radius);
            SetPrivateField("_useLinearFalloff", linearFalloff);
        }

        private void SetPrivateField<T>(string fieldName, T value)
        {
            FieldInfo field = typeof(ElementDepositOnImpact).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少预期的序列化字段 {fieldName}。");
            field.SetValue(_deposit, value);
        }
    }
}

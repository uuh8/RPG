using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class DormantLiquidRenderPolicyTests
    {
        [TestCase(0, 1f, .08f, 0f)]
        [TestCase(1, 1f, .08f, .08f)]
        [TestCase(128, 1f, .08f, 128f / 255f)]
        [TestCase(255, .5f, .08f, .5f)]
        public void Height_PreservesEmptyAndClampsNonEmptyToVisibleThickness(
            byte amount, float cellSize, float minimumRatio, float expected)
        {
            Assert.That(DormantLiquidRenderPolicy.CalculateHeight(
                amount, cellSize, minimumRatio), Is.EqualTo(expected).Within(.0001f));
        }

        [Test]
        public void Matrix_PlacesFillOnCellBottomInsteadOfFloatingAtCenter()
        {
            Matrix4x4 matrix = DormantLiquidRenderPolicy.CreateCellMatrix(
                new Vector3Int(2, 3, 4), Vector3.one, .5f, 128, .08f);
            float height = .5f * 128f / 255f;
            Assert.That(matrix.GetColumn(3).y, Is.EqualTo(1f + 3f * .5f + height * .5f).Within(.0001f));
            Assert.That(matrix.lossyScale.y, Is.EqualTo(height).Within(.0001f));
        }
    }
}

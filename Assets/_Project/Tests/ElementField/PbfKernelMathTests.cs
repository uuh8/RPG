using System;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// CPU reference 锁定 PBF Kernel 的数学 Contract；Compute Shader 必须逐项保持同一公式，
    /// 否则 GPU 中看似小的系数偏差会被每个 Substep 的 Solver Iteration 累积放大。
    /// </summary>
    public sealed class PbfKernelMathTests
    {
        [Test]
        public void Poly6IsGreatestAtOriginAndZeroAtSupportBoundary()
        {
            const float h = 2f;
            float expectedAtOrigin = 315f / (64f * Mathf.PI * Mathf.Pow(h, 3f));

            Assert.That(PbfKernelMath.Poly6(0f, h), Is.EqualTo(expectedAtOrigin).Within(1e-5f));
            Assert.That(PbfKernelMath.Poly6(0.5f, h), Is.LessThan(expectedAtOrigin));
            Assert.That(PbfKernelMath.Poly6(h, h), Is.Zero);
            Assert.That(PbfKernelMath.Poly6(h + 0.1f, h), Is.Zero);
        }

        [Test]
        public void SpikyGradientUsesLockedThreeDimensionalFormulaAndRejectsDegenerateDirection()
        {
            const float h = 2f;
            Vector3 gradient = PbfKernelMath.SpikyGradient(new Vector3(0.5f, 0f, 0f), h);
            float expectedX = -45f / (Mathf.PI * Mathf.Pow(h, 6f)) * Mathf.Pow(h - 0.5f, 2f);

            Assert.That(gradient.x, Is.EqualTo(expectedX).Within(1e-6f));
            Assert.That(gradient.y, Is.Zero);
            Assert.That(gradient.z, Is.Zero);
            Assert.That(PbfKernelMath.SpikyGradient(Vector3.zero, h), Is.EqualTo(Vector3.zero));
            Assert.That(PbfKernelMath.SpikyGradient(new Vector3(h, 0f, 0f), h), Is.EqualTo(Vector3.zero));
            Assert.That(PbfKernelMath.SpikyGradient(new Vector3(h + 0.01f, 0f, 0f), h), Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void IsolatedParticleLambdaStaysFinite()
        {
            float lambda = PbfKernelMath.CalculateLambda(
                density: 0f,
                restDensity: 1000f,
                gradientSumSquared: 0f,
                lambdaEpsilon: 0.0001f);

            Assert.That(float.IsNaN(lambda) || float.IsInfinity(lambda), Is.False);
            Assert.That(lambda, Is.EqualTo(10000f).Within(0.1f));
        }

        [Test]
        public void SymmetricPairCorrectionsSumToApproximatelyZero()
        {
            const float h = 1f;
            const float restDensity = 1000f;
            Vector3 right = new Vector3(0.4f, 0f, 0f);
            Vector3 correctionA = PbfKernelMath.CalculatePairPositionCorrection(
                right,
                lambdaI: -0.5f,
                lambdaJ: -0.5f,
                artificialPressure: 0.001f,
                smoothingRadius: h,
                restDensity: restDensity);
            Vector3 correctionB = PbfKernelMath.CalculatePairPositionCorrection(
                -right,
                lambdaI: -0.5f,
                lambdaJ: -0.5f,
                artificialPressure: 0.001f,
                smoothingRadius: h,
                restDensity: restDensity);

            Assert.That((correctionA + correctionB).sqrMagnitude, Is.LessThan(1e-10f));
        }

        [TestCase(0f)]
        [TestCase(-0.01f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void KernelRejectsInvalidSmoothingRadius(float smoothingRadius)
        {
            Assert.That(
                () => PbfKernelMath.Poly6(0f, smoothingRadius),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }
}

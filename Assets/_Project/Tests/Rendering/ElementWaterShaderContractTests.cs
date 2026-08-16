using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 锁定体积水 Shader 与 Material/Editor 的序列化契约。
    /// 这里只证明属性存在，不把 Shader Import 成功冒充为最终 Game View 视觉验收。
    /// </summary>
    public sealed class ElementWaterShaderContractTests
    {
        private static readonly string[] SharedWaterProperties =
        {
            "_ShallowColor", "_DeepColor", "_FresnelColor",
            "_ShallowAlpha", "_DeepAlpha", "_FresnelAlphaStrength", "_WaterAlpha",
            "_WaveScaleA", "_WaveScaleB", "_WaveSpeedA", "_WaveSpeedB",
            "_WaveNormalStrength", "_TriplanarSharpness", "_VolumeWaveWorldScale",
            "_DepthFadeDistance", "_DepthNoiseStrength", "_FresnelPower", "_Smoothness",
            "_FoamColor", "_FoamDistance", "_FoamStrength", "_FoamAlphaBoost"
        };

        [Test]
        public void ElementWaterShaderExposesVolumeProjectionProperties()
        {
            AssertSharedProperties("Game/Elemental/Water");
        }

        [Test]
        public void ProceduralLiquidPreservesLegacyWaterProperties()
        {
            AssertSharedProperties("Game/Elemental/Liquid Procedural");
        }

        [Test]
        public void ProceduralLiquidUsesUnityIndirectContractAndSharedWaterCore()
        {
            const string shaderPath =
                "Assets/_Project/Art/Elemental/Shaders/ElementLiquidProcedural.shader";
            string source = File.ReadAllText(shaderPath);
            int defineIndex = source.IndexOf("#define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs");
            int includeIndex = source.IndexOf("#include \"UnityIndirect.cginc\"");
            Assert.That(defineIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(includeIndex, Is.GreaterThan(defineIndex));
            StringAssert.Contains("InitIndirectDrawArgs(0)", source);
            StringAssert.Contains("GetIndirectVertexID(input.vertexID)", source);
            StringAssert.Contains("StructuredBuffer<FluidSurfaceTriangle> _FluidSurfaceTriangles", source);
            StringAssert.Contains("ElementWaterCore.hlsl", source);
            StringAssert.Contains("ElementWaterBuildOrthonormalBasis", source);
            StringAssert.Contains("\"Queue\" = \"Transparent\"", source);
            StringAssert.Contains("Cull Back", source);
        }

        private static void AssertSharedProperties(string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, $"Shader import failed: {shaderName}");

            var material = new Material(shader);
            try
            {
                for (int i = 0; i < SharedWaterProperties.Length; i++)
                {
                    Assert.That(material.HasProperty(SharedWaterProperties[i]), Is.True,
                        $"{shaderName} is missing {SharedWaterProperties[i]}.");
                }
            }
            finally
            {
                // Material 是 Unity Native Engine Object，不由普通 C# using/IDisposable 管理。
                Object.DestroyImmediate(material);
            }
        }
    }
}

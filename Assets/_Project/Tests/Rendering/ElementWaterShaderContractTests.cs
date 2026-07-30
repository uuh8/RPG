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
        [Test]
        public void ElementWaterShaderExposesVolumeProjectionProperties()
        {
            Shader shader = Shader.Find("Game/Elemental/Water");
            Assert.That(shader, Is.Not.Null);

            var material = new Material(shader);
            try
            {
                Assert.That(
                    material.HasProperty("_TriplanarSharpness"),
                    Is.True);
                Assert.That(
                    material.HasProperty("_VolumeWaveWorldScale"),
                    Is.True);
            }
            finally
            {
                // Material 是 Unity Native Engine Object，不由普通 C# using/IDisposable 管理。
                Object.DestroyImmediate(material);
            }
        }
    }
}

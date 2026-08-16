using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class GpuLiquidSurfaceRendererInitializationTests
    {
        [Test]
        public void InvalidProfileIsConvertedToFailureWithoutThrowing()
        {
            LiquidRenderProfile profile = ScriptableObject.CreateInstance<LiquidRenderProfile>();
            try
            {
                var serialized = new UnityEditor.SerializedObject(profile);
                serialized.FindProperty("_targetVoxelSize").floatValue = 0f;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.DoesNotThrow(() => LiquidRenderSettingsFactory.TryCreate(
                    profile, out _, out _));
                Assert.That(LiquidRenderSettingsFactory.TryCreate(
                    profile, out _, out string error), Is.False);
                Assert.That(error, Is.Not.Empty);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}

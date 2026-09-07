using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;

namespace Game.ElementField.Tests
{
    public sealed class MaterialStatusProjectionTests
    {
        [Test]
        public void DefaultProjectionMapsWaterFirePoisonAndStickyWithoutEmbeddingStatusInCatalog()
        {
            MaterialCatalog catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(
                "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
            MaterialStatusProjectionProfile profile = AssetDatabase.LoadAssetAtPath<MaterialStatusProjectionProfile>(
                "Assets/_Project/ScriptableObjects/Materials/MaterialStatusProjection_Default.asset");
            Assert.That(catalog, Is.Not.Null);
            Assert.That(profile, Is.Not.Null);
            MaterialStatusProjectionSnapshot snapshot = profile.CreateSnapshot(catalog.CreateSnapshot());
            Assert.That(snapshot.Count, Is.EqualTo(4));
            Assert.That(snapshot.Get(0).Status, Is.EqualTo(StatusKind.Wet));
            Assert.That(snapshot.Get(1).Status, Is.EqualTo(StatusKind.Burning));
            Assert.That(snapshot.Get(2).Status, Is.EqualTo(StatusKind.Poisoned));
            Assert.That(snapshot.Get(3).Status, Is.EqualTo(StatusKind.Sticky));
        }
    }
}

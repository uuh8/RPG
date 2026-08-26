using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Game.Materials.Tests
{
    public sealed class MaterialIdentityContractTests
    {
        [Test]
        public void MaterialIds_PreserveSerializedAndGpuByteProtocol()
        {
            Assert.That((byte)MaterialId.Empty, Is.EqualTo(0));
            Assert.That((byte)MaterialId.Water, Is.EqualTo(1));
            Assert.That((byte)MaterialId.Fire, Is.EqualTo(2));
            Assert.That((byte)MaterialId.Poison, Is.EqualTo(3));
            Assert.That((byte)MaterialId.Sticky, Is.EqualTo(4));
        }

        [Test]
        public void BehaviorKind_ContainsOnlyTheCurrentSemanticCategories()
        {
            Assert.That(
                Enum.GetValues(typeof(MaterialBehaviorKind)),
                Is.EqualTo(new[]
                {
                    MaterialBehaviorKind.None,
                    MaterialBehaviorKind.Liquid,
                    MaterialBehaviorKind.ReactiveField,
                }));
        }

        [Test]
        public void MaterialsAssembly_DoesNotReferenceUpperGameplayOrPresentationModules()
        {
            string[] references = typeof(MaterialId).Assembly
                .GetReferencedAssemblies()
                .Select(name => name.Name)
                .ToArray();

            Assert.That(references, Does.Not.Contain("Game.Combat"));
            Assert.That(references, Does.Not.Contain("Game.ElementField"));
            Assert.That(references, Does.Not.Contain("Game.Rendering"));
        }

        [Test]
        public void Definition_DoesNotOwnBackendRenderingStatusOrSolverParameters()
        {
            FieldInfo[] fields = typeof(MaterialDefinition).GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            Assert.That(fields.Select(field => field.Name), Is.EquivalentTo(new[] { "_id", "_behavior" }));
        }
    }
}

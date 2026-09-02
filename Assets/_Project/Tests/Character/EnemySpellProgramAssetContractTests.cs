using Game.Combat;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class EnemySpellProgramAssetContractTests
    {
        private const string SpellPath =
            "Assets/_Project/ScriptableObjects/Enemy/Spells/Enemy_Emit_Fireball.asset";
        private const string ProgramPath =
            "Assets/_Project/ScriptableObjects/Enemy/Programs/Enemy_Wizard_Program_Fireball.asset";
        private const string PrefabPath =
            "Assets/_Project/Art/Prefabs/BaseEnemy/MagicEnemy_Base.prefab";

        [Test]
        public void OrdinaryWizardProgram_HasExpectedFireballContract()
        {
            SpellDefinition spell = AssetDatabase.LoadAssetAtPath<SpellDefinition>(SpellPath);
            Assert.That(spell, Is.Not.Null);
            Assert.That(spell.Kind, Is.EqualTo(SpellKind.Emit));
            Assert.That(spell.BaseDamage, Is.EqualTo(10f));
            Assert.That(spell.BaseSpeed, Is.EqualTo(18f));
            Assert.That(spell.DamageType, Is.EqualTo(DamageType.Magical));
            Assert.That(spell.ProjectilePrefab, Is.Not.Null);

            WandLoadout program = AssetDatabase.LoadAssetAtPath<WandLoadout>(ProgramPath);
            Assert.That(program, Is.Not.Null);
            Assert.That(program.BaseDraws, Is.EqualTo(1));
            Assert.That(program.Spells, Has.Length.EqualTo(1));
            Assert.That(program.Spells[0], Is.SameAs(spell));
        }

        [Test]
        public void MagicEnemyPrefab_HasSpellCasterOriginAndOrdinaryProgram()
        {
            WandLoadout program = AssetDatabase.LoadAssetAtPath<WandLoadout>(ProgramPath);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.GetComponent<SpellCaster>(), Is.Not.Null);

            RangedEnemyController controller = prefab.GetComponent<RangedEnemyController>();
            Assert.That(controller, Is.Not.Null);

            SerializedObject serializedController = new SerializedObject(controller);
            Assert.That(
                serializedController.FindProperty("_castOrigin").objectReferenceValue,
                Is.Not.Null);

            SerializedProperty programs = serializedController.FindProperty("_spellPrograms");
            Assert.That(programs.arraySize, Is.EqualTo(1));
            Assert.That(
                programs.GetArrayElementAtIndex(0).objectReferenceValue,
                Is.SameAs(program));
        }
    }
}

using NUnit.Framework;
using UnityEngine;
using Game.Combat;

namespace Game.Combat.Tests
{
    public class ManaComponentTests
    {
        [Test]
        public void Spend_WhenEnoughMana_DecreasesCurrentMana()
        {
            GameObject go = new GameObject("mana-test");
            try
            {
                ManaComponent mana = go.AddComponent<ManaComponent>();

                Assert.AreEqual(100f, mana.CurrentMana, 1e-4f);
                Assert.IsTrue(mana.Spend(30f));
                Assert.AreEqual(70f, mana.CurrentMana, 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Spend_WhenInsufficientMana_ReturnsFalseAndKeepsCurrentMana()
        {
            GameObject go = new GameObject("mana-test");
            try
            {
                ManaComponent mana = go.AddComponent<ManaComponent>();

                Assert.IsFalse(mana.Spend(130f));
                Assert.AreEqual(100f, mana.CurrentMana, 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class WandEditOpsTests
    {
        private static SpellDefinition Spell() => ScriptableObject.CreateInstance<SpellDefinition>();

        [Test]
        public void InsertAt_Middle_ShiftsRest()
        {
            var a = Spell(); var b = Spell(); var c = Spell();
            var list = new List<SpellDefinition> { a, b };
            WandEditOps.InsertAt(list, 1, c, 8);
            Assert.AreEqual(3, list.Count);
            Assert.AreSame(a, list[0]);
            Assert.AreSame(c, list[1]);
            Assert.AreSame(b, list[2]);
        }

        [Test]
        public void InsertAt_IndexClampedToEnd()
        {
            var a = Spell(); var b = Spell();
            var list = new List<SpellDefinition> { a };
            WandEditOps.InsertAt(list, 99, b, 8);
            Assert.AreEqual(2, list.Count);
            Assert.AreSame(b, list[1]);
        }

        [Test]
        public void InsertAt_AtCapacity_Rejected()
        {
            var list = new List<SpellDefinition> { Spell(), Spell() };
            WandEditOps.InsertAt(list, 0, Spell(), 2);
            Assert.AreEqual(2, list.Count); // 满，不插入
        }

        [Test]
        public void RemoveAt_Valid_Removes()
        {
            var a = Spell(); var b = Spell();
            var list = new List<SpellDefinition> { a, b };
            WandEditOps.RemoveAt(list, 0);
            Assert.AreEqual(1, list.Count);
            Assert.AreSame(b, list[0]);
        }

        [Test]
        public void RemoveAt_OutOfRange_Ignored()
        {
            var list = new List<SpellDefinition> { Spell() };
            WandEditOps.RemoveAt(list, 5);
            Assert.AreEqual(1, list.Count);
        }

        [Test]
        public void Move_ForwardReorders()
        {
            var a = Spell(); var b = Spell(); var c = Spell();
            var list = new List<SpellDefinition> { a, b, c };
            WandEditOps.Move(list, 0, 2); // a 移到末尾
            Assert.AreSame(b, list[0]);
            Assert.AreSame(c, list[1]);
            Assert.AreSame(a, list[2]);
        }

        [Test]
        public void Move_SelfNoop_And_ToClamped()
        {
            var a = Spell(); var b = Spell();
            var list = new List<SpellDefinition> { a, b };
            WandEditOps.Move(list, 1, 1); // 自身→自身：无操作
            Assert.AreSame(a, list[0]);
            Assert.AreSame(b, list[1]);
            WandEditOps.Move(list, 0, 99); // to 钳到末尾
            Assert.AreSame(b, list[0]);
            Assert.AreSame(a, list[1]);
        }
    }
}

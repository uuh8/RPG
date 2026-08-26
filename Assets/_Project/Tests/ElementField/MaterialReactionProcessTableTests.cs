using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialReactionProcessTableTests
    {
        [Test]
        public void SameContactStartsOnceAndResolvesUsingLatestSnapshot()
        {
            var tuning = new ToxicCombustionTuning
            {
                FireThreshold = 1f, PoisonThreshold = 1f, WindUpSeconds = 0.2f,
                MaxPoisonConsume = 40f, MinEffectiveConsume = 1f,
                BaseDamage = 5f, DamagePerPoison = 1f, Radius = 2f,
                CooldownSeconds = 0.3f,
            };
            var table = new MaterialReactionProcessTable(2);
            Vector3Int fire = Vector3Int.zero;
            Vector3Int poison = Vector3Int.right;
            Assert.That(table.TryStartToxic(fire, poison, 100, 80, in tuning), Is.True);
            Assert.That(table.TryStartToxic(fire, poison, 100, 80, in tuning), Is.False);
            var occupancy = new PoisonOccupancy(poison, 12);
            var output = new ToxicWorldReactionResolution[1];
            Assert.That(table.TickToxic(0.1f, occupancy, in tuning, output), Is.Zero);
            Assert.That(table.TickToxic(0.1f, occupancy, in tuning, output), Is.EqualTo(1));
            Assert.That(output[0].ConsumedGmu, Is.EqualTo(12));
            Assert.That(output[0].Damage, Is.EqualTo(17f));
            Assert.That(table.TryStartToxic(fire, poison, 100, 80, in tuning), Is.False,
                "同一接触点在 Cooldown 内不能每 Tick 重复触发毒爆。");
            Assert.That(table.TickToxic(0.3f, occupancy, in tuning, output), Is.Zero);
            Assert.That(table.TryStartToxic(fire, poison, 100, 80, in tuning), Is.True);
        }

        private sealed class PoisonOccupancy : ILiquidOccupancyReadOnly
        {
            private readonly Vector3Int _cell; private readonly byte _amount;
            public PoisonOccupancy(Vector3Int cell, byte amount) { _cell = cell; _amount = amount; }
            public bool HasValidSnapshot => true;
            public Bounds SnapshotBounds => new Bounds(Vector3.zero, Vector3.one);
            public uint SnapshotVersion => 1;
            public bool TryGetAmountUnitsPerParticle(MaterialId material, out uint amountUnits)
            { amountUnits = material == MaterialId.Poison ? 4u : 8u; return true; }
            public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination) => 0;
            public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount)
            { amount = cell == _cell && material == MaterialId.Poison ? _amount : (byte)0; return amount > 0; }
        }
    }
}

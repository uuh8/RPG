using NUnit.Framework;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class CastTraceTests
    {
        [Test]
        public void Collector_RecordAndClear_ReusesLogicalBuffer()
        {
            var collector = new CastTraceCollector(4);
            var step = new CastTraceStep(
                CastTraceStepKind.CastStarted,
                -1,
                null,
                1,
                1,
                10f,
                10f,
                CastModifierState.Default,
                CastModifierState.Default);

            collector.Record(step);
            Assert.AreEqual(1, collector.Count);
            Assert.AreEqual(CastTraceStepKind.CastStarted, collector[0].Kind);

            collector.Clear();
            Assert.AreEqual(0, collector.Count);
            Assert.IsFalse(collector.ExpandedDuringLastCollection);
        }

        [Test]
        public void Collector_Constructor_ClampsNegativeCapacity()
        {
            Assert.DoesNotThrow(() => new CastTraceCollector(-10));
        }

        [Test]
        public void Collector_WhenCapacityIsExceeded_ReportsExpansion()
        {
            var collector = new CastTraceCollector(1);
            var step = new CastTraceStep(
                CastTraceStepKind.CastStarted, -1, null,
                1, 1, 10f, 10f,
                CastModifierState.Default, CastModifierState.Default);

            collector.Record(step);
            collector.Record(step);

            Assert.IsTrue(collector.ExpandedDuringLastCollection);
        }
    }
}

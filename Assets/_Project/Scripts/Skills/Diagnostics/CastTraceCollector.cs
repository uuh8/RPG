using System.Collections.Generic;

namespace Game.Skills
{
    public sealed class CastTraceCollector
    {
        private readonly List<CastTraceStep> _steps;

        public int Count => _steps.Count;
        public CastTraceStep this[int index] => _steps[index];
        public IReadOnlyList<CastTraceStep> Steps => _steps;
        public bool ExpandedDuringLastCollection { get; private set; }

        public CastTraceCollector(int initialCapacity = 64)
        {
            _steps = new List<CastTraceStep>(initialCapacity > 0 ? initialCapacity : 0);
        }

        public void Clear()
        {
            _steps.Clear();
            ExpandedDuringLastCollection = false;
        }

        public void Record(CastTraceStep step)
        {
            if (_steps.Count == _steps.Capacity)
                ExpandedDuringLastCollection = true;

            _steps.Add(step);
        }
    }
}

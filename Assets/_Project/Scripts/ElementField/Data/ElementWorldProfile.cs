using System;
using Game.Combat;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 连续关卡稀疏元素世界的 Gameplay 配置。它只描述 Cell、Streaming 与 Simulation 预算，
    /// 不保存 Water Material、Fire Particle 等 Presentation 资源。
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Element Field/Element World Profile", fileName = "ElementWorldProfile")]
    public sealed class ElementWorldProfile : ScriptableObject
    {
        [Header("Cell And Chunk")]
        [SerializeField, Min(0.01f)] private float _cellSize = 0.25f;
        [SerializeField, Min(1)] private int _chunkSize = 8;
        [SerializeField, Min(1)] private int _maximumResidentChunks = 512;

        [Header("Interest Region")]
        [SerializeField, Min(0)] private int _activeRadiusXZChunks = 10;
        [SerializeField, Min(0)] private int _activeRadiusYChunks = 3;
        [SerializeField, Min(0f)] private float _sleepGraceSeconds = 2f;

        [Header("Fixed Simulation")]
        [SerializeField, Min(0.1f)] private float _tickRate = 10f;
        [SerializeField, Min(1)] private int _maxPendingWrites = 256;
        [SerializeField, Min(1)] private int _maxCatchUpTicks = 3;
        [SerializeField, Range(0, byte.MaxValue)] private int _maxDownFlowPerTick = 64;
        [SerializeField, Range(0, byte.MaxValue)] private int _maxLateralFlowPerTick = 16;
        [SerializeField, Range(0, byte.MaxValue)] private int _fireDecayPerTick = 1;

        [Header("Shared P2 Reaction Rules")]
        [SerializeField] private ElementReactionProfile _reactionProfile;

        public float CellSize => _cellSize;
        public int ChunkSize => _chunkSize;
        public int MaximumResidentChunks => _maximumResidentChunks;
        public int ActiveRadiusXZChunks => _activeRadiusXZChunks;
        public int ActiveRadiusYChunks => _activeRadiusYChunks;
        public float SleepGraceSeconds => _sleepGraceSeconds;
        public float TickRate => _tickRate;
        public int MaxPendingWrites => _maxPendingWrites;
        public int MaxCatchUpTicks => _maxCatchUpTicks;
        public byte MaxDownFlowPerTick => (byte)_maxDownFlowPerTick;
        public byte MaxLateralFlowPerTick => (byte)_maxLateralFlowPerTick;
        public byte FireDecayPerTick => (byte)_fireDecayPerTick;
        public ElementReactionProfile ReactionProfile => _reactionProfile;

        public long SleepGraceTicks
        {
            get
            {
                // Ceiling 保证配置的 Grace 秒数不会因为离散 Tick 向下取整而提前结束。
                return (long)Math.Ceiling(_sleepGraceSeconds * _tickRate);
            }
        }

        public ElementFieldSimulationSettings CreateSimulationSettings()
        {
            if (_reactionProfile == null)
            {
                throw new InvalidOperationException(
                    "ElementWorldProfile requires an ElementReactionProfile before creating simulation settings.");
            }

            return new ElementFieldSimulationSettings(
                _cellSize,
                (byte)_maxDownFlowPerTick,
                (byte)_maxLateralFlowPerTick,
                (byte)_fireDecayPerTick,
                _chunkSize,
                _reactionProfile.Extinguish);
        }

        private void OnValidate()
        {
            _cellSize = Mathf.Max(0.01f, _cellSize);
            _chunkSize = Mathf.Max(1, _chunkSize);
            _maximumResidentChunks = Mathf.Max(1, _maximumResidentChunks);
            _activeRadiusXZChunks = Mathf.Max(0, _activeRadiusXZChunks);
            _activeRadiusYChunks = Mathf.Max(0, _activeRadiusYChunks);
            _sleepGraceSeconds = Mathf.Max(0f, _sleepGraceSeconds);
            _tickRate = Mathf.Max(0.1f, _tickRate);
            _maxPendingWrites = Mathf.Max(1, _maxPendingWrites);
            _maxCatchUpTicks = Mathf.Max(1, _maxCatchUpTicks);
            _maxDownFlowPerTick = Mathf.Clamp(_maxDownFlowPerTick, 0, byte.MaxValue);
            _maxLateralFlowPerTick = Mathf.Clamp(_maxLateralFlowPerTick, 0, byte.MaxValue);
            _fireDecayPerTick = Mathf.Clamp(_fireDecayPerTick, 0, byte.MaxValue);
        }
    }
}

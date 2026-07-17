using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// P6-B 连续关卡的 Unity 生命周期骨架。当前 Task 只负责固定 Origin、Interest Point、
    /// Resident Chunk 的 Active/Sleep/Reclaimable 分类与固定 Tick；Water/Fire 模拟将在 Task 3 接入。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElementWorldRuntime : MonoBehaviour
    {
        private const float TransformTolerance = 0.0001f;

        [Header("Gameplay Data")]
        [SerializeField] private ElementWorldProfile _profile;

        [Header("Streaming Interest Point")]
        [Tooltip("只决定附近哪些 Chunk 工作，不会成为世界 Origin，也不会让 ElementWorldRoot 跟随玩家移动。")]
        [SerializeField] private Transform _interestPoint;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private Vector3Int _interestGlobalCell;
        [SerializeField] private Vector3Int _interestChunkKey;
        [SerializeField] private long _worldTick;
        [SerializeField] private int _residentChunkCount;
        [SerializeField] private int _activeChunkCount;
        [SerializeField] private int _sleepingChunkCount;
        [SerializeField] private int _reclaimedChunkCountLastTick;

        private ElementWorldStore _store;
        private ElementFieldTickAccumulator _tickAccumulator;
        private ElementWorldChunk[] _activeChunks;
        private ElementChunkKey[] _reclaimKeys;
        private Vector3 _origin;
        private ElementChunkKey _interestChunk;
        private bool _hasInterestChunk;

        public bool IsInitialized { get; private set; }
        public Vector3 Origin => _origin;
        public long WorldTick => _worldTick;
        public ElementChunkKey InterestChunk => _interestChunk;
        public int ResidentChunkCount => _residentChunkCount;
        public int ActiveChunkCount => _activeChunkCount;
        public int SleepingChunkCount => _sleepingChunkCount;

        private void Awake()
        {
            if (!TryInitialize())
                enabled = false;
        }

        private void Update()
        {
            if (!IsInitialized || Time.timeScale <= 0f)
                return;

            RefreshInterestChunk();

            int ticks = _tickAccumulator.Consume(Time.deltaTime);
            for (int i = 0; i < ticks; i++)
            {
                _worldTick++;
                RebuildActivitySets();
            }
        }

        /// <summary>
        /// 测试与 Debug 使用显式 deltaTime；正常 Gameplay 仍由 Update 驱动。
        /// </summary>
        public int TickForTests(float deltaTime)
        {
            if (!IsInitialized)
                return 0;

            RefreshInterestChunk();
            int ticks = _tickAccumulator.Consume(deltaTime);
            for (int i = 0; i < ticks; i++)
            {
                _worldTick++;
                RebuildActivitySets();
            }

            return ticks;
        }

        private bool TryInitialize()
        {
            if (IsInitialized)
                return true;
            if (_profile == null)
            {
                GameLog.Error("ElementWorldRuntime requires an ElementWorldProfile.", "ElementField");
                return false;
            }
            if (_interestPoint == null)
            {
                GameLog.Error("ElementWorldRuntime requires an Interest Point Transform.", "ElementField");
                return false;
            }
            if (!HasSupportedTransform())
            {
                GameLog.Error(
                    "ElementWorldRuntime requires identity world rotation and unit world scale.",
                    "ElementField");
                return false;
            }

            try
            {
                _origin = transform.position;
                _store = new ElementWorldStore(
                    _profile.ChunkSize,
                    _profile.MaximumResidentChunks);
                _tickAccumulator = new ElementFieldTickAccumulator(
                    _profile.TickRate,
                    _profile.MaxCatchUpTicks);

                // 两个工作数组按 Resident 上限一次性分配；后续 Tick 只覆盖内容，不 new List/Array。
                _activeChunks = new ElementWorldChunk[_profile.MaximumResidentChunks];
                _reclaimKeys = new ElementChunkKey[_profile.MaximumResidentChunks];
                IsInitialized = true;

                RefreshInterestChunk();
                RebuildActivitySets();
                return true;
            }
            catch (Exception exception)
            {
                GameLog.Error(
                    $"ElementWorldRuntime initialization failed: {exception.Message}",
                    "ElementField");
                return false;
            }
        }

        private void RefreshInterestChunk()
        {
            _interestGlobalCell = ElementWorldCoordinates.WorldToGlobalCell(
                _interestPoint.position,
                _origin,
                _profile.CellSize);
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(
                _interestGlobalCell,
                _profile.ChunkSize,
                out ElementChunkKey nextInterestChunk,
                out _);

            if (_hasInterestChunk && nextInterestChunk == _interestChunk)
                return;

            _interestChunk = nextInterestChunk;
            _hasInterestChunk = true;
            _interestChunkKey = new Vector3Int(
                _interestChunk.X,
                _interestChunk.Y,
                _interestChunk.Z);

            // 跨 Chunk 时立即刷新，不必等待下一个 Simulation Tick 才更新调度窗口。
            RebuildActivitySets();
        }

        private void RebuildActivitySets()
        {
            if (!_hasInterestChunk || _store == null)
                return;

            _activeChunkCount = 0;
            _sleepingChunkCount = 0;
            _reclaimedChunkCountLastTick = 0;
            int reclaimCount = 0;

            Dictionary<ElementChunkKey, ElementWorldChunk>.Enumerator enumerator =
                _store.Chunks.GetEnumerator();
            while (enumerator.MoveNext())
            {
                KeyValuePair<ElementChunkKey, ElementWorldChunk> pair = enumerator.Current;
                ElementWorldChunk chunk = pair.Value;
                bool insidePlayerRegion = ElementChunkActivityPlanner.IsInsideActiveRegion(
                    pair.Key,
                    _interestChunk,
                    _profile.ActiveRadiusXZChunks,
                    _profile.ActiveRadiusYChunks);
                ElementChunkActivityState state = ElementChunkActivityPlanner.Evaluate(
                    pair.Key,
                    _interestChunk,
                    chunk.HasAnyElement,
                    chunk.LastRelevantTick,
                    _worldTick,
                    _profile.SleepGraceTicks,
                    _profile.ActiveRadiusXZChunks,
                    _profile.ActiveRadiusYChunks);

                chunk.ActivityState = state;
                if (state == ElementChunkActivityState.Active)
                {
                    // 只有玩家兴趣范围或真实 Gameplay 事件可以续租。若 Grace Lease 自己每 Tick 都刷新时间，
                    // 远处 Chunk 会永远 Active，Sleep 将永远无法发生。
                    if (insidePlayerRegion)
                        chunk.MarkRelevant(_worldTick);
                    _activeChunks[_activeChunkCount++] = chunk;
                }
                else if (state == ElementChunkActivityState.Sleeping)
                {
                    _sleepingChunkCount++;
                }
                else
                {
                    _reclaimKeys[reclaimCount++] = pair.Key;
                }
            }
            enumerator.Dispose();

            // Dictionary 枚举过程中不能 Remove；先把 Key 写入预分配 Buffer，枚举结束后再统一回收。
            for (int i = 0; i < reclaimCount; i++)
            {
                if (_store.TryRemoveEmptyChunk(_reclaimKeys[i]))
                    _reclaimedChunkCountLastTick++;
            }

            _residentChunkCount = _store.ResidentChunkCount;
        }

        private bool HasSupportedTransform()
        {
            bool unitScale = (transform.lossyScale - Vector3.one).sqrMagnitude
                <= TransformTolerance * TransformTolerance;
            bool identityRotation = Quaternion.Angle(transform.rotation, Quaternion.identity)
                <= TransformTolerance;
            return unitScale && identityRotation;
        }
    }
}

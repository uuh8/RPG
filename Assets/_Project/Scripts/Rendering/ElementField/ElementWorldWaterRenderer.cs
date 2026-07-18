using System;
using Game.Core;
using Game.ElementField;
using Unity.Profiling;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 把连续稀疏 ElementWorld 的 Water Cell 表现为按 Chunk 复用的 Mesh。
    /// Gameplay 决定 Cell 内容，Builder 决定几何，本组件只负责 Visible Chunk、Dirty Version、
    /// Rebuild Budget 与 View Pool 生命周期；它从不反向修改 ElementField。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElementWorldWaterRenderer : MonoBehaviour
    {
        private const int MaximumQuadsPerCell = 5;
        private const int VerticesPerQuad = 4;
        private const int IndicesPerQuad = 6;

        private static readonly ProfilerMarker SyncViewsMarker =
            new ProfilerMarker("ElementWorldWater.SyncViews");
        private static readonly ProfilerMarker RebuildChunkMarker =
            new ProfilerMarker("ElementWorldWater.RebuildChunk");

        [Header("Read Only World")]
        [Tooltip("拖入场景中的 ElementWorldRuntime；Rendering 只通过 IElementWorldReadOnly 读取。")]
        [SerializeField] private ElementWorldRuntime _worldRuntime;

        [Header("Water Presentation")]
        [Tooltip("复用自研 ElementWater.shader 的 shared Material，不为每个 Chunk 克隆材质。")]
        [SerializeField] private Material _waterMaterial;

        [Header("View And Rebuild Budget")]
        [Tooltip("最多同时持有多少个 Water Chunk View。超出后保留 Gameplay 数据，但暂不创建更多视觉对象。")]
        [SerializeField, Min(1)] private int _maximumViews = 256;
        [Tooltip("单帧最多重建多少个 Dirty Chunk，用跨帧延迟换取稳定 Frame Time。")]
        [SerializeField, Min(1)] private int _maxChunkRebuildsPerFrame = 4;

        [Header("Runtime Debug (Read Only In Play Mode)")]
        [SerializeField] private int _visibleElementChunkCount;
        [SerializeField] private int _assignedViewCount;
        [SerializeField] private int _lastFrameDirtyCount;
        [SerializeField] private int _lastFrameRebuildCount;

        private IElementWorldReadOnly _world;
        private WorldWaterChunkViewPool _pool;
        private ElementChunkKey[] _visibleKeys;
        private WorldWaterChunkView[] _assignedViews;
        private int _nextRebuildScanIndex;
        private bool _isInitialized;

        private void Start()
        {
            // World Runtime 在 Awake 建 Store；Renderer 到 Start 再取只读边界，避免依赖 GameObject Awake 顺序。
            if (!TryInitialize())
                enabled = false;
        }

        private void Update()
        {
            if (!_isInitialized || !_world.IsInitialized)
                return;

            using (SyncViewsMarker.Auto())
            {
                _visibleElementChunkCount = _world.CopyVisibleChunkKeys(_visibleKeys);
                ReleaseViewsOutsideVisibleSet();
                AcquireVisibleViews();
                _assignedViewCount = _pool.CopyAssignedViews(_assignedViews);
            }

            RebuildDirtyViewsWithinBudget();
        }

        private void OnDestroy()
        {
            _pool?.Dispose();
            _pool = null;
        }

        private void OnValidate()
        {
            _maximumViews = Mathf.Max(1, _maximumViews);
            _maxChunkRebuildsPerFrame = Mathf.Max(1, _maxChunkRebuildsPerFrame);
        }

        private bool TryInitialize()
        {
            if (_isInitialized)
                return true;
            if (_worldRuntime == null)
            {
                GameLog.Error("ElementWorldWaterRenderer requires an ElementWorldRuntime.", "Rendering");
                return false;
            }
            if (_waterMaterial == null)
            {
                GameLog.Error("ElementWorldWaterRenderer requires a Water Material.", "Rendering");
                return false;
            }

            _world = _worldRuntime;
            if (!_world.IsInitialized)
            {
                GameLog.Error("ElementWorldWaterRenderer cannot initialize before ElementWorldRuntime.", "Rendering");
                return false;
            }

            try
            {
                int cellsPerChunk = checked(
                    _world.ChunkSize * _world.ChunkSize * _world.ChunkSize);
                int vertexCapacity = checked(cellsPerChunk * MaximumQuadsPerCell * VerticesPerQuad);
                int indexCapacity = checked(cellsPerChunk * MaximumQuadsPerCell * IndicesPerQuad);

                // Visible Snapshot 必须能容纳全部 Resident Chunk，之后才由 View Cap 截断。
                // 若数组直接按 View Cap 分配，Dictionary 枚举顺序会随机决定哪些近处 Chunk 被显示。
                _visibleKeys = new ElementChunkKey[_world.MaximumResidentChunkCount];
                _assignedViews = new WorldWaterChunkView[_maximumViews];
                _pool = new WorldWaterChunkViewPool(
                    transform,
                    _waterMaterial,
                    _maximumViews,
                    vertexCapacity,
                    indexCapacity);
            }
            catch (OverflowException)
            {
                GameLog.Error("ElementWorldWaterRenderer capacity exceeds Int32 limits.", "Rendering");
                return false;
            }

            _isInitialized = true;
            return true;
        }

        private void ReleaseViewsOutsideVisibleSet()
        {
            int assignedCount = _pool.CopyAssignedViews(_assignedViews);
            for (int i = 0; i < assignedCount; i++)
            {
                ElementChunkKey key = _assignedViews[i].Key;
                if (!ContainsVisibleKey(key))
                    _pool.Release(key);
            }
        }

        private void AcquireVisibleViews()
        {
            float chunkWorldSize = _world.ChunkSize * _world.CellSize;
            for (int i = 0; i < _visibleElementChunkCount; i++)
            {
                ElementChunkKey key = _visibleKeys[i];
                Vector3 worldPosition = _world.Origin + new Vector3(
                    key.X * chunkWorldSize,
                    key.Y * chunkWorldSize,
                    key.Z * chunkWorldSize);

                // TryAcquire 对已有 Key 是幂等操作；新 Key 优先复用已回收的 View。
                if (!_pool.TryAcquire(key, worldPosition, out _))
                    break;
            }
        }

        private void RebuildDirtyViewsWithinBudget()
        {
            _lastFrameDirtyCount = 0;
            _lastFrameRebuildCount = 0;
            if (_assignedViewCount == 0)
            {
                _nextRebuildScanIndex = 0;
                return;
            }

            for (int i = 0; i < _assignedViewCount; i++)
            {
                WorldWaterChunkView view = _assignedViews[i];
                uint visualVersion = CalculateVisualVersion(view.Key);
                if (!view.HasSynchronizedVersion || view.LastSeenVisualVersion != visualVersion)
                    _lastFrameDirtyCount++;
            }

            int inspected = 0;
            int cursor = _nextRebuildScanIndex % _assignedViewCount;
            while (inspected < _assignedViewCount
                   && _lastFrameRebuildCount < _maxChunkRebuildsPerFrame)
            {
                WorldWaterChunkView view = _assignedViews[cursor];
                cursor++;
                if (cursor == _assignedViewCount)
                    cursor = 0;
                inspected++;

                uint visualVersion = CalculateVisualVersion(view.Key);
                if (view.HasSynchronizedVersion && view.LastSeenVisualVersion == visualVersion)
                    continue;

                RebuildView(view);
                view.LastSeenVisualVersion = visualVersion;
                view.HasSynchronizedVersion = true;
                _lastFrameRebuildCount++;
            }

            // 环形游标保证持续变化的前几个 Chunk 不会长期占满预算，使后续 Chunk Starvation。
            _nextRebuildScanIndex = cursor;
        }

        private void RebuildView(WorldWaterChunkView view)
        {
            using (RebuildChunkMarker.Auto())
            {
                WaterChunkMeshBuilder.BuildWorldChunk(
                    _world,
                    view.Key,
                    view.Vertices,
                    view.Uvs,
                    view.Indices);

                Mesh mesh = view.Mesh;
                mesh.Clear(keepVertexLayout: false);
                if (view.Indices.Count == 0)
                {
                    view.Renderer.enabled = false;
                    return;
                }

                mesh.SetVertices(view.Vertices);
                mesh.SetUVs(0, view.Uvs);
                mesh.SetTriangles(view.Indices, 0, calculateBounds: false);
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                view.Renderer.enabled = true;
            }
        }

        private uint CalculateVisualVersion(ElementChunkKey key)
        {
            // 本 Chunk 的 Side/Top 取决于边界邻居；只比较自己的 Version 会留下旧内部面或裂缝。
            // 把六邻域版本混入一个签名，任一相邻 Chunk 变化都会触发当前 Mesh 重建。
            unchecked
            {
                uint hash = 2166136261u;
                hash = Mix(hash, _world.GetChunkVersion(key));
                hash = Mix(hash, _world.GetChunkVersion(new ElementChunkKey(key.X - 1, key.Y, key.Z)));
                hash = Mix(hash, _world.GetChunkVersion(new ElementChunkKey(key.X + 1, key.Y, key.Z)));
                hash = Mix(hash, _world.GetChunkVersion(new ElementChunkKey(key.X, key.Y - 1, key.Z)));
                hash = Mix(hash, _world.GetChunkVersion(new ElementChunkKey(key.X, key.Y + 1, key.Z)));
                hash = Mix(hash, _world.GetChunkVersion(new ElementChunkKey(key.X, key.Y, key.Z - 1)));
                hash = Mix(hash, _world.GetChunkVersion(new ElementChunkKey(key.X, key.Y, key.Z + 1)));
                return hash;
            }
        }

        private bool ContainsVisibleKey(ElementChunkKey key)
        {
            // CopyVisibleChunkKeys 已稳定排序；当前 Demo 规模下线性扫描比每帧维护 HashSet 更简单且零 GC。
            for (int i = 0; i < _visibleElementChunkCount; i++)
            {
                if (_visibleKeys[i] == key)
                    return true;
            }

            return false;
        }

        private static uint Mix(uint hash, uint value)
        {
            return (hash ^ value) * 16777619u;
        }
    }
}

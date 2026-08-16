using System;
using Game.Core;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField
{
    /// <summary>
    /// 在 Simulation Dispatch 之后低频发起单个 packed Buffer 的 AsyncGPUReadback，
    /// 再把粒子按 Global Cell 投影为只读 Water Amount。视觉仍读即时 GPU Buffer；Gameplay 接受低频延迟。
    /// </summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class FluidGameplayOccupancyBridge : MonoBehaviour, ILiquidOccupancyReadOnly
    {
        [SerializeField] private MonoBehaviour _fluidSourceComponent;
        [SerializeField] private ElementWorldRuntime _elementWorldRuntime;
        [SerializeField, Min(0.05f)] private float _readbackInterval = 0.25f;

        private IFluidGameplayReadbackSource _source;
        private NativeArray<Vector4> _readbackA;
        private NativeArray<Vector4> _readbackB;
        private LiquidGameplaySnapshot _publishedSnapshot;
        private LiquidGameplaySnapshot _stagingSnapshot;
        private FluidGameplayReadbackLease _frozenLease;
        private AsyncGPUReadbackRequest _request;
        private Action<AsyncGPUReadbackRequest> _cachedCallback;
        private FluidReadbackRequestState _requestState;
        private float _elapsed;
        private int _writeBufferIndex;
        private int _readbackErrorCount;
        private int _lastReportedErrorCount;
        private bool _supported;
        private bool _destroyed;

        public bool HasValidSnapshot => _publishedSnapshot != null
            && _publishedSnapshot.HasValidSnapshot;
        public Bounds SnapshotBounds => HasValidSnapshot
            ? _publishedSnapshot.SnapshotBounds
            : default;
        public uint SnapshotVersion => HasValidSnapshot
            ? _publishedSnapshot.SnapshotVersion
            : 0u;
        public uint AmountUnitsPerParticle => HasValidSnapshot
            ? _publishedSnapshot.AmountUnitsPerParticle
            : 0u;
        public float SnapshotIntervalSeconds => _readbackInterval;

        private void Awake()
        {
            // Attribute/OnValidate 只约束正常 Inspector 编辑；损坏 YAML 或 Runtime 工具仍可能写入 NaN。
            // 若不在初始化边界清洗，NaN 比较会让 LateUpdate 每次都穿过 interval gate，形成逐帧 Readback。
            _readbackInterval = FluidReadbackIntervalPolicy.Sanitize(_readbackInterval);
            _cachedCallback = OnReadbackCompleted;
            _source = _fluidSourceComponent as IFluidGameplayReadbackSource;
            _supported = SystemInfo.supportsAsyncGPUReadback
                && _source != null
                && _elementWorldRuntime != null;
            if (!_supported)
                return;

            // Persistent NativeArray 属于初始化资源，不得推迟到首次 LateUpdate 造成帧内分配。
            if (_source.TryGetGameplayParticleCapacity(out int particleCapacity))
                EnsureStorage(particleCapacity);
            else
                _supported = false;
        }

        private void OnEnable()
        {
            _elapsed = _readbackInterval;
        }

        private void LateUpdate()
        {
            if (_destroyed || !_supported)
                return;

            if (_requestState.IsCompleted)
                FinishCompletedRequest();

            if (_requestState.IsInFlight)
                return;

            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed < _readbackInterval)
                return;

            _elapsed = 0f;
            TryStartReadback();
        }

        private void OnDisable()
        {
            // Disabled Component 不再发请求；已提交请求的 NativeArray 保持有效，等待 callback 或 OnDestroy 收口。
            _elapsed = 0f;
        }

        private void OnDestroy()
        {
            if (_destroyed)
                return;
            _destroyed = true;

            if (_requestState.TryBeginDestroyWait())
            {
                // 只允许在最终 Teardown 等待一次；正常 LateUpdate 路径绝不阻塞 CPU/GPU Pipeline。
                _request.WaitForCompletion();
                _requestState.RecordCompletion(_request.hasError);
            }

            if (_requestState.IsCompleted)
                FinishCompletedRequest();
            ReleaseSourceLeaseOnce();
            DisposeReadbackArrays();
        }

        public bool TryGetAmount(
            Vector3Int globalCell,
            ElementMaterialKind materialKind,
            out byte amount)
        {
            if (_publishedSnapshot == null)
            {
                amount = 0;
                return false;
            }

            return _publishedSnapshot.TryGetAmount(globalCell, materialKind, out amount);
        }

        private void TryStartReadback()
        {
            if (!_elementWorldRuntime.IsInitialized
                || !_source.TryAcquireGameplayReadbackLease(
                    _elementWorldRuntime.Origin,
                    _elementWorldRuntime.CellSize,
                    out _frozenLease))
            {
                return;
            }

            if (!_readbackA.IsCreated
                || _readbackA.Length != _frozenLease.Metadata.ParticleCapacity)
            {
                // Capacity 是初始化布局的一部分；运行中变化必须拒绝，不能在 LateUpdate 重分配 Persistent Array。
                _source.ReleaseGameplayReadbackLease();
                return;
            }

            if (!_requestState.TryBegin())
            {
                _source.ReleaseGameplayReadbackLease();
                return;
            }
            try
            {
                if (_writeBufferIndex == 0)
                {
                    _request = AsyncGPUReadback.RequestIntoNativeArray(
                        ref _readbackA,
                        _frozenLease.PackedSamples,
                        _cachedCallback);
                }
                else
                {
                    _request = AsyncGPUReadback.RequestIntoNativeArray(
                        ref _readbackB,
                        _frozenLease.PackedSamples,
                        _cachedCallback);
                }
            }
            catch (Exception)
            {
                _requestState.TryCancelBeforeSubmission();
                ReleaseSourceLeaseOnce();
                _readbackErrorCount++;
                ReportReadbackErrorRateLimited();
            }
        }

        private void OnReadbackCompleted(AsyncGPUReadbackRequest request)
        {
            _requestState.RecordCompletion(request.hasError);
            // Unity 的 AsyncGPUReadback callback 到达时 Request 已完成，source Buffer 不再被 GPU 读取。
            // 这里解除 lease；若 source 已先 Disable/Destroy，它才会执行延迟释放。Binning 仍留在 LateUpdate。
            ReleaseSourceLeaseOnce();
        }

        private void FinishCompletedRequest()
        {
            if (!_requestState.TryConsumeCompletion(out bool hasError))
                return;

            ReleaseSourceLeaseOnce();
            if (hasError)
            {
                _readbackErrorCount++;
                ReportReadbackErrorRateLimited();
                return;
            }

            NativeArray<Vector4> completed = _writeBufferIndex == 0
                ? _readbackA
                : _readbackB;
            bool rebuilt = _stagingSnapshot.TryRebuild(completed, in _frozenLease.Metadata);
            if (FluidSnapshotPublishPolicy.ShouldPublish(hasError, rebuilt))
            {
                // 只交换已经完整 Binning 的对象；失败路径完全保留旧 data/bounds/version。
                LiquidGameplaySnapshot previous = _publishedSnapshot;
                _publishedSnapshot = _stagingSnapshot;
                _stagingSnapshot = previous;
                _writeBufferIndex ^= 1;
            }
        }

        private void EnsureStorage(int particleCapacity)
        {
            if (_readbackA.IsCreated)
                return;

            _readbackA = new NativeArray<Vector4>(particleCapacity, Allocator.Persistent);
            _readbackB = new NativeArray<Vector4>(particleCapacity, Allocator.Persistent);
            _publishedSnapshot = new LiquidGameplaySnapshot(particleCapacity);
            _stagingSnapshot = new LiquidGameplaySnapshot(particleCapacity);
        }

        private void ReleaseSourceLeaseOnce()
        {
            if (!_requestState.TryReleaseLease())
                return;
            _source?.ReleaseGameplayReadbackLease();
        }

        private void DisposeReadbackArrays()
        {
            if (_readbackA.IsCreated)
                _readbackA.Dispose();
            if (_readbackB.IsCreated)
                _readbackB.Dispose();
        }

        private void ReportReadbackErrorRateLimited()
        {
            // 1,2,4,8... 次才报告：持续错误仍可见，但不会每 0.25 秒刷屏。
            if (_readbackErrorCount != 1
                && (_readbackErrorCount & (_readbackErrorCount - 1)) != 0)
            {
                return;
            }

            _lastReportedErrorCount = _readbackErrorCount;
            GameLog.Warn(
                $"GPU Water Gameplay Readback 失败 {_lastReportedErrorCount} 次；继续保留上一份有效 Snapshot。",
                "ElementField");
        }

        private void OnValidate()
        {
            _readbackInterval = FluidReadbackIntervalPolicy.Sanitize(_readbackInterval);
        }
    }
}

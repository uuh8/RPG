using Game.Core;
using Game.ElementField;
using Game.Materials;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// 为 RAM Dormant Cell 提供低成本静态代理。它只在 Archive Version 改变时重建矩阵，
    /// 正常帧复用数组并提交 Instanced Draw；GPU PBF 恢复后 Archive 记录移除，代理随版本消失。
    /// </summary>
    public sealed class DormantLiquidCellRenderer : MonoBehaviour
    {
        private const int MaximumInstancesPerDraw = 1023;

        [SerializeField] private ElementWorldRuntime _worldRuntime;
        [SerializeField] private Material _sourceLiquidMaterial;
        [SerializeField] private Shader _dormantShader;
        [SerializeField] private MaterialId _targetMaterial = MaterialId.Water;
        [SerializeField, Range(0f, 1f)] private float _minimumHeightRatio = .08f;

        private IFluidDormantReadOnly _source;
        private LiquidMaterialCellSample[] _samples;
        private Matrix4x4[] _matrices;
        private Mesh _mesh;
        private Material _runtimeMaterial;
        private RenderParams _renderParams;
        private uint _lastVersion = uint.MaxValue;
        private int _instanceCount;

        private void Start()
        {
            if (_worldRuntime == null || _sourceLiquidMaterial == null
                || _targetMaterial == MaterialId.Empty)
            {
                GameLog.Error("DormantLiquidCellRenderer 缺少 World、Material 或 MaterialId。", "Rendering");
                enabled = false;
                return;
            }
            _source = _worldRuntime.DormantLiquids;
            if (_source == null || _source.MaximumDormantCellRecords <= 0)
            {
                enabled = false;
                return;
            }
            if (_dormantShader == null)
            {
                GameLog.Error("Dormant Liquid Shader 未绑定，无法创建静态代理材质。", "Rendering");
                enabled = false;
                return;
            }

            // 数组、Mesh、Material 都在初始化边界创建；Update 不允许为稳定残留制造 GC Alloc。
            _samples = new LiquidMaterialCellSample[_source.MaximumDormantCellRecords];
            _matrices = new Matrix4x4[_source.MaximumDormantCellRecords];
            _mesh = CreateUnitCube();
            _runtimeMaterial = new Material(_dormantShader) { enableInstancing = true };
            _runtimeMaterial.CopyPropertiesFromMaterial(_sourceLiquidMaterial);
            _renderParams = new RenderParams(_runtimeMaterial)
            {
                layer = gameObject.layer,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                worldBounds = _worldRuntime.GetActiveWorldBounds()
            };
        }

        private void Update()
        {
            if (_source == null) return;
            if (_lastVersion != _source.DormantVersion)
                RebuildInstances();
            if (_instanceCount <= 0) return;
            _renderParams.worldBounds = _worldRuntime.GetActiveWorldBounds();
            // Unity 对单次 RenderMeshInstanced 提交设有 1023 个实例上限；分批仍复用同一矩阵数组，
            // 所以大量 Dormant Cell 不会抛异常，也不会在每帧创建临时集合。
            for (int start = 0; start < _instanceCount; start += MaximumInstancesPerDraw)
            {
                int count = Mathf.Min(MaximumInstancesPerDraw, _instanceCount - start);
                Graphics.RenderMeshInstanced(
                    in _renderParams, _mesh, 0, _matrices, count, start);
            }
        }

        private void RebuildInstances()
        {
            _lastVersion = _source.DormantVersion;
            _instanceCount = _source.CopyDormantCells(_targetMaterial, _samples);
            for (int i = 0; i < _instanceCount; i++)
            {
                LiquidMaterialCellSample sample = _samples[i];
                _matrices[i] = DormantLiquidRenderPolicy.CreateCellMatrix(
                    sample.GlobalCell, _worldRuntime.Origin, _worldRuntime.CellSize,
                    sample.Amount, _minimumHeightRatio);
            }
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_runtimeMaterial != null) Destroy(_runtimeMaterial);
        }

        private static Mesh CreateUnitCube()
        {
            var mesh = new Mesh { name = "DormantLiquidCellCube" };
            mesh.vertices = new[]
            {
                new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f),
                new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f),
                new Vector3(-.5f,-.5f,.5f), new Vector3(.5f,-.5f,.5f),
                new Vector3(.5f,.5f,.5f), new Vector3(-.5f,.5f,.5f)
            };
            mesh.triangles = new[]
            {
                0,2,1,0,3,2, 1,2,6,1,6,5, 5,6,7,5,7,4,
                4,7,3,4,3,0, 3,7,6,3,6,2, 4,0,1,4,1,5
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

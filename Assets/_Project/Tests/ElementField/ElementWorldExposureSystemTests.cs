using Game.Materials;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 使用真实稀疏 World Runtime、Physics Collider 与 StatusController 验证 Exposure Adapter。
    /// 这里刻意不 Mock Physics，因为跨 Chunk Bounds、父级 Status 查找和多 Collider 去重都是集成风险。
    /// </summary>
    public sealed class ElementWorldExposureSystemTests
    {
        private const float ExposureInterval = 0.25f;
        private const float MaxApplyPerTick = 10f;

        private GameObject _worldRoot;
        private GameObject _interestObject;
        private ElementWorldRuntime _runtime;
        private ElementWorldExposureSystem _exposure;
        private ElementWorldProfile _profile;
        private ElementReactionProfile _reactionProfile;
        private MaterialDefinition[] _materialDefinitions;
        private MaterialCatalog _materialCatalog;
        private MaterialSimulationRoutingProfile _routingProfile;
        private MaterialStatusProjectionProfile _statusProjection;
        private readonly List<GameObject> _createdTargets = new List<GameObject>(4);

        [SetUp]
        public void SetUp()
        {
            _reactionProfile = ScriptableObject.CreateInstance<ElementReactionProfile>();
            CreateMaterialRoutingFixture();
            _profile = ScriptableObject.CreateInstance<ElementWorldProfile>();
            SetPrivateField(_profile, "_cellSize", 1f);
            SetPrivateField(_profile, "_chunkSize", 2);
            SetPrivateField(_profile, "_maximumResidentChunks", 32);
            SetPrivateField(_profile, "_activeRadiusXZChunks", 1);
            SetPrivateField(_profile, "_activeRadiusYChunks", 1);
            SetPrivateField(_profile, "_sleepGraceSeconds", 0f);
            SetPrivateField(_profile, "_tickRate", 10f);
            SetPrivateField(_profile, "_maxPendingWrites", 16);
            SetPrivateField(_profile, "_maxCatchUpTicks", 1);
            SetPrivateField(_profile, "_maxDownFlowPerTick", 0);
            SetPrivateField(_profile, "_maxLateralFlowPerTick", 0);
            SetPrivateField(_profile, "_fireDecayPerTick", 0);
            SetPrivateField(_profile, "_reactionProfile", _reactionProfile);
            SetPrivateField(_profile, "_materialCatalog", _materialCatalog);
            SetPrivateField(_profile, "_materialSimulationRouting", _routingProfile);
            _statusProjection = ScriptableObject.CreateInstance<MaterialStatusProjectionProfile>();
            SetPrivateField(
                _statusProjection,
                "_bindings",
                new[]
                {
                    new MaterialStatusProjectionBinding
                    {
                        Material = MaterialId.Water,
                        Status = StatusKind.Wet,
                        MaximumApplyPerExposureTick = MaxApplyPerTick,
                    },
                    new MaterialStatusProjectionBinding
                    {
                        Material = MaterialId.Fire,
                        Status = StatusKind.Burning,
                        MaximumApplyPerExposureTick = MaxApplyPerTick,
                    },
                });
            SetPrivateField(_profile, "_materialStatusProjection", _statusProjection);
            SetPrivateField(
                _profile,
                "_materialReactionBindings",
                AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>(
                    "Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset"));

            _interestObject = new GameObject("ElementWorldExposure.Interest");
            _interestObject.transform.position = CellCenter(0);

            _worldRoot = new GameObject("ElementWorldExposure.World");
            _worldRoot.SetActive(false);
            _runtime = _worldRoot.AddComponent<ElementWorldRuntime>();
            _exposure = _worldRoot.AddComponent<ElementWorldExposureSystem>();
            SetPrivateField(_runtime, "_profile", _profile);
            SetPrivateField(_runtime, "_interestPoint", _interestObject.transform);
            SetPrivateField(_exposure, "_targetLayers", (LayerMask)~0);
            SetPrivateField(_exposure, "_exposureInterval", ExposureInterval);
            SetPrivateField(_exposure, "_naturalDecayHoldGraceSeconds", 0.1f);

            Assert.That(InvokePrivateResult<bool>(_runtime, "TryInitialize"), Is.True);
            InvokePrivateVoid(_exposure, "Awake");
            _worldRoot.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _createdTargets.Count; i++)
            {
                if (_createdTargets[i] != null)
                    Object.DestroyImmediate(_createdTargets[i]);
            }
            _createdTargets.Clear();

            if (_worldRoot != null)
                Object.DestroyImmediate(_worldRoot);
            if (_interestObject != null)
                Object.DestroyImmediate(_interestObject);
            if (_profile != null)
                Object.DestroyImmediate(_profile);
            if (_reactionProfile != null)
                Object.DestroyImmediate(_reactionProfile);
            if (_routingProfile != null)
                Object.DestroyImmediate(_routingProfile);
            if (_statusProjection != null)
                Object.DestroyImmediate(_statusProjection);
            if (_materialCatalog != null)
                Object.DestroyImmediate(_materialCatalog);
            if (_materialDefinitions != null)
            {
                for (int i = 0; i < _materialDefinitions.Length; i++)
                    Object.DestroyImmediate(_materialDefinitions[i]);
            }
        }

        [Test]
        public void WaterCellAppliesWetProportionalToAmount()
        {
            Deposit(0, MaterialId.Water, amount: 128);
            StatusController target = CreateTarget(CellCenter(0), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 128f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(expected).Within(0.0001f));
            Assert.That(target.GetIntensity(StatusKind.Burning), Is.Zero);
        }

        [Test]
        public void SettledSleepingWaterStillAppliesWetInsideInterestRegion()
        {
            Deposit(0, MaterialId.Water, amount: 128);

            // 写入 Tick 之后再推进两个无变化 Tick，使该非空 Chunk 达到默认 Settlement Hysteresis。
            Assert.That(_runtime.TickForTests(0.1f), Is.EqualTo(1));
            Assert.That(_runtime.TickForTests(0.1f), Is.EqualTo(1));
            StatusController target = CreateTarget(CellCenter(0), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 128f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(expected).Within(0.0001f),
                "Solver-Sleeping 只表示水已稳定，不代表 Water Cell 从 Gameplay 世界消失。");
        }

        [Test]
        public void FireCellAppliesBurningProportionalToAmount()
        {
            Deposit(1, MaterialId.Fire, amount: 64);
            StatusController target = CreateTarget(CellCenter(1), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 64f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Burning), Is.EqualTo(expected).Within(0.0001f));
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.Zero);
        }

        [Test]
        public void ColliderCrossingChunkBoundaryUsesMaximumIntensity()
        {
            Deposit(1, MaterialId.Water, amount: 64);
            Deposit(2, MaterialId.Water, amount: 200);
            StatusController target = CreateTarget(
                new Vector3(2f, 0.5f, 0.5f),
                new Vector3(1.8f, 0.4f, 0.4f));

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 200f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(expected).Within(0.0001f),
                "Collider 横跨 Chunk(0) Local X=1 与 Chunk(1) Local X=0 时必须读取最大 Water Amount。");
        }

        [Test]
        public void MultipleCollidersApplyOnlyOncePerStatusTarget()
        {
            Deposit(0, MaterialId.Water, byte.MaxValue);
            GameObject targetRoot = new GameObject("WorldExposureTarget.MultiCollider");
            _createdTargets.Add(targetRoot);
            targetRoot.transform.position = CellCenter(0);
            StatusController target = targetRoot.AddComponent<StatusController>();
            CreateChildCollider(targetRoot.transform, new Vector3(-0.1f, 0f, 0f));
            CreateChildCollider(targetRoot.transform, new Vector3(0.1f, 0f, 0f));
            Physics.SyncTransforms();

            _exposure.TickForTests(ExposureInterval);

            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(MaxApplyPerTick).Within(0.0001f));
        }

        [Test]
        public void TargetOutsideActiveWorldRegionIsIgnored()
        {
            Deposit(8, MaterialId.Water, byte.MaxValue);
            StatusController target = CreateTarget(CellCenter(8), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            Assert.That(target.GetIntensity(StatusKind.Wet), Is.Zero,
                "远离 Interest Region 的 Collider 即使所在 Sleeping Chunk 保留 Water，也不应继续接受 Exposure。");
        }

        private void Deposit(int globalX, MaterialId kind, byte amount)
        {
            var request = new ElementWriteRequest(
                CellCenter(globalX),
                kind,
                amount,
                radius: 0f,
                useLinearFalloff: false);
            Assert.That(_runtime.TryEnqueueWrite(in request), Is.True);
            Assert.That(_runtime.TickForTests(0.1f), Is.EqualTo(1));
        }

        private StatusController CreateTarget(Vector3 position, Vector3 colliderSize)
        {
            GameObject target = new GameObject("WorldExposureTarget");
            _createdTargets.Add(target);
            target.transform.position = position;
            StatusController controller = target.AddComponent<StatusController>();
            BoxCollider collider = target.AddComponent<BoxCollider>();
            collider.size = colliderSize;
            Physics.SyncTransforms();
            return controller;
        }

        private static void CreateChildCollider(Transform parent, Vector3 localPosition)
        {
            GameObject child = new GameObject("WorldExposureTarget.Collider");
            child.transform.SetParent(parent, false);
            child.transform.localPosition = localPosition;
            BoxCollider collider = child.AddComponent<BoxCollider>();
            collider.size = Vector3.one * 0.3f;
        }

        private static Vector3 CellCenter(int x)
        {
            return new Vector3(x + 0.5f, 0.5f, 0.5f);
        }

        private void CreateMaterialRoutingFixture()
        {
            _materialDefinitions = new[]
            {
                CreateDefinition(MaterialId.Water, MaterialBehaviorKind.Liquid),
                CreateDefinition(MaterialId.Fire, MaterialBehaviorKind.ReactiveField),
                CreateDefinition(MaterialId.Poison, MaterialBehaviorKind.Liquid),
            };
            _materialCatalog = ScriptableObject.CreateInstance<MaterialCatalog>();
            SetPrivateField(_materialCatalog, "_definitions", _materialDefinitions);

            _routingProfile = ScriptableObject.CreateInstance<MaterialSimulationRoutingProfile>();
            var serialized = new SerializedObject(_routingProfile);
            SerializedProperty routes = serialized.FindProperty("_routes");
            routes.arraySize = 2;
            SetRoute(routes.GetArrayElementAtIndex(0), MaterialId.Fire, MaterialSimulationBackendKind.ElementCell);
            SetRoute(routes.GetArrayElementAtIndex(1), MaterialId.Poison, MaterialSimulationBackendKind.Unsupported);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static MaterialDefinition CreateDefinition(MaterialId id, MaterialBehaviorKind behavior)
        {
            MaterialDefinition definition = ScriptableObject.CreateInstance<MaterialDefinition>();
            SetPrivateField(definition, "_id", id);
            SetPrivateField(definition, "_behavior", behavior);
            return definition;
        }

        private static void SetRoute(
            SerializedProperty route,
            MaterialId material,
            MaterialSimulationBackendKind backend)
        {
            route.FindPropertyRelative("_material").intValue = (byte)material;
            route.FindPropertyRelative("_backend").intValue = (byte)backend;
        }

        private static void SetPrivateField<TTarget, TValue>(
            TTarget target,
            string fieldName,
            TValue value)
        {
            FieldInfo field = typeof(TTarget).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少预期字段 {typeof(TTarget).Name}.{fieldName}。");
            field.SetValue(target, value);
        }

        private static void InvokePrivateVoid<TTarget>(TTarget target, string methodName)
        {
            MethodInfo method = typeof(TTarget).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"缺少预期生命周期方法 {typeof(TTarget).Name}.{methodName}。");
            method.Invoke(target, null);
        }

        private static TResult InvokePrivateResult<TResult>(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"缺少预期初始化方法 {target.GetType().Name}.{methodName}。");
            return (TResult)method.Invoke(target, null);
        }
    }
}

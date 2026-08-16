using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 这是有真实 Graphics Device 时才执行的 GPU 数值测试，不以源码 grep 或 C# 编译冒充 Dispatch。
    /// 注意：GraphicsBuffer.GetData 会同步等待 GPU，只允许用于 Test-only 验证；严禁复制到 Runtime 热路径。
    /// </summary>
    public sealed class GpuFluidParticleLifecycleTests
    {
        private const string LifecycleShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute";
        private const int ThreadGroupSize = 64;

        [Test]
        public void InitializePartialSpawnAndOverflowKeepBothActivityFlagsAndCountersInSync()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(LifecycleShaderPath);
            Assert.That(shader, Is.Not.Null, $"Missing ComputeShader at {LifecycleShaderPath}.");

            var resources = new FluidGpuResourceSet(8, 16, 4);
            try
            {
                DispatchInitialize(shader, resources);

                var initializedPositions = new Vector4[8];
                var initializedMetadata = new FluidGpuUInt2[8];
                var counters = new uint[FluidGpuLayout.CounterCount];
                resources.Positions.GetData(initializedPositions);
                resources.Metadata.GetData(initializedMetadata);
                resources.Counters.GetData(counters);
                Assert.That(CountActive(initializedPositions), Is.Zero);
                AssertActivityStateIsConsistent(initializedPositions, initializedMetadata, expectedActiveCount: 0);
                Assert.That(counters[FluidGpuLayout.FreeCountCounterIndex], Is.EqualTo(8u));
                Assert.That(counters[FluidGpuLayout.ActiveCountCounterIndex], Is.Zero);

                var partialRequest = new FluidSpawnRequest(
                    worldPosition: new Vector3(2f, 3f, 4f),
                    initialVelocity: Vector3.zero,
                    radius: 0.5f,
                    particleCount: 3u,
                    materialId: 7u,
                    seed: 12345u,
                    flags: FluidSpawnFlags.UseLinearFalloff);
                resources.SpawnRequests.SetData(new[] { new FluidGpuSpawnRequest(in partialRequest) });
                DispatchSpawn(shader, resources, requestCount: 1);

                var spawnedPositions = new Vector4[8];
                var metadata = new FluidGpuUInt2[8];
                resources.Positions.GetData(spawnedPositions);
                resources.Metadata.GetData(metadata);
                resources.Counters.GetData(counters);

                Assert.That(CountActive(spawnedPositions), Is.EqualTo(3));
                AssertActivityStateIsConsistent(spawnedPositions, metadata, expectedActiveCount: 3);
                Assert.That(counters[FluidGpuLayout.FreeCountCounterIndex], Is.EqualTo(5u));
                Assert.That(counters[FluidGpuLayout.ActiveCountCounterIndex], Is.EqualTo(3u));
                Assert.That(counters[FluidGpuLayout.DroppedParticleCountCounterIndex], Is.Zero);
                for (int i = 0; i < spawnedPositions.Length; i++)
                {
                    if (spawnedPositions[i].w > 0.5f)
                    {
                        Assert.That(metadata[i].X, Is.EqualTo(7u));
                        Vector3 offset = (Vector3)spawnedPositions[i] - partialRequest.WorldPosition;
                        Assert.That(offset.magnitude, Is.LessThanOrEqualTo(partialRequest.Radius + 0.0001f));
                    }
                }

                var overflowRequest = new FluidSpawnRequest(
                    worldPosition: Vector3.zero,
                    initialVelocity: Vector3.zero,
                    radius: 0.25f,
                    particleCount: 10u,
                    materialId: 9u,
                    seed: 67890u,
                    flags: 0u);
                resources.SpawnRequests.SetData(new[] { new FluidGpuSpawnRequest(in overflowRequest) });
                DispatchSpawn(shader, resources, requestCount: 1);

                resources.Positions.GetData(spawnedPositions);
                resources.Metadata.GetData(metadata);
                resources.Counters.GetData(counters);
                Assert.That(CountActive(spawnedPositions), Is.EqualTo(8));
                AssertActivityStateIsConsistent(spawnedPositions, metadata, expectedActiveCount: 8);
                Assert.That(counters[FluidGpuLayout.FreeCountCounterIndex], Is.Zero);
                Assert.That(counters[FluidGpuLayout.ActiveCountCounterIndex], Is.EqualTo(8u));
                Assert.That(counters[FluidGpuLayout.DroppedParticleCountCounterIndex], Is.EqualTo(5u));

                DispatchMinimumTick(shader, resources, deltaTime: 0.1f);
                var committedPositions = new Vector4[8];
                resources.Positions.GetData(committedPositions);
                for (int i = 0; i < committedPositions.Length; i++)
                {
                    Assert.That(
                        committedPositions[i].y - spawnedPositions[i].y,
                        Is.EqualTo(-0.0981f).Within(0.0002f));
                }
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void HugeDirectGpuSpawnRequestBoundsWorkAndAggregatesDroppedCount()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(LifecycleShaderPath);
            Assert.That(shader, Is.Not.Null, $"Missing ComputeShader at {LifecycleShaderPath}.");

            var resources = new FluidGpuResourceSet(8, 16, 1);
            try
            {
                DispatchInitialize(shader, resources);
                var maliciousRequest = new FluidSpawnRequest(
                    worldPosition: Vector3.zero,
                    initialVelocity: Vector3.zero,
                    radius: 0.5f,
                    particleCount: uint.MaxValue,
                    materialId: 3u,
                    seed: 42u,
                    flags: 0u);
                resources.SpawnRequests.SetData(new[] { new FluidGpuSpawnRequest(in maliciousRequest) });
                DispatchSpawn(shader, resources, requestCount: 1);

                var positions = new Vector4[8];
                var metadata = new FluidGpuUInt2[8];
                var counters = new uint[FluidGpuLayout.CounterCount];
                resources.Positions.GetData(positions);
                resources.Metadata.GetData(metadata);
                resources.Counters.GetData(counters);

                Assert.That(CountActive(positions), Is.EqualTo(8));
                AssertActivityStateIsConsistent(positions, metadata, expectedActiveCount: 8);
                Assert.That(counters[FluidGpuLayout.ActiveCountCounterIndex], Is.EqualTo(8u));
                Assert.That(
                    counters[FluidGpuLayout.DroppedParticleCountCounterIndex],
                    Is.EqualTo(uint.MaxValue - 8u));
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void LinearFalloffAndUniformVolumeUseTheirExpectedMeanNormalizedRadius()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(LifecycleShaderPath);
            Assert.That(shader, Is.Not.Null, $"Missing ComputeShader at {LifecycleShaderPath}.");

            const int sampleCount = 1024;
            var resources = new FluidGpuResourceSet(sampleCount, sampleCount * 2, 1);
            try
            {
                float uniformMean = SpawnAndMeasureMeanRadius(
                    shader,
                    resources,
                    sampleCount,
                    flags: 0u);
                float linearMean = SpawnAndMeasureMeanRadius(
                    shader,
                    resources,
                    sampleCount,
                    flags: FluidSpawnFlags.UseLinearFalloff);

                Assert.That(uniformMean, Is.EqualTo(0.75f).Within(0.035f));
                Assert.That(linearMean, Is.EqualTo(0.60f).Within(0.035f));
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void ConsumeParticles_ReleasesOnlyMatchingMaterialInsideGlobalCellAndRestoresFreeStack()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(LifecycleShaderPath);
            Assert.That(shader, Is.Not.Null);
            var resources = new FluidGpuResourceSet(8, 16, 4);
            try
            {
                DispatchInitialize(shader, resources);
                var water = new FluidSpawnRequest(
                    new Vector3(0.25f, 0.25f, 0.25f), Vector3.zero, 0f, 4u,
                    (uint)ElementMaterialKind.Water, 1u, 0u);
                var fire = new FluidSpawnRequest(
                    new Vector3(0.25f, 0.25f, 0.25f), Vector3.zero, 0f, 2u,
                    (uint)ElementMaterialKind.Fire, 2u, 0u);
                resources.SpawnRequests.SetData(new[]
                {
                    new FluidGpuSpawnRequest(in water),
                    new FluidGpuSpawnRequest(in fire),
                });
                DispatchSpawn(shader, resources, requestCount: 2);

                resources.ConsumeRequests.SetData(new[]
                {
                    new FluidConsumeCommand(
                        Vector3Int.zero,
                        Vector3Int.zero,
                        (uint)ElementMaterialKind.Water,
                        2u,
                        17u),
                });
                DispatchConsume(shader, resources, 1, Vector3.zero, 1f);

                var metadata = new FluidGpuUInt2[8];
                var counters = new uint[FluidGpuLayout.CounterCount];
                resources.Metadata.GetData(metadata);
                resources.Counters.GetData(counters);
                int waterCount = 0;
                int fireCount = 0;
                for (int index = 0; index < metadata.Length; index++)
                {
                    if ((metadata[index].Y & FluidGpuLayout.ActiveFlag) == 0u)
                        continue;
                    if (metadata[index].X == (uint)ElementMaterialKind.Water)
                        waterCount++;
                    if (metadata[index].X == (uint)ElementMaterialKind.Fire)
                        fireCount++;
                }
                Assert.That(waterCount, Is.EqualTo(2));
                Assert.That(fireCount, Is.EqualTo(2));
                Assert.That(counters[FluidGpuLayout.ActiveCountCounterIndex], Is.EqualTo(4u));
                Assert.That(counters[FluidGpuLayout.FreeCountCounterIndex], Is.EqualTo(4u));
            }
            finally
            {
                resources.Dispose();
            }
        }

        [Test]
        public void Activity_HighSparseSlotSleepsRemainsResidentAndWakesWithFullCapacityArgs()
        {
            IgnoreWithoutComputeSupport();
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(LifecycleShaderPath);
            Assert.That(shader, Is.Not.Null);
            var resources = new FluidGpuResourceSet(65, 128, 1);
            try
            {
                DispatchInitialize(shader, resources);
                DispatchInitializeActivity(shader, resources);
                var positions = new Vector4[65];
                var velocities = new Vector4[65];
                var densities = new Vector2[65];
                var metadata = new FluidGpuUInt2[65];
                var stableTicks = new uint[65];
                positions[64] = new Vector4(1f, 1f, 1f, 1f);
                densities[64] = new Vector2(1000f, 0f);
                metadata[64] = new FluidGpuUInt2(1u, FluidGpuLayout.ActiveFlag);
                stableTicks[64] = 2u;
                resources.Positions.SetData(positions);
                resources.Velocities.SetData(velocities);
                resources.DensityLambda.SetData(densities);
                resources.Metadata.SetData(metadata);
                resources.StableTickCounters.SetData(stableTicks);

                DispatchActivity(shader, resources, wakeAll: false, stableThreshold: 3);
                resources.Metadata.GetData(metadata);
                var args = new uint[3];
                resources.SolverDispatchArgs.GetData(args);
                Assert.That(FluidActivityFlags.IsSleeping(metadata[64].Y), Is.True);
                Assert.That(FluidActivityFlags.ContributesToSurface(metadata[64].Y), Is.True);
                Assert.That(args[0], Is.Zero);

                var wake = new uint[65];
                wake[64] = 1u;
                resources.WakeRequests.SetData(wake);
                DispatchActivity(shader, resources, wakeAll: false, stableThreshold: 3);
                resources.Metadata.GetData(metadata);
                resources.SolverDispatchArgs.GetData(args);
                Assert.That(FluidActivityFlags.RequiresSolver(metadata[64].Y), Is.True);
                Assert.That(args[0], Is.EqualTo(2u),
                    "slot64 醒着时必须覆盖 65 capacity，而不是按 AwakeCount=1 只发一组。");
            }
            finally
            {
                resources.Dispose();
            }
        }

        private static void DispatchInitialize(ComputeShader shader, FluidGpuResourceSet resources)
        {
            int kernel = shader.FindKernel("InitializePool");
            BindInitializePool(shader, kernel, resources);
            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.Dispatch(
                kernel,
                DivideRoundUp(resources.ParticleCapacity),
                1,
                1);

            int auxiliaryKernel = shader.FindKernel("InitializePoolAuxiliary");
            BindInitializePoolAuxiliary(shader, auxiliaryKernel, resources);
            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", resources.HashTableCapacity);
            shader.Dispatch(
                auxiliaryKernel,
                DivideRoundUp(resources.HashTableCapacity),
                1,
                1);
        }

        private static void DispatchInitializeActivity(
            ComputeShader shader,
            FluidGpuResourceSet resources)
        {
            int kernel = shader.FindKernel("InitializeActivity");
            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetBuffer(kernel, "_StableTickCounters", resources.StableTickCounters);
            shader.SetBuffer(kernel, "_WakeRequests", resources.WakeRequests);
            shader.SetBuffer(kernel, "_ActivityCounters", resources.ActivityCounters);
            shader.SetBuffer(kernel, "_SolverDispatchArgs", resources.SolverDispatchArgs);
            shader.SetBuffer(kernel, "_HashDispatchArgs", resources.HashDispatchArgs);
            shader.Dispatch(kernel, DivideRoundUp(resources.ParticleCapacity), 1, 1);
        }

        private static void DispatchActivity(
            ComputeShader shader,
            FluidGpuResourceSet resources,
            bool wakeAll,
            int stableThreshold)
        {
            int clear = shader.FindKernel("ClearActivityCounters");
            int update = shader.FindKernel("UpdateParticleActivity");
            int args = shader.FindKernel("WriteSolverDispatchArgs");
            shader.SetBuffer(clear, "_ActivityCounters", resources.ActivityCounters);
            shader.Dispatch(clear, 1, 1, 1);
            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetInt("_ParticleGroupCount", DivideRoundUp(resources.ParticleCapacity));
            shader.SetInt("_HashTableGroupCount", DivideRoundUp(resources.HashTableCapacity));
            shader.SetInt("_SleepAfterStableTicks", stableThreshold);
            shader.SetInt("_WakeAllInterestParticles", wakeAll ? 1 : 0);
            shader.SetFloat("_SleepVelocityThreshold", 0.01f);
            shader.SetFloat("_SleepDensityErrorThreshold", 0.02f);
            shader.SetFloat("_RestDensity", 1000f);
            shader.SetVector("_ActiveBoundsMin", Vector3.zero);
            shader.SetVector("_ActiveBoundsMax", Vector3.one * 10f);
            shader.SetBuffer(update, "_Positions", resources.Positions);
            shader.SetBuffer(update, "_Velocities", resources.Velocities);
            shader.SetBuffer(update, "_DensityLambda", resources.DensityLambda);
            shader.SetBuffer(update, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(update, "_StableTickCounters", resources.StableTickCounters);
            shader.SetBuffer(update, "_WakeRequests", resources.WakeRequests);
            shader.SetBuffer(update, "_ActivityCounters", resources.ActivityCounters);
            shader.Dispatch(update, DivideRoundUp(resources.ParticleCapacity), 1, 1);
            shader.SetBuffer(args, "_ActivityCounters", resources.ActivityCounters);
            shader.SetBuffer(args, "_SolverDispatchArgs", resources.SolverDispatchArgs);
            shader.SetBuffer(args, "_HashDispatchArgs", resources.HashDispatchArgs);
            shader.Dispatch(args, 1, 1, 1);
        }

        private static void DispatchSpawn(
            ComputeShader shader,
            FluidGpuResourceSet resources,
            int requestCount)
        {
            int kernel = shader.FindKernel("SpawnParticles");
            BindSpawnParticles(shader, kernel, resources);
            shader.SetInt("_SpawnRequestCount", requestCount);
            shader.Dispatch(kernel, DivideRoundUp(requestCount), 1, 1);
        }

        private static void DispatchMinimumTick(
            ComputeShader shader,
            FluidGpuResourceSet resources,
            float deltaTime)
        {
            int gravityKernel = shader.FindKernel("ApplyGravity");
            int predictKernel = shader.FindKernel("PredictPositions");
            int commitKernel = shader.FindKernel("CommitPositions");
            BindApplyGravity(shader, gravityKernel, resources);
            BindPredictPositions(shader, predictKernel, resources);
            BindCommitPositions(shader, commitKernel, resources);
            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetFloat("_DeltaTime", deltaTime);
            shader.SetFloat("_MaxSpeed", 100f);
            shader.SetVector("_Gravity", new Vector4(0f, -9.81f, 0f, 0f));
            int groups = DivideRoundUp(resources.ParticleCapacity);
            shader.Dispatch(gravityKernel, groups, 1, 1);
            shader.Dispatch(predictKernel, groups, 1, 1);
            shader.Dispatch(commitKernel, groups, 1, 1);
        }

        private static void DispatchConsume(
            ComputeShader shader,
            FluidGpuResourceSet resources,
            int commandCount,
            Vector3 worldOrigin,
            float cellSize)
        {
            int kernel = shader.FindKernel("ConsumeParticles");
            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetInt("_ReactionCommandCount", commandCount);
            shader.SetVector("_WorldOrigin", worldOrigin);
            shader.SetFloat("_CellSize", cellSize);
            shader.SetBuffer(kernel, "_Positions", resources.Positions);
            shader.SetBuffer(kernel, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(kernel, "_Velocities", resources.Velocities);
            shader.SetBuffer(kernel, "_DensityLambda", resources.DensityLambda);
            shader.SetBuffer(kernel, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(kernel, "_DeltaPositions", resources.DeltaPositions);
            shader.SetBuffer(kernel, "_FreeIndices", resources.FreeIndices);
            shader.SetBuffer(kernel, "_Counters", resources.Counters);
            shader.SetBuffer(kernel, "_ConsumeRequests", resources.ConsumeRequests);
            shader.Dispatch(kernel, commandCount, 1, 1);
        }

        private static float SpawnAndMeasureMeanRadius(
            ComputeShader shader,
            FluidGpuResourceSet resources,
            int sampleCount,
            uint flags)
        {
            DispatchInitialize(shader, resources);
            var request = new FluidSpawnRequest(
                worldPosition: Vector3.zero,
                initialVelocity: Vector3.zero,
                radius: 1f,
                particleCount: (uint)sampleCount,
                materialId: 1u,
                seed: 13579u,
                flags: flags);
            resources.SpawnRequests.SetData(new[] { new FluidGpuSpawnRequest(in request) });
            DispatchSpawn(shader, resources, requestCount: 1);

            var positions = new Vector4[sampleCount];
            resources.Positions.GetData(positions);
            float normalizedRadiusSum = 0f;
            for (int i = 0; i < positions.Length; i++)
            {
                Assert.That(positions[i].w, Is.GreaterThan(0.5f));
                normalizedRadiusSum += ((Vector3)positions[i]).magnitude;
            }

            return normalizedRadiusSum / sampleCount;
        }

        private static void BindInitializePool(
            ComputeShader shader,
            int kernel,
            FluidGpuResourceSet resources)
        {
            shader.SetBuffer(kernel, "_Positions", resources.Positions);
            shader.SetBuffer(kernel, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(kernel, "_Velocities", resources.Velocities);
            shader.SetBuffer(kernel, "_DensityLambda", resources.DensityLambda);
            shader.SetBuffer(kernel, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(kernel, "_DeltaPositions", resources.DeltaPositions);
            shader.SetBuffer(kernel, "_SpatialEntries", resources.SpatialEntries);
            shader.SetBuffer(kernel, "_FreeIndices", resources.FreeIndices);
        }

        private static void BindInitializePoolAuxiliary(
            ComputeShader shader,
            int kernel,
            FluidGpuResourceSet resources)
        {
            shader.SetBuffer(kernel, "_CellRanges", resources.CellRanges);
            shader.SetBuffer(kernel, "_Counters", resources.Counters);
        }

        private static void BindSpawnParticles(
            ComputeShader shader,
            int kernel,
            FluidGpuResourceSet resources)
        {
            shader.SetBuffer(kernel, "_Positions", resources.Positions);
            shader.SetBuffer(kernel, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(kernel, "_Velocities", resources.Velocities);
            shader.SetBuffer(kernel, "_DensityLambda", resources.DensityLambda);
            shader.SetBuffer(kernel, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(kernel, "_DeltaPositions", resources.DeltaPositions);
            shader.SetBuffer(kernel, "_FreeIndices", resources.FreeIndices);
            shader.SetBuffer(kernel, "_Counters", resources.Counters);
            shader.SetBuffer(kernel, "_SpawnRequests", resources.SpawnRequests);
        }

        private static void BindApplyGravity(
            ComputeShader shader,
            int kernel,
            FluidGpuResourceSet resources)
        {
            shader.SetBuffer(kernel, "_Velocities", resources.Velocities);
            shader.SetBuffer(kernel, "_ParticleMetadata", resources.Metadata);
        }

        private static void BindPredictPositions(
            ComputeShader shader,
            int kernel,
            FluidGpuResourceSet resources)
        {
            shader.SetBuffer(kernel, "_Positions", resources.Positions);
            shader.SetBuffer(kernel, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(kernel, "_Velocities", resources.Velocities);
            shader.SetBuffer(kernel, "_ParticleMetadata", resources.Metadata);
        }

        private static void BindCommitPositions(
            ComputeShader shader,
            int kernel,
            FluidGpuResourceSet resources)
        {
            shader.SetBuffer(kernel, "_Positions", resources.Positions);
            shader.SetBuffer(kernel, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(kernel, "_ParticleMetadata", resources.Metadata);
        }

        private static int CountActive(Vector4[] positions)
        {
            int active = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                if (positions[i].w > 0.5f)
                    active++;
            }

            return active;
        }

        private static void AssertActivityStateIsConsistent(
            Vector4[] positions,
            FluidGpuUInt2[] metadata,
            int expectedActiveCount)
        {
            int positionActiveCount = 0;
            int metadataActiveCount = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                bool positionIsActive = positions[i].w > 0.5f;
                bool metadataIsActive = (metadata[i].Y & FluidGpuLayout.ActiveFlag) != 0u;
                Assert.That(metadataIsActive, Is.EqualTo(positionIsActive), $"Slot {i} activity flags diverged.");
                if (positionIsActive)
                    positionActiveCount++;
                if (metadataIsActive)
                    metadataActiveCount++;
            }

            Assert.That(positionActiveCount, Is.EqualTo(expectedActiveCount));
            Assert.That(metadataActiveCount, Is.EqualTo(expectedActiveCount));
        }

        private static int DivideRoundUp(int count)
        {
            return (count + ThreadGroupSize - 1) / ThreadGroupSize;
        }

        private static void IgnoreWithoutComputeSupport()
        {
            if (!SystemInfo.supportsComputeShaders
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("当前 Graphics Device 不支持 ComputeShader；GPU 数值测试被跳过，不能记为通过。");
            }
        }
    }
}

using System;
using Game.ElementField;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 真实 Compute + R32_SFloat Tex3D 数值 fixture。WaitForCompletion 只允许出现在 Test-only；
    /// Runtime Density Renderer 不得同步回读，否则会让 CPU 等待 GPU 并破坏流水线并行。
    /// </summary>
    public sealed class GpuFluidDensityTests
    {
        private const string DensityShaderPath =
            "Assets/_Project/Art/Elemental/Compute/FluidDensity.compute";
        private const string SpatialHashShaderPath =
            "Assets/_Project/Art/Elemental/Compute/PbfSpatialHash.compute";
        private const int ParticleCapacity = 4;
        private const int HashCapacity = 16;
        private const float SmoothingRadius = 1f;
        private const float ParticleMass = 0.25f;

        [Test]
        public void SurfaceResourcesUseRequiredGpuLayoutsAndReuseTranslationOnlyGrid()
        {
            FluidSurfaceGridSettings first = CreateGrid(new Vector3(-2f, -2f, -2f));
            IgnoreWithoutSurfaceSupport(in first);

            var resources = new FluidSurfaceGpuResources(in first, ParticleCapacity);
            try
            {
                Assert.That(resources.DensityTexture.dimension, Is.EqualTo(TextureDimension.Tex3D));
                Assert.That(resources.DensityTexture.graphicsFormat, Is.EqualTo(GraphicsFormat.R32_SFloat));
                Assert.That(resources.DensityTexture.enableRandomWrite, Is.True);
                Assert.That(resources.DensityTexture.volumeDepth, Is.EqualTo(first.Resolution.z));
                Assert.That(resources.TriangleBuffer.stride, Is.EqualTo(96));
                Assert.That(resources.TriangleCounter.stride, Is.EqualTo(sizeof(uint)));
                Assert.That(resources.OverflowCounter.stride, Is.EqualTo(sizeof(uint)));
                Assert.That(resources.IndirectArguments.stride,
                    Is.EqualTo(GraphicsBuffer.IndirectDrawArgs.size));
                Assert.That(resources.IndirectArguments.target,
                    Is.EqualTo(GraphicsBuffer.Target.IndirectArguments));
                Assert.That(resources.AnisotropyBuffer.stride, Is.EqualTo(64));
                Assert.That(resources.AnisotropyBuffer.count, Is.EqualTo(ParticleCapacity));

                RenderTexture originalTexture = resources.DensityTexture;
                GraphicsBuffer originalTriangles = resources.TriangleBuffer;
                FluidSurfaceGridSettings translated = CreateGrid(new Vector3(4f, -7f, 3f));
                Assert.That(resources.TryUpdateGrid(in translated), Is.True);
                Assert.That(resources.DensityTexture, Is.SameAs(originalTexture));
                Assert.That(resources.TriangleBuffer, Is.SameAs(originalTriangles));
                Assert.That(resources.GridSettings.WorldOrigin, Is.EqualTo(translated.WorldOrigin));
            }
            finally
            {
                resources.Dispose();
                resources.Dispose();
            }

            Assert.That(resources.DensityTexture, Is.Null);
            Assert.That(resources.TriangleBuffer, Is.Null);
            Assert.That(resources.IndirectArguments, Is.Null);
        }

        [Test]
        public void SingleParticleMatchesIndependentPoly6AtCenterAndIsZeroAtAndOutsideSupport()
        {
            FluidSurfaceGridSettings grid = CreateGrid(new Vector3(-2f, -2f, -2f));
            IgnoreWithoutSurfaceSupport(in grid);
            ComputeShader density = LoadShader(DensityShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in grid, ParticleCapacity))
            {
                UploadParticles(particles, new[] { new Vector3(0f, 0f, 0f) });
                BuildSpatialHash(spatialHash, particles);
                DispatchDensity(density, particles, surface, in grid);
                float[] values = ReadDensityTestOnly(surface.DensityTexture);

                float expectedCenter = ParticleMass * IndependentPoly6(0f, SmoothingRadius);
                Assert.That(Read(values, grid.Resolution, 4, 4, 4),
                    Is.EqualTo(expectedCenter).Within(expectedCenter * 1e-4f));
                Assert.That(Read(values, grid.Resolution, 5, 4, 4),
                    Is.LessThan(expectedCenter));
                Assert.That(Read(values, grid.Resolution, 6, 4, 4), Is.EqualTo(0f).Within(1e-7f));
                Assert.That(Read(values, grid.Resolution, 7, 4, 4), Is.EqualTo(0f).Within(1e-7f));
                AssertAllFinite(values);
            }
        }

        [Test]
        public void TwoParticlesAddAndNegativeTranslatedWorldCoordinatesPreserveLocalDensity()
        {
            FluidSurfaceGridSettings negativeGrid = CreateGrid(new Vector3(-4f, -4f, -4f));
            IgnoreWithoutSurfaceSupport(in negativeGrid);
            ComputeShader density = LoadShader(DensityShaderPath);
            ComputeShader spatialHash = LoadShader(SpatialHashShaderPath);

            using (var particles = new FluidGpuResourceSet(ParticleCapacity, HashCapacity, 1))
            using (var surface = new FluidSurfaceGpuResources(in negativeGrid, ParticleCapacity))
            {
                var negativeParticle = new Vector3(-2f, -2f, -2f);
                UploadParticles(particles, new[] { negativeParticle, negativeParticle });
                BuildSpatialHash(spatialHash, particles);
                DispatchDensity(density, particles, surface, in negativeGrid);
                float[] twoParticleValues = ReadDensityTestOnly(surface.DensityTexture);

                float singleExpected = ParticleMass * IndependentPoly6(0f, SmoothingRadius);
                Assert.That(Read(twoParticleValues, negativeGrid.Resolution, 4, 4, 4),
                    Is.EqualTo(singleExpected * 2f).Within(singleExpected * 2e-4f));

                FluidSurfaceGridSettings translatedGrid = CreateGrid(new Vector3(6f, 1f, -8f));
                Assert.That(surface.TryUpdateGrid(in translatedGrid), Is.True);
                var translatedParticle = translatedGrid.WorldOrigin + Vector3.one * 2f;
                UploadParticles(particles, new[] { translatedParticle });
                BuildSpatialHash(spatialHash, particles);
                DispatchDensity(density, particles, surface, in translatedGrid);
                float[] translatedValues = ReadDensityTestOnly(surface.DensityTexture);

                Assert.That(Read(translatedValues, translatedGrid.Resolution, 4, 4, 4),
                    Is.EqualTo(singleExpected).Within(singleExpected * 1e-4f));
                Assert.That(Read(translatedValues, translatedGrid.Resolution, 5, 4, 4),
                    Is.EqualTo(IndependentPoly6(0.5f, SmoothingRadius) * ParticleMass)
                        .Within(singleExpected * 1e-4f));
                AssertAllFinite(twoParticleValues);
                AssertAllFinite(translatedValues);
            }
        }

        private static FluidSurfaceGridSettings CreateGrid(Vector3 origin)
        {
            return new FluidSurfaceGridSettings(
                new Vector3Int(9, 9, 9),
                origin,
                Vector3.one * 0.5f,
                maximumTriangleCount: 32);
        }

        private static void UploadParticles(FluidGpuResourceSet particles, Vector3[] activePositions)
        {
            var positions = new Vector4[ParticleCapacity];
            var metadata = new FluidGpuUInt2[ParticleCapacity];
            for (int i = 0; i < activePositions.Length; i++)
            {
                positions[i] = new Vector4(
                    activePositions[i].x,
                    activePositions[i].y,
                    activePositions[i].z,
                    1f);
                metadata[i] = new FluidGpuUInt2(0u, FluidGpuLayout.ActiveFlag);
            }

            particles.PredictedPositions.SetData(positions);
            particles.Metadata.SetData(metadata);
        }

        private static void BuildSpatialHash(ComputeShader shader, FluidGpuResourceSet resources)
        {
            int build = shader.FindKernel("BuildSpatialEntries");
            int sort = shader.FindKernel("BitonicSort");
            int clear = shader.FindKernel("ClearCellRanges");
            int ranges = shader.FindKernel("BuildCellRanges");
            int particleGroups = DivideRoundUp(resources.ParticleCapacity, 64);
            int hashGroups = DivideRoundUp(resources.HashTableCapacity, 64);

            shader.SetInt("_ParticleCapacity", resources.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", resources.HashTableCapacity);
            shader.SetFloat("_SmoothingRadius", SmoothingRadius);
            shader.SetBuffer(build, "_PredictedPositions", resources.PredictedPositions);
            shader.SetBuffer(build, "_ParticleMetadata", resources.Metadata);
            shader.SetBuffer(build, "_SpatialCells", resources.SpatialCells);
            shader.SetBuffer(build, "_SpatialEntries", resources.SpatialEntries);
            shader.Dispatch(build, particleGroups, 1, 1);

            shader.SetBuffer(sort, "_SpatialEntries", resources.SpatialEntries);
            for (int stage = 2; stage <= resources.ParticleCapacity; stage <<= 1)
            {
                for (int pass = stage >> 1; pass > 0; pass >>= 1)
                {
                    shader.SetInt("_BitonicStage", stage);
                    shader.SetInt("_BitonicPass", pass);
                    shader.Dispatch(sort, particleGroups, 1, 1);
                }
            }

            shader.SetBuffer(clear, "_CellRanges", resources.CellRanges);
            shader.Dispatch(clear, hashGroups, 1, 1);
            shader.SetBuffer(ranges, "_SpatialEntries", resources.SpatialEntries);
            shader.SetBuffer(ranges, "_CellRanges", resources.CellRanges);
            shader.Dispatch(ranges, particleGroups, 1, 1);
        }

        private static void DispatchDensity(
            ComputeShader shader,
            FluidGpuResourceSet particles,
            FluidSurfaceGpuResources surface,
            in FluidSurfaceGridSettings grid)
        {
            int kernel = shader.FindKernel("GatherIsotropicDensity");
            shader.SetInt("_ParticleCapacity", particles.ParticleCapacity);
            shader.SetInt("_HashTableCapacity", particles.HashTableCapacity);
            shader.SetFloat("_SmoothingRadius", SmoothingRadius);
            shader.SetFloat("_ParticleMass", ParticleMass);
            shader.SetInt("_GridResolutionX", grid.Resolution.x);
            shader.SetInt("_GridResolutionY", grid.Resolution.y);
            shader.SetInt("_GridResolutionZ", grid.Resolution.z);
            shader.SetVector("_WorldOrigin", grid.WorldOrigin);
            shader.SetVector("_VoxelSize", grid.VoxelSize);
            shader.SetBuffer(kernel, "_PredictedPositions", particles.PredictedPositions);
            shader.SetBuffer(kernel, "_ParticleMetadata", particles.Metadata);
            shader.SetBuffer(kernel, "_SpatialEntries", particles.SpatialEntries);
            shader.SetBuffer(kernel, "_CellRanges", particles.CellRanges);
            shader.SetBuffer(kernel, "_SpatialCells", particles.SpatialCells);
            shader.SetTexture(kernel, "_DensityTexture", surface.DensityTexture);
            shader.Dispatch(
                kernel,
                DivideRoundUp(grid.Resolution.x, 4),
                DivideRoundUp(grid.Resolution.y, 4),
                DivideRoundUp(grid.Resolution.z, 4));
        }

        private static float[] ReadDensityTestOnly(RenderTexture texture)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False, "Test-only 3D density readback failed.");
            NativeArray<float> data = request.GetData<float>();
            return data.ToArray();
        }

        private static float Read(float[] values, Vector3Int resolution, int x, int y, int z)
        {
            return values[x + resolution.x * (y + resolution.y * z)];
        }

        private static float IndependentPoly6(float distance, float smoothingRadius)
        {
            if (distance < 0f || distance >= smoothingRadius)
                return 0f;
            double h2 = smoothingRadius * smoothingRadius;
            double difference = h2 - distance * distance;
            double h9 = Math.Pow(smoothingRadius, 9d);
            return (float)(315d / (64d * Math.PI * h9) * difference * difference * difference);
        }

        private static void AssertAllFinite(float[] values)
        {
            for (int i = 0; i < values.Length; i++)
                Assert.That(float.IsNaN(values[i]) || float.IsInfinity(values[i]), Is.False);
        }

        private static ComputeShader LoadShader(string path)
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            Assert.That(shader, Is.Not.Null, $"Missing ComputeShader at {path}.");
            return shader;
        }

        private static int DivideRoundUp(int value, int groupSize)
        {
            return (value + groupSize - 1) / groupSize;
        }

        private static void IgnoreWithoutSurfaceSupport(in FluidSurfaceGridSettings grid)
        {
            string reason = null;
            if (!SystemInfo.supportsComputeShaders
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                || !FluidSurfaceGpuResources.IsSupported(in grid, out reason))
            {
                Assert.Ignore(reason ?? "ComputeShader is unavailable; GPU density test is skipped, not passed.");
            }
        }
    }
}

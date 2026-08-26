using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    public sealed class FireParcelSurfaceResources : IDisposable
    {
        public const int TriangleStride = 96;
        public RenderTexture Density { get; }
        public GraphicsBuffer Triangles { get; }
        public GraphicsBuffer Counter { get; }
        public GraphicsBuffer Overflow { get; }
        public GraphicsBuffer DrawArgs { get; }

        public FireParcelSurfaceResources(in FireParcelSurfaceGridSettings grid, int maximumTriangles)
        {
            var descriptor = new RenderTextureDescriptor(grid.Resolution.x, grid.Resolution.y)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = grid.Resolution.z,
                graphicsFormat = GraphicsFormat.R32_SFloat,
                depthStencilFormat = GraphicsFormat.None,
                enableRandomWrite = true,
                msaaSamples = 1
            };
            Density = new RenderTexture(descriptor) { name = "GpuFire.Density", wrapMode = TextureWrapMode.Clamp };
            if (!Density.Create()) throw new InvalidOperationException("无法创建 Fire Density Texture。");
            Triangles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maximumTriangles, TriangleStride);
            Counter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
            Overflow = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
            DrawArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawArgs.size);
        }

        public void Dispose()
        {
            if (Density != null) { Density.Release(); UnityEngine.Object.Destroy(Density); }
            Triangles?.Dispose(); Counter?.Dispose(); Overflow?.Dispose(); DrawArgs?.Dispose();
        }
    }
}

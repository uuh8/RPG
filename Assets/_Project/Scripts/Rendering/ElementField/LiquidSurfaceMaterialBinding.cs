using System;
using Game.Materials;
using UnityEngine;

namespace Game.Rendering
{
    [Serializable]
    public struct LiquidSurfaceMaterialBinding
    {
        public MaterialId Material;
        public LiquidRenderProfile Profile;
        public Material SurfaceMaterial;
    }
}

Shader "Game/Elemental/Liquid Procedural"
{
    // Property 名称和默认值与 Legacy ElementWater 保持一致，因此同一套 Water 调参经验可以迁移；
    // 唯一变化是 Vertex 不再来自 Unity Mesh，而是来自 GPU Marching Cubes Triangle Buffer。
    Properties
    {
        [Header(Color)]
        _ShallowColor("Shallow Color", Color) = (0.35, 0.85, 0.88, 1)
        _DeepColor("Deep Color", Color) = (0.04, 0.18, 0.65, 1)
        _FresnelColor("Fresnel Color", Color) = (0.72, 1.0, 0.96, 1)

        [Header(Transparency)]
        _ShallowAlpha("Shallow Alpha", Range(0, 1)) = 0.2
        _DeepAlpha("Deep Alpha", Range(0, 1)) = 0.65
        _FresnelAlphaStrength("Fresnel Alpha Strength", Range(0, 0.5)) = 0.15
        _WaterAlpha("Global Water Alpha", Range(0, 1)) = 1.0

        [Header(Waves)]
        _WaveScaleA("Wave Scale A", Float) = 15.0
        _WaveScaleB("Wave Scale B", Float) = 12.0
        _WaveSpeedA("Wave Speed A", Vector) = (0.05, 0.02, 0, 0)
        _WaveSpeedB("Wave Speed B", Vector) = (-0.03, 0.04, 0, 0)
        _WaveNormalStrength("Wave Normal Strength", Range(0, 1)) = 0.25
        _TriplanarSharpness("Triplanar Sharpness", Range(1, 8)) = 4.0
        _VolumeWaveWorldScale("Volume Wave World Scale", Float) = 1.0

        [Header(Depth And Fresnel)]
        _DepthFadeDistance("Depth Fade Distance", Range(0.05, 5)) = 1.5
        _DepthNoiseStrength("Depth Noise Strength", Range(0, 0.5)) = 0.12
        _FresnelPower("Fresnel Power", Range(0.5, 8)) = 4.0
        _Smoothness("Smoothness", Range(0, 1)) = 0.85

        [Header(Foam)]
        _FoamColor("Foam Color", Color) = (0.85, 1.0, 1.0, 1)
        _FoamDistance("Foam Distance", Range(0.01, 1)) = 0.25
        _FoamStrength("Foam Strength", Range(0, 1)) = 0.8
        _FoamAlphaBoost("Foam Alpha Boost", Range(0, 0.5)) = 0.2

        [Header(Development Fragment Isolation)]
        [Toggle] _DebugForceOpaqueFragment("Force Opaque Fragment", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        // Compute 已把 Winding 朝 outward 修正；Cull Back 可省掉封闭液体背面的大量透明 Overdraw。
        Cull Back

        Pass
        {
            Name "LiquidProceduralForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex LiquidProceduralVertex
            #pragma fragment LiquidProceduralFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "ElementWaterCore.hlsl"

            // UnityIndirect.cginc 只有在宏先定义后才生成无参数 helper。Vertex 必须先 Init，
            // 再用 GetIndirectVertexID 修正 Vulkan/GL 与 D3D 对 startVertex 的不同语义。
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs
            #include "UnityIndirect.cginc"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _FresnelColor;
                half4 _FoamColor;

                float _ShallowAlpha;
                float _DeepAlpha;
                float _FresnelAlphaStrength;
                float _WaterAlpha;

                float _WaveScaleA;
                float _WaveScaleB;
                float4 _WaveSpeedA;
                float4 _WaveSpeedB;
                float _WaveNormalStrength;
                float _TriplanarSharpness;
                float _VolumeWaveWorldScale;

                float _DepthFadeDistance;
                float _DepthNoiseStrength;
                float _FresnelPower;
                float _Smoothness;

                float _FoamDistance;
                float _FoamStrength;
                float _FoamAlphaBoost;
                float _DebugForceOpaqueFragment;
            CBUFFER_END

            struct FluidSurfaceVertex
            {
                float3 positionWS;
                float padding0;
                float3 normalWS;
                float padding1;
            };

            struct FluidSurfaceTriangle
            {
                FluidSurfaceVertex v0;
                FluidSurfaceVertex v1;
                FluidSurfaceVertex v2;
            };

            StructuredBuffer<FluidSurfaceTriangle> _FluidSurfaceTriangles;
            struct ProceduralAttributes
            {
                // SV_VertexID 是当前 Draw 的逻辑顶点编号，不需要创建 Unity Mesh/Vertex Buffer。
                uint vertexID : SV_VertexID;
            };

            struct ProceduralVaryings
            {
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float4 shadowCoord : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                half3 vertexSH : TEXCOORD4;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            FluidSurfaceVertex GetSurfaceVertex(uint vertexID)
            {
                uint triangleIndex = vertexID / 3u;
                uint cornerIndex = vertexID - triangleIndex * 3u;
                // triangle 是 HLSL 的几何着色器输入修饰符；Unity 6 DXC 不允许把它当局部变量名。
                FluidSurfaceTriangle surfaceTriangle = _FluidSurfaceTriangles[triangleIndex];
                if (cornerIndex == 0u)
                    return surfaceTriangle.v0;
                // Unity 6 DXC 不支持用 ?: 在两个自定义 Struct 之间选择返回值；
                // 显式分支既保留相同 Corner 映射，也避免 type mismatch 使整个 Pass 编译失败。
                if (cornerIndex == 1u)
                    return surfaceTriangle.v1;
                return surfaceTriangle.v2;
            }

            ProceduralVaryings LiquidProceduralVertex(ProceduralAttributes input)
            {
                ProceduralVaryings output = (ProceduralVaryings)0;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                InitIndirectDrawArgs(0);
                uint correctedVertexID = GetIndirectVertexID(input.vertexID);
                FluidSurfaceVertex vertex = GetSurfaceVertex(correctedVertexID);

                // Marching Cubes 已输出 World Space，因此不能再乘 Object-to-World；只需 World-to-Clip。
                output.positionWS = vertex.positionWS;
                output.normalWS = SafeNormalize(vertex.normalWS);
                output.positionCS = TransformWorldToHClip(vertex.positionWS);
                output.shadowCoord = TransformWorldToShadowCoord(vertex.positionWS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                OUTPUT_SH(output.normalWS, output.vertexSH);
                return output;
            }

            half4 LiquidProceduralFragment(ProceduralVaryings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // 受控诊断：仅旁路复杂 Fragment 光照/深度/波纹计算，Vertex、Cull、ZTest 与 Draw 保持不变。
                // 若该颜色可见，说明 Rasterization 已成功，故障范围可收敛到下方 Fragment 计算。
                if (_DebugForceOpaqueFragment > 0.5f)
                    return half4(_ShallowColor.rgb, 1.0h);

                float3 baseNormalWS = NormalizeNormalPerPixel(input.normalWS);
                float3 tangentWS;
                float3 bitangentWS;
                ElementWaterBuildOrthonormalBasis(baseNormalWS, tangentWS, bitangentWS);
                float3x3 tangentToWorld = float3x3(tangentWS, bitangentWS, baseNormalWS);

                // 复用 Legacy Water Core 的 World-space Triplanar Noise；封闭水滴的侧壁不会拉花。
                float waveNoiseA;
                float waveNoiseB;
                float combinedNoise;
                ElementWaterVolumeWaves_float(
                    input.positionWS,
                    baseNormalWS,
                    _Time.y,
                    _WaveScaleA,
                    _WaveScaleB,
                    _WaveSpeedA.xy,
                    _WaveSpeedB.xy,
                    _VolumeWaveWorldScale,
                    _TriplanarSharpness,
                    waveNoiseA,
                    waveNoiseB,
                    combinedNoise);

                float3 normalA = ElementWaterNormalFromHeight(
                    waveNoiseA, _WaveNormalStrength, input.positionWS, tangentToWorld);
                float3 normalB = ElementWaterNormalFromHeight(
                    waveNoiseB, _WaveNormalStrength, input.positionWS, tangentToWorld);
                float3 normalTS = ElementWaterBlendNormals(normalA, normalB);
                float3 dynamicNormalWS = NormalizeNormalPerPixel(
                    TransformTangentToWorld(normalTS, tangentToWorld));

                // Camera Depth 给出水面后方第一个不透明几何的深度；两者之差近似视线方向水层厚度。
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float rawSceneDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = unity_OrthoParams.w == 0.0
                    ? LinearEyeDepth(rawSceneDepth, _ZBufferParams)
                    : LinearDepthToEyeDepth(rawSceneDepth);
                float surfaceEyeDepth = -TransformWorldToView(input.positionWS).z;
                float waterThickness = max(sceneEyeDepth - surfaceEyeDepth, 0.0);

                float3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                float3 viewDirectionTS = TransformWorldToTangent(viewDirectionWS, tangentToWorld);
                float depthMask;
                float fresnelMask;
                ElementWaterSurface_float(
                    waterThickness,
                    _DepthFadeDistance,
                    _DepthNoiseStrength,
                    combinedNoise,
                    normalTS,
                    viewDirectionTS,
                    _FresnelPower,
                    depthMask,
                    fresnelMask);

                float contactMask = 1.0 - saturate(
                    waterThickness / max(_FoamDistance, 0.0001));
                float foamNoise = smoothstep(0.35, 0.70, combinedNoise);
                float foamMask = saturate(contactMask * foamNoise * _FoamStrength);
                half3 depthColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, depthMask);
                half3 fresnelColor = lerp(depthColor, _FresnelColor.rgb, fresnelMask);
                half3 finalAlbedo = lerp(fresnelColor, _FoamColor.rgb, foamMask);
                float depthAlpha = lerp(_ShallowAlpha, _DeepAlpha, depthMask);
                half finalAlpha = saturate((depthAlpha
                    + fresnelMask * _FresnelAlphaStrength
                    + foamMask * _FoamAlphaBoost) * _WaterAlpha);
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = finalAlbedo;
                surfaceData.metallic = 0.0h;
                surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                surfaceData.smoothness = (half)_Smoothness;
                surfaceData.normalTS = (half3)normalTS;
                surfaceData.occlusion = 1.0h;
                surfaceData.emission = half3(0.0h, 0.0h, 0.0h);
                surfaceData.alpha = finalAlpha;
                surfaceData.clearCoatMask = 0.0h;
                surfaceData.clearCoatSmoothness = 0.0h;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = dynamicNormalWS;
                inputData.viewDirectionWS = (half3)viewDirectionWS;
                inputData.shadowCoord = input.shadowCoord;
                inputData.fogCoord = input.fogFactor;
                inputData.vertexLighting = VertexLighting(input.positionWS, dynamicNormalWS);
                inputData.bakedGI = SampleSHPixel(input.vertexSH, dynamicNormalWS);
                inputData.normalizedScreenSpaceUV = screenUV;
                inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);
                inputData.tangentToWorld = (half3x3)tangentToWorld;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = finalAlpha;
                return color;
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}

Shader "Game/Elemental/Fire Parcel Splat"
{
    Properties
    {
        [HDR]_CoreColor("Core Color", Color) = (4,1.2,0.05,1)
        [HDR]_EdgeColor("Edge Color", Color) = (1.2,0.04,0.01,1)
        _Softness("Softness", Range(0.01,1)) = 0.35
        _PhaseMode("Phase Mode (0 Surface, 1 Gas)", Float) = 0
        _GasRadiusMultiplier("Gas Radius Multiplier", Range(0.25,2)) = 0.78
        _GasAlphaScale("Gas Alpha Scale", Range(0,1)) = 0.42
        _SurfaceRadiusMultiplier("Surface Splat Radius Multiplier", Range(0.25,2)) = 0.9
        _SurfaceAlphaScale("Surface Splat Alpha Scale", Range(0,1)) = 0.48
        _EdgeIrregularity("Edge Irregularity", Range(0,0.4)) = 0.16
        _VerticalStretch("Vertical Stretch", Range(1,2)) = 1.28
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "FireParcelSplat"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct FireParcelStateGpu
            {
                float3 position; float age;
                float3 velocity; float lifetime;
                float3 sourcePosition; float radius;
                float surfaceWeight; float gasWeight; float heat; float active;
            };
            StructuredBuffer<FireParcelStateGpu> _FireParcelStates;
            half4 _CoreColor;
            half4 _EdgeColor;
            float _Softness;
            float _PhaseMode;
            float _GasRadiusMultiplier;
            float _GasAlphaScale;
            float _SurfaceRadiusMultiplier;
            float _SurfaceAlphaScale;
            float _EdgeIrregularity;
            float _VerticalStretch;
            float3 _SurfaceLodCenter;
            float3 _SurfaceLodHalfSize;
            float _SurfaceLodBlendWidth;

            struct Attributes { uint vertexID : SV_VertexID; uint instanceID : SV_InstanceID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half4 color : TEXCOORD1; float seed : TEXCOORD2; float3 positionWS : TEXCOORD3; };

            Varyings Vert(Attributes input)
            {
                static const float2 corners[6] = {
                    float2(-1,-1), float2(-1,1), float2(1,1),
                    float2(-1,-1), float2(1,1), float2(1,-1)
                };
                FireParcelStateGpu state = _FireParcelStates[input.instanceID];
                float phaseWeight = _PhaseMode < 0.5 ? state.surfaceWeight : state.gasWeight;
                float2 corner = corners[input.vertexID % 6];
                float radius = state.radius * (_PhaseMode < 0.5
                    ? _SurfaceRadiusMultiplier
                    : _GasRadiusMultiplier);
                float3 cameraRight = float3(UNITY_MATRIX_I_V._m00, UNITY_MATRIX_I_V._m10, UNITY_MATRIX_I_V._m20);
                float3 cameraUp = float3(UNITY_MATRIX_I_V._m01, UNITY_MATRIX_I_V._m11, UNITY_MATRIX_I_V._m21);
                float verticalStretch = _PhaseMode < 0.5 ? 1.0 : _VerticalStretch;
                float3 positionWS = state.position
                    + (cameraRight * corner.x + cameraUp * corner.y * verticalStretch) * radius;
                Varyings output;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = corner;
                float alphaScale = _PhaseMode < 0.5 ? _SurfaceAlphaScale : _GasAlphaScale;
                output.color = half4(
                    lerp(_EdgeColor.rgb, _CoreColor.rgb, state.heat),
                    phaseWeight * state.active * alphaScale);
                output.seed = frac((input.instanceID + 1u) * 0.61803398875);
                output.positionWS = positionWS;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float angle = atan2(input.uv.y, input.uv.x);
                float edgeNoise = sin(angle * 3.0 + input.seed * 6.28318) * 0.62
                    + sin(angle * 5.0 - input.seed * 11.0) * 0.38;
                float irregularRadius = 1.0 + edgeNoise * _EdgeIrregularity;
                float radial = length(input.uv) / max(irregularRadius, 0.55);
                float density = 1.0 - smoothstep(1.0 - _Softness, 1.0, radial);
                if (_PhaseMode < 0.5)
                {
                    // Liquid-like Splat 只补局部 Marching Surface 外侧；越靠近 Near 内部权重越低。
                    float2 distanceToNearEdge = _SurfaceLodHalfSize.xz
                        - abs(input.positionWS.xz - _SurfaceLodCenter.xz);
                    float insideDistance = min(distanceToNearEdge.x, distanceToNearEdge.y);
                    float nearWeight = smoothstep(
                        0.0,
                        max(_SurfaceLodBlendWidth, 0.0001),
                        insideDistance);
                    density *= 1.0 - nearWeight;
                }
                clip(input.color.a * density - 0.001);
                return half4(input.color.rgb * density, input.color.a * density);
            }
            ENDHLSL
        }
    }
}

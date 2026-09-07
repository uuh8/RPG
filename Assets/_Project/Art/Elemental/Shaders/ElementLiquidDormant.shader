Shader "Game/Elemental/Liquid Dormant Cell"
{
    Properties
    {
        _ShallowColor("Shallow Color", Color) = (0.35,0.85,0.88,1)
        _DeepColor("Deep Color", Color) = (0.04,0.18,0.65,1)
        _Smoothness("Smoothness", Range(0,1)) = 0.8
        _WaterAlpha("Global Alpha", Range(0,1)) = 0.75
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back
        Pass
        {
            Name "DormantLiquidForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor;
                float4 _DeepColor;
                float _Smoothness;
                float _WaterAlpha;
            CBUFFER_END

            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; };
            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }
            half4 Frag(Varyings input):SV_Target
            {
                float3 normal = normalize(input.normalWS);
                Light light = GetMainLight();
                float lighting = .35 + .65 * saturate(dot(normal, light.direction));
                float top = saturate(normal.y * .5 + .5);
                float3 color = lerp(_DeepColor.rgb, _ShallowColor.rgb, top) * lighting;
                return half4(color, saturate(_WaterAlpha * lerp(_DeepColor.a, _ShallowColor.a, top)));
            }
            ENDHLSL
        }
    }
}

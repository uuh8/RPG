Shader "Game/Elemental/Fire Parcel Surface"
{
    Properties { [HDR]_CoreColor("Core",Color)=(4,1.2,0.05,1) [HDR]_EdgeColor("Edge",Color)=(1.2,0.04,0.01,1) _Alpha("Alpha",Range(0,1))=0.8 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }
        Pass
        {
            // 普通 Alpha 合成避免多个 Surface 层把 HDR 能量无上限相加；Bloom 仍由 HDR RGB 驱动。
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct V{float3 p;float p0;float3 n;float p1;}; struct T{V a;V b;V c;};
            StructuredBuffer<T> _FireSurfaceTriangles;
            half4 _CoreColor,_EdgeColor; float _Alpha;
            float3 _SurfaceLodCenter;
            float3 _SurfaceLodHalfSize;
            float _SurfaceLodBlendWidth;
            struct A{uint id:SV_VertexID;}; struct O{float4 cs:SV_POSITION;float3 ws:TEXCOORD0;float3 n:TEXCOORD1;};
            O Vert(A i){uint ti=i.id/3,ci=i.id%3;T t=_FireSurfaceTriangles[ti];V v;if(ci==0)v=t.a;else if(ci==1)v=t.b;else v=t.c;O o;o.ws=v.p;o.n=v.n;o.cs=TransformWorldToHClip(v.p);return o;}
            half4 Frag(O i):SV_Target
            {
                float3 view=GetWorldSpaceNormalizeViewDir(i.ws);
                float fresnel=pow(1-saturate(dot(normalize(i.n),view)),2);
                half3 color=lerp(_CoreColor.rgb,_EdgeColor.rgb,fresnel);
                float2 distanceToNearEdge = _SurfaceLodHalfSize.xz
                    - abs(i.ws.xz - _SurfaceLodCenter.xz);
                float insideDistance = min(distanceToNearEdge.x, distanceToNearEdge.y);
                float nearWeight = smoothstep(
                    0.0,
                    max(_SurfaceLodBlendWidth, 0.0001),
                    insideDistance);
                return half4(color,_Alpha * nearWeight);
            }
            ENDHLSL
        }
    }
}

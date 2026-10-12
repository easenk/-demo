Shader "HD2D/LayeredLitSprite"
{
    Properties
    {
        _MainTex ("Sprite", 2D) = "white" {}
        _Tint ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0.01, 1)) = 0.35
        _LightMix ("Unlit Mix (0=全受光 1=无光)", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off                       // 双面可见
            ZWrite Off                     // 不写深度 → 共面层靠 sortingOrder，不打架
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half  _Cutoff;
                half  _LightMix;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                half3  normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.uv = IN.uv;
                OUT.normalWS = normalize(mul(IN.normalOS, (float3x3)unity_WorldToObject));
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _Tint;
                clip(tex.a - _Cutoff);          // 硬边剔除，像素画的命根子

                half3 N = normalize(IN.normalWS);

                // 1) 主光：带实时阴影衰减 → 角色走进树影会变暗
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half ndl = saturate(dot(N, mainLight.direction) * 0.5 + 0.5);   // half-lambert：暗部不死黑
                half3 lightSum = mainLight.color * mainLight.shadowAttenuation * ndl;

                // 2) 附加光（战场聚光等）：竖直立牌对顶光是掠射，天然贡献小——物理正确
                int count = GetAdditionalLightsCount();
                for (int i = 0; i < count; i++)
                {
                    Light add = GetAdditionalLight(i, IN.positionWS);
                    lightSum += add.color * add.distanceAttenuation
                              * saturate(dot(N, add.direction) * 0.5 + 0.5);
                }

                // 3) 环境光（来自天空盒采样）
                half3 ambient = SampleSH(N);

                half3 litColor   = tex.rgb * (lightSum + ambient);
                half3 unlitColor = tex.rgb;                     // 贴图原样
                half3 color = lerp(litColor, unlitColor, _LightMix);
                return half4(color, tex.a);
            }
            ENDHLSL
        }
    }
}
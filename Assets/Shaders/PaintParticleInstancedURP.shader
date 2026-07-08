Shader "Custom/PaintParticleInstancedURP"
{

    Properties
    {
        _Scale        ("Particle Scale",           Float)        = 0.06
        _Glossiness   ("Wetness (Specular Power)", Range(4, 128)) = 48
        _SpecStrength ("Specular Strength",        Range(0, 2))   = 0.8
        _AmbientBoost ("Ambient Boost",            Range(0, 1))   = 0.25
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5   

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            StructuredBuffer<float3> _Positions;   
            StructuredBuffer<int>    _States;     

            float  _Scale;
            float  _Glossiness;
            float  _SpecStrength;
            float  _AmbientBoost;

            float3 _BucketCenter;
            float3 _BucketRight;
            float3 _BucketUp;
            float3 _BucketForward;
            float  _BucketHeight;
            float4 _PaintColor;
            float4 _PaintColorDark;

            struct Attributes
            {
                float3 positionOS : POSITION;   
                float2 uv         : TEXCOORD0;   
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float4 color      : TEXCOORD2;
            };

            Varyings vert (Attributes IN, uint instanceID : SV_InstanceID)
            {
                Varyings OUT;

                float3 rawPos = _Positions[instanceID];
                int    state  = _States[instanceID];

         
                float3 center = (state == 0)
                    ? (_BucketCenter + _BucketRight * rawPos.x + _BucketUp * rawPos.y + _BucketForward * rawPos.z)
                    : rawPos;

                float scale = (state == 2) ? 0.0 : _Scale;

                float3 camRight = float3(UNITY_MATRIX_V[0][0], UNITY_MATRIX_V[0][1], UNITY_MATRIX_V[0][2]);
                float3 camUp    = float3(UNITY_MATRIX_V[1][0], UNITY_MATRIX_V[1][1], UNITY_MATRIX_V[1][2]);
                float3 worldPos = center + (camRight * IN.positionOS.x + camUp * IN.positionOS.y) * scale;

                OUT.positionWS = worldPos;
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = IN.uv;

                float depth  = (state == 0) ? rawPos.y : dot(center - _BucketCenter, _BucketUp);
                float halfH  = _BucketHeight * 0.5;
                float t      = saturate((depth + halfH) / halfH);
                OUT.color = lerp(_PaintColorDark, _PaintColor, t);

                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float2 p  = IN.uv * 2.0 - 1.0;
                float  r2 = dot(p, p);
                if (r2 > 1.0) discard;

              
                float3 nView = float3(p.x, p.y, sqrt(1.0 - r2));
                float3 nWS   = normalize(mul((float3x3)UNITY_MATRIX_I_V, nView));

               
                Light  mainLight = GetMainLight();
                float3 L = normalize(mainLight.direction);
                float3 V = normalize(GetCameraPositionWS() - IN.positionWS);
                float3 H = normalize(L + V);

                float  ndl   = saturate(dot(nWS, L));
                float3 shade = IN.color.rgb * (ndl * 0.7 + _AmbientBoost);

               
                float  spec = pow(saturate(dot(nWS, H)), _Glossiness) * _SpecStrength;
                float3 lit  = shade + spec * IN.color.rgb * 0.5;

                return half4(saturate(lit), 1.0);
            }
            ENDHLSL
        }
    }
}
Shader "Custom/PaintParticleInstancedURP"
{
    // GPU-instanced sphere-imposter:
    //   - one camera-facing quad per particle (2 triangles)
    //   - positions/states read straight from the solver's ComputeBuffers (no CPU readback)
    //   - the quad is shaded as a lit, round, wet sphere in the fragment stage
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
            #pragma target   4.5   // needed for StructuredBuffer in the vertex stage

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            StructuredBuffer<float3> _Positions;   // particle world positions (from solver)
            StructuredBuffer<int>    _States;      // 0=Inside 1=Falling 2=OnCanvas

            float  _Scale;
            float  _Glossiness;
            float  _SpecStrength;
            float  _AmbientBoost;

            // Set every frame from C#. INSIDE particle positions are bucket-fluid-local.
            float3 _BucketCenter;
            float3 _BucketRight;
            float3 _BucketUp;
            float3 _BucketForward;
            float  _BucketHeight;
            float4 _PaintColor;
            float4 _PaintColorDark;

            struct Attributes
            {
                float3 positionOS : POSITION;   // quad corner in [-0.5..0.5]
                float2 uv         : TEXCOORD0;   // 0..1 across the quad
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

                // state 0 = INSIDE stored in bucket-fluid-local space.
                // state 1 = FALLING already stored in world-space.
                float3 center = (state == 0)
                    ? (_BucketCenter + _BucketRight * rawPos.x + _BucketUp * rawPos.y + _BucketForward * rawPos.z)
                    : rawPos;

                // Hide particles that already landed on the canvas (state == 2)
                float scale = (state == 2) ? 0.0 : _Scale;

                // Build a camera-facing quad (billboard) from the view axes
                float3 camRight = float3(UNITY_MATRIX_V[0][0], UNITY_MATRIX_V[0][1], UNITY_MATRIX_V[0][2]);
                float3 camUp    = float3(UNITY_MATRIX_V[1][0], UNITY_MATRIX_V[1][1], UNITY_MATRIX_V[1][2]);
                float3 worldPos = center + (camRight * IN.positionOS.x + camUp * IN.positionOS.y) * scale;

                OUT.positionWS = worldPos;
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.uv = IN.uv;

                // Depth-based colour: dark at the bottom of the bucket -> light at the top
                float depth  = (state == 0) ? rawPos.y : dot(center - _BucketCenter, _BucketUp);
                float halfH  = _BucketHeight * 0.5;
                float t      = saturate((depth + halfH) / halfH);
                OUT.color = lerp(_PaintColorDark, _PaintColor, t);

                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // 1) Make the quad a round disc
                float2 p  = IN.uv * 2.0 - 1.0;
                float  r2 = dot(p, p);
                if (r2 > 1.0) discard;

                // 2) Fake a sphere normal (view space) -> world space
                float3 nView = float3(p.x, p.y, sqrt(1.0 - r2));
                float3 nWS   = normalize(mul((float3x3)UNITY_MATRIX_I_V, nView));

                // 3) Lambert shading that keeps the paint colour,
                //    independent of (possibly very bright / HDR) light intensity
                Light  mainLight = GetMainLight();
                float3 L = normalize(mainLight.direction);
                float3 V = normalize(GetCameraPositionWS() - IN.positionWS);
                float3 H = normalize(L + V);

                float  ndl   = saturate(dot(nWS, L));
                float3 shade = IN.color.rgb * (ndl * 0.7 + _AmbientBoost);

                // Subtle wet highlight, tinted by the paint colour (never washes to white)
                float  spec = pow(saturate(dot(nWS, H)), _Glossiness) * _SpecStrength;
                float3 lit  = shade + spec * IN.color.rgb * 0.5;

                return half4(saturate(lit), 1.0);
            }
            ENDHLSL
        }
    }
}
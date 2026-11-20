Shader "Hidden/Demeo/AlphaDiagnostics"
{
    SubShader
    {
        Pass
        {
            Name "ALPHA_TEST_STRIP"
            Cull Off ZWrite Off ZTest Always ColorMask RGBA Blend Off AlphaToMask Off
            Stencil { WriteMask 0 Comp Always Pass Keep Fail Keep ZFail Keep }
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ STEREO_INSTANCING_ON
            #pragma multi_compile_fragment _ _LINEAR_TO_SRGB_CONVERSION
            #include "UnityCG.cginc"
            struct Attributes { uint id : SV_VertexID; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings o;
                UNITY_INITIALIZE_OUTPUT(Varyings,o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.uv = float2((input.id << 1) & 2, input.id & 2);
                o.position = float4(o.uv * 2 - 1,0,1);
                return o;
            }
            float4 frag(Varyings input) : SV_Target
            {
                // Screen-relative strip; clip everything outside. No scene
                // sample, alpha blend, depth write or second RGB*A multiply.
                float2 p = (input.uv - float2(.25,.2)) / float2(.5,.08);
                clip(p); clip(1-p);
                float s = min(p.x * 6,5.9999);
                int cell = (int)s;
                float4 value;
                if (cell == 0) value = 0;
                else if (cell == 1) value = float4(.25,.25,.25,1);
                else if (cell == 2) value = float4(0,1,0,1);
                else if (cell == 3) value = float4(.5,.5,.5,.5);
                else if (cell == 4) value = float4(.25,.25,.25,0);
                else { float a = frac(s); value = float4(float3(.25,.25,.25)*a,a); }
                // Follow the final blit's existing conversion keyword when
                // Unity uses an UNORM output view. Never gamma-encode alpha.
                #if defined(_LINEAR_TO_SRGB_CONVERSION)
                value.rgb = LinearToGammaSpace(value.rgb);
                #endif
                return value;
            }
            ENDCG
        }
    }
    Fallback Off
}

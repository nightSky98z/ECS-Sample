Shader "Custom/CheckerGrid"
{
    Properties
    {
        _ColorA("Color A", Color) = (1, 1, 1, 1)
        _ColorB("Color B", Color) = (0.55, 0.55, 0.55, 1)
        _Tiling("Cells Per UV", Float) = 20
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _ColorA;
                float4 _ColorB;
                float _Tiling;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float4, _ColorA)
                UNITY_DOTS_INSTANCED_PROP(float4, _ColorB)
                UNITY_DOTS_INSTANCED_PROP(float, _Tiling)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

            static float4 unity_DOTS_Sampled_ColorA;
            static float4 unity_DOTS_Sampled_ColorB;
            static float unity_DOTS_Sampled_Tiling;

            void SetupDOTSCheckerGridMaterialPropertyCaches()
            {
                unity_DOTS_Sampled_ColorA = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _ColorA);
                unity_DOTS_Sampled_ColorB = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _ColorB);
                unity_DOTS_Sampled_Tiling = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _Tiling);
            }

            #undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
            #define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSCheckerGridMaterialPropertyCaches()

            #define _ColorA unity_DOTS_Sampled_ColorA
            #define _ColorB unity_DOTS_Sampled_ColorB
            #define _Tiling unity_DOTS_Sampled_Tiling
            #endif

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float cellsPerUv = max(_Tiling, 1.0);
                float2 cell = floor(input.uv * cellsPerUv);
                float checker = fmod(cell.x + cell.y, 2.0);

                return half4(lerp(_ColorA.rgb, _ColorB.rgb, checker), 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

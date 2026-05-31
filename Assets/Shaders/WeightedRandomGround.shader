Shader "Custom/WeightedRandomGround"
{
    Properties
    {
        _DirtTex("Dirt Texture", 2D) = "white" {}
        _ForestTex("Forest Floor Texture", 2D) = "white" {}
        _BirchTex("Forest Floor Birch Texture", 2D) = "white" {}
        _TextureTileSize("Texture Tile Size", Float) = 5
        _PatchSize("Random Patch Size", Float) = 20
        _RandomSeed("Random Seed", Float) = 1
        _DirtWeight("Dirt Weight", Float) = 1
        _ForestWeight("Forest Floor Weight", Float) = 3
        _BirchWeight("Forest Floor Birch Weight", Float) = 2
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

            TEXTURE2D(_DirtTex);
            SAMPLER(sampler_DirtTex);
            TEXTURE2D(_ForestTex);
            SAMPLER(sampler_ForestTex);
            TEXTURE2D(_BirchTex);
            SAMPLER(sampler_BirchTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 worldXZ : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float _TextureTileSize;
                float _PatchSize;
                float _RandomSeed;
                float _DirtWeight;
                float _ForestWeight;
                float _BirchWeight;
            CBUFFER_END

            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float, _TextureTileSize)
                UNITY_DOTS_INSTANCED_PROP(float, _PatchSize)
                UNITY_DOTS_INSTANCED_PROP(float, _RandomSeed)
                UNITY_DOTS_INSTANCED_PROP(float, _DirtWeight)
                UNITY_DOTS_INSTANCED_PROP(float, _ForestWeight)
                UNITY_DOTS_INSTANCED_PROP(float, _BirchWeight)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

            static float unity_DOTS_Sampled_TextureTileSize;
            static float unity_DOTS_Sampled_PatchSize;
            static float unity_DOTS_Sampled_RandomSeed;
            static float unity_DOTS_Sampled_DirtWeight;
            static float unity_DOTS_Sampled_ForestWeight;
            static float unity_DOTS_Sampled_BirchWeight;

            void SetupDOTSWeightedRandomGroundMaterialPropertyCaches()
            {
                unity_DOTS_Sampled_TextureTileSize = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _TextureTileSize);
                unity_DOTS_Sampled_PatchSize = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _PatchSize);
                unity_DOTS_Sampled_RandomSeed = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _RandomSeed);
                unity_DOTS_Sampled_DirtWeight = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _DirtWeight);
                unity_DOTS_Sampled_ForestWeight = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _ForestWeight);
                unity_DOTS_Sampled_BirchWeight = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _BirchWeight);
            }

            #undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
            #define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSWeightedRandomGroundMaterialPropertyCaches()

            #define _TextureTileSize unity_DOTS_Sampled_TextureTileSize
            #define _PatchSize unity_DOTS_Sampled_PatchSize
            #define _RandomSeed unity_DOTS_Sampled_RandomSeed
            #define _DirtWeight unity_DOTS_Sampled_DirtWeight
            #define _ForestWeight unity_DOTS_Sampled_ForestWeight
            #define _BirchWeight unity_DOTS_Sampled_BirchWeight
            #endif

            float HashCell(float2 cell, float seed)
            {
                float2 value = cell + float2(seed * 17.13, seed * 43.71);
                value = frac(value * float2(0.1031, 0.11369));
                value += dot(value, value.yx + 33.33);
                return frac((value.x + value.y) * value.x);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 worldPosition = TransformObjectToWorld(input.positionOS.xyz);
                output.positionHCS = TransformWorldToHClip(worldPosition);
                output.worldXZ = worldPosition.xz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float tileSize = max(abs(_TextureTileSize), 0.001);
                float patchSize = max(abs(_PatchSize), 0.001);
                float2 textureUv = input.worldXZ / tileSize;
                float2 patchCell = floor(input.worldXZ / patchSize);

                float dirtWeight = max(_DirtWeight, 0.0);
                float forestWeight = max(_ForestWeight, 0.0);
                float birchWeight = max(_BirchWeight, 0.0);
                float totalWeight = dirtWeight + forestWeight + birchWeight;

                if (totalWeight <= 0.0)
                {
                    return SAMPLE_TEXTURE2D(_ForestTex, sampler_ForestTex, textureUv);
                }

                float roll = HashCell(patchCell, _RandomSeed) * totalWeight;

                if (roll < dirtWeight)
                {
                    return SAMPLE_TEXTURE2D(_DirtTex, sampler_DirtTex, textureUv);
                }

                if (roll < dirtWeight + forestWeight)
                {
                    return SAMPLE_TEXTURE2D(_ForestTex, sampler_ForestTex, textureUv);
                }

                return SAMPLE_TEXTURE2D(_BirchTex, sampler_BirchTex, textureUv);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

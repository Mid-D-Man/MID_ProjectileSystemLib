// InstancedProjectile_URP.shader
// URP version of the 2D Instanced Projectile shader.
// Requires ProjectileRenderer2D.cs to pass _UVRect and _Color 
// via MaterialPropertyBlock.SetVectorArray.

Shader "MidMan/InstancedProjectile_URP"
{
    Properties
    {
        _MainTex ("Sprite Atlas", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType"     = "Plane"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Lighting Off

        Pass
        {
            Name "Unlit"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // Enables GPU instancing and the associated macros
            #pragma multi_compile_instancing

            // Core URP library replacing UnityCG.cginc
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // URP specific texture declaration
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            // Per-instance properties
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _UVRect)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            // 'appdata' is conventionally 'Attributes' in URP
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // 'v2f' is conventionally 'Varyings' in URP
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float4 col        : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // URP equivalent of UnityObjectToClipPos
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                // Remap the quad's [0,1] UVs into the sprite's atlas rect
                float4 rect = UNITY_ACCESS_INSTANCED_PROP(Props, _UVRect);
                output.uv = input.uv * rect.zw + rect.xy;

                output.col = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                
                return output;
            }

            // URP uses half4 for color data instead of fixed4
            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // URP texture sampling
                half4 texCol = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);

                texCol.a *= input.col.a;
                texCol.rgb *= input.col.rgb;

                return texCol;
            }
            ENDHLSL
        }
    }

    Fallback Off
}

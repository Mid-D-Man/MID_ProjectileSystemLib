// InstancedProjectile.shader
// Built-in render pipeline, fully GPU-instanced.
// Each instance reads its own UV rect (atlas sub-region) and tint colour.
// Compatible with Unity 2022.3 + old hardware — no compute shaders required.

Shader "MidMan/InstancedProjectile"
{
    Properties
    {
        _MainTex ("Sprite Atlas", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
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
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            // Required for DrawMeshInstanced with per-instance data
            #pragma instancing_options assumeuniformscaling

            #include "UnityCG.cginc"

            // ── Per-instance data via MaterialPropertyBlock ────────────────
            UNITY_INSTANCING_BUFFER_START(InstanceProps)
                // UV region inside the atlas: (x_offset, y_offset, width, height)
                // in 0-1 UV space
                UNITY_DEFINE_INSTANCED_PROP(float4, _UVRect)
                // RGBA tint — used for fade-out (alpha) and status effects
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(InstanceProps)

            sampler2D _MainTex;

            struct appdata
            {
                float4 vertex   : POSITION;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                float4 col : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o;
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                o.pos = UnityObjectToClipPos(v.vertex);

                // Remap quad UV (0–1) into the sprite's sub-region of the atlas
                float4 uvRect = UNITY_ACCESS_INSTANCED_PROP(InstanceProps, _UVRect);
                o.uv = uvRect.xy + v.texcoord * uvRect.zw;

                o.col = UNITY_ACCESS_INSTANCED_PROP(InstanceProps, _Color);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                fixed4 texCol = tex2D(_MainTex, i.uv);
                // Multiply atlas pixel by per-instance tint
                // Alpha from both texture AND tint (for fade-out)
                return texCol * i.col;
            }
            ENDCG
        }
    }

    // Fallback for hardware that can't do instancing — renders nothing
    // rather than breaking silently
    FallBack Off
}

// Inkstrike target ball: a see-through team-coloured dome over the target, rings sweeping down it
// over and over (towards the ground: where it'll land). Brighter along its silhouette. Its UVs: v up
// it (0 at the bottom, 1 at the top). Two-sided, glowing (additive-ish).
Shader "Ink/StrikeBall"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.2, 0.6, 1)
        _Fade ("Fade", Range(0, 1)) = 1
        _Rings ("Rings", Float) = 7
        _Speed ("Ring Speed (rings per second)", Float) = 1.2
        _RingWidth ("Ring Width", Range(0.01, 0.9)) = 0.18
        _BaseAlpha ("Face-on Alpha", Range(0, 1)) = 0.08
        _RimAlpha ("Rim Alpha", Range(0, 1)) = 0.45
        _RingAlpha ("Ring Alpha", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha One
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Fade, _Rings, _Speed, _RingWidth, _BaseAlpha, _RimAlpha, _RingAlpha;

            struct v2f
            {
                float4 pos     : SV_POSITION;
                float2 uv      : TEXCOORD0;
                float3 normal  : TEXCOORD1;
                float3 viewDir : TEXCOORD2;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = normalize(UnityWorldSpaceViewDir(mul(unity_ObjectToWorld, v.vertex).xyz));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float band = frac(i.uv.y * _Rings + _Time.y * _Speed);     // + time: the rings move down v
                float aa = fwidth(i.uv.y * _Rings) * 1.5;
                float ring = smoothstep(1.0 - _RingWidth - aa, 1.0 - _RingWidth, band) * (1.0 - smoothstep(1.0 - aa, 1.0, band));
                ring *= band;                                               // a soft trailing fade above each ring
                float rim = pow(1.0 - abs(dot(normalize(i.normal), i.viewDir)), 3.0);
                float a = (_BaseAlpha + rim * _RimAlpha + ring * _RingAlpha) * _Fade;
                return fixed4(_Color.rgb, saturate(a));
            }
            ENDCG
        }
    }
}

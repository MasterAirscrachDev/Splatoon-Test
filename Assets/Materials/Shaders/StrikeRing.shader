// Inkstrike target ring: the band round the target ball's equator. Warning chevrons in the team
// colour run round it (they spin), between two solid edges top and bottom. Its UVs: u round the band
// (0..1), v up it (0.5 at the bottom edge, 1 at the top). Two-sided, glowing (additive-ish).
Shader "Ink/StrikeRing"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.2, 0.6, 1)
        _Fade ("Fade", Range(0, 1)) = 1
        _Chevrons ("Chevrons Round", Float) = 16
        _Spin ("Spin (turns per second)", Float) = 0.35
        _Slant ("Chevron Slant", Range(0, 1)) = 0.35
        _Gap ("Gap Between Chevrons", Range(0, 0.9)) = 0.45
        _EdgeWidth ("Edge Width (share of the band)", Range(0, 0.5)) = 0.14
        _Alpha ("Alpha", Range(0, 1)) = 0.9
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
            float _Fade, _Chevrons, _Spin, _Slant, _Gap, _EdgeWidth, _Alpha;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float across = saturate((i.uv.y - 0.5) * 2.0);          // 0 at the bottom edge, 1 at the top
                float edge = step(across, _EdgeWidth) + step(1.0 - _EdgeWidth, across);
                // Chevrons: a "<" shape per cell, bent at the band's middle, marching round with time.
                float bend = abs(across - 0.5) * 2.0 * _Slant;
                float cell = frac(i.uv.x * _Chevrons + bend - _Time.y * _Spin * _Chevrons);
                float chevron = step(_Gap, cell) * (1.0 - edge);
                float aa = fwidth(cell) * 1.5;                           // soften the chevrons' sides
                chevron *= smoothstep(_Gap, _Gap + aa, cell) * smoothstep(1.0, 1.0 - aa, cell);
                float a = (edge * 1.0 + chevron * 0.85) * _Alpha * _Fade;
                return fixed4(_Color.rgb, saturate(a));
            }
            ENDCG
        }
    }
}

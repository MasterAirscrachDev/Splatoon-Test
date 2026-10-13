// A sub's preview path (SubPreview's line): like Ink/SubPreview, pale down the middle and edged in a
// dark shade of the team colour, so it reads on our own ink as well as bare floor - across the
// line's width (a LineRenderer's v) rather than by its facing. Unlit, see-through, antialiased edges.
// _EdgeDark 1 and _Pale 0 make it one flat colour (the Sniper's laser).
Shader "Ink/SubPreviewLine"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.2, 0.6, 1)
        _Alpha ("Alpha", Range(0, 1)) = 0.9
        _Pale ("Paleness", Range(0, 1)) = 0.8
        _EdgeDark ("Edge shade", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Alpha, _Pale, _EdgeDark;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float across = abs(i.uv.y * 2.0 - 1.0);            // 0 down the middle, 1 at the edges
                float edge = smoothstep(0.3, 0.7, across);
                fixed3 core = lerp(_Color.rgb, 1.0, _Pale);
                float soft = 1.0 - smoothstep(1.0 - fwidth(across) * 1.5, 1.0, across); // antialiased sides
                return fixed4(lerp(core, _Color.rgb * _EdgeDark, edge), _Alpha * soft);
            }
            ENDCG
        }
    }
}

Shader "Ink/InkSurface"
{
    Properties
    {
        [Header(Ink Layer)]
        _MainTex     ("Ink Coverage (R alpha, G beta)", 2D) = "black" {}
        _AlphaColor  ("Alpha Team Colour",     Color)       = (0, 1, 1, 1)
        _BetaColor   ("Beta Team Colour",      Color)       = (1, 0, 1, 1)
        _GammaColor  ("Gamma Team Colour",     Color)       = (1, 0.85, 0.05, 1)
        _DeltaColor  ("Delta Team Colour",     Color)       = (0.2, 0.85, 0.25, 1)
        _BumpMap     ("Normal Map",            2D)          = "bump"  {}
        _Smoothness  ("Smoothness",            Range(0,1))  = 0.4
        _AlphaCutoff ("Alpha Cutoff",          Range(0,1))  = 0.1

        [Header(Underlay Layer)]
        [Toggle(_UNDERLAY_ON)] _UnderlayToggle ("Use Underlay (ignores Alpha Cutoff)", Float) = 0
        _UnderlayTex    ("Underlay Albedo",    2D)          = "white" {}
        _UnderlayBump   ("Underlay Normal",    2D)          = "bump"  {}
        _UnderlaySmooth ("Underlay Smoothness",Range(0,1))  = 0.1
    }

    SubShader
    {
        // AlphaTest queue; with the underlay on, Alpha = 1 so nothing is clipped.
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }

        CGPROGRAM
        // Clipped by hand rather than with alphatest:, which writes 0 to the alpha channel; as an
        // opaque surface shader this writes 1, so render textures (the map) see solid ink.
        #pragma surface surf Standard fullforwardshadows
        #pragma shader_feature _UNDERLAY_ON
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _BumpMap;
        sampler2D _UnderlayTex;
        sampler2D _UnderlayBump;
        half4 _AlphaColor, _BetaColor, _GammaColor, _DeltaColor;
        half _Smoothness;
        half _UnderlaySmooth;
        half _AlphaCutoff;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv_UnderlayTex;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float4 ink     = tex2D(_MainTex,  IN.uv_MainTex); // coverage per team: r, g, b, a = teams 1..4
            // Runtime RGB-encoded normal map (not DXT5nm), so decode directly.
            half3 inkNorm  = tex2D(_BumpMap, IN.uv_MainTex).xyz * 2.0 - 1.0;

            float coverage = saturate(ink.r + ink.g + ink.b + ink.a);
            // Team colour switches where the two strongest coverages cross, anti-aliased to about
            // a screen pixel. The filtered coverage gives a smooth sub-texel boundary, not a texel
            // staircase. (With two teams this is exactly the alpha/beta crossing.)
            half3 cols[4] = { _AlphaColor.rgb, _BetaColor.rgb, _GammaColor.rgb, _DeltaColor.rgb };
            float c[4] = { ink.r, ink.g, ink.b, ink.a };
            int first = 0;
            [unroll] for (int i = 1; i < 4; i++) if (c[i] > c[first]) first = i;
            int second = first == 0 ? 1 : 0;
            [unroll] for (int j = 0; j < 4; j++) if (j != first && c[j] > c[second]) second = j;
            float diff = c[first] - c[second];
            float t = saturate(diff / max(fwidth(diff), 1e-4) * 0.5 + 0.5);
            half3 inkColor = lerp(cols[second], cols[first], t);

            #ifdef _UNDERLAY_ON
                half4 base      = tex2D(_UnderlayTex,  IN.uv_UnderlayTex);
                half3 baseNorm  = UnpackNormal(tex2D(_UnderlayBump, IN.uv_UnderlayTex));

                o.Albedo     = lerp(base.rgb,       inkColor,  coverage);
                o.Normal     = lerp(baseNorm,        inkNorm,   coverage);
                o.Smoothness = lerp(_UnderlaySmooth, _Smoothness, coverage);
                o.Alpha      = 1.0; // always opaque when underlay is active
            #else
                o.Albedo     = inkColor;
                o.Normal     = inkNorm;
                o.Smoothness = _Smoothness;
                o.Alpha      = coverage;
                clip(coverage - _AlphaCutoff);
            #endif
        }
        ENDCG
    }

    FallBack "Standard"
}

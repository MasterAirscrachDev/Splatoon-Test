Shader "Ink/InkSurface"
{
    Properties
    {
        [Header(Ink Layer)]
        _MainTex     ("Ink Texture",          2D)          = "black" {}
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
        // alphatest:_AlphaCutoff inserts clip(o.Alpha - _AlphaCutoff).
        #pragma surface surf Standard fullforwardshadows alphatest:_AlphaCutoff
        #pragma shader_feature _UNDERLAY_ON
        #pragma target 3.0

        sampler2D _MainTex;
        sampler2D _BumpMap;
        sampler2D _UnderlayTex;
        sampler2D _UnderlayBump;
        half _Smoothness;
        half _UnderlaySmooth;

        struct Input
        {
            float2 uv_MainTex;
            float2 uv_UnderlayTex;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            half4 ink      = tex2D(_MainTex,  IN.uv_MainTex);
            // Runtime RGB-encoded normal map (not DXT5nm), so decode directly.
            half3 inkNorm  = tex2D(_BumpMap, IN.uv_MainTex).xyz * 2.0 - 1.0;

            #ifdef _UNDERLAY_ON
                half4 base      = tex2D(_UnderlayTex,  IN.uv_UnderlayTex);
                half3 baseNorm  = UnpackNormal(tex2D(_UnderlayBump, IN.uv_UnderlayTex));

                // Blend ink over underlay, weighted by ink alpha
                o.Albedo     = lerp(base.rgb,       ink.rgb,   ink.a);
                o.Normal     = lerp(baseNorm,        inkNorm,   ink.a);
                o.Smoothness = lerp(_UnderlaySmooth, _Smoothness, ink.a);
                o.Alpha      = 1.0; // always opaque when underlay is active
            #else
                o.Albedo     = ink.rgb;
                o.Normal     = inkNorm;
                o.Smoothness = _Smoothness;
                o.Alpha      = ink.a; // clipped against _AlphaCutoff
            #endif
        }
        ENDCG
    }

    FallBack "Standard"
}

// The swim-form ink tank: a rounded glass tank filled from the bottom up to _Fill, with two layers
// of moving waves (higher while sloshing, tipped by _Tilt), a foamy surface, and a mark at _Mark (the sub's cost): dashed until there's enough ink, then solid and
// glowing. Ink colour is the Image colour. Sized in pixels from the rect, so any tank shape works.
Shader "UI/InkTank"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _EmptyColor ("Empty Glass", Color) = (0.06, 0.06, 0.09, 0.8)
        _OutlineColor ("Outline", Color) = (0, 0, 0, 1)
        _Outline ("Outline Width (px)", Float) = 3
        _Corner ("Corner Radius (share of width)", Range(0, 0.5)) = 0.3
        _Fill ("Fill", Range(0, 1)) = 0.7
        _Mark ("Sub Cost Mark", Range(0, 1)) = 0.6
        _MarkColor ("Mark When Ready", Color) = (1, 1, 1, 1)
        _WaveAmp ("Wave Amplitude", Float) = 0.022
        _WaveFreq ("Waves Across", Float) = 1.1
        _WaveSpeed ("Wave Speed", Float) = 0.7
        _Slosh ("Slosh (extra wave height)", Float) = 0
        _Tilt ("Tilt (surface slope)", Float) = 0

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 uv       : TEXCOORD0;
                float4 worldPos : TEXCOORD1;
            };

            fixed4 _Color, _EmptyColor, _OutlineColor, _MarkColor;
            float _Outline, _Corner, _Fill, _Mark, _WaveAmp, _WaveFreq, _WaveSpeed, _Slosh, _Tilt;
            float4 _ClipRect;

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPos = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            // Two summed sines read as liquid rather than a regular ripple.
            float Wave(float x, float t, float offset)
            {
                return sin((x + t + offset) * 6.2832) * 0.65 + sin((x * 2.3 - t * 1.4 + offset * 1.7) * 6.2832) * 0.35;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float2 px = 1.0 / max(fwidth(uv), 1e-5); // the rect's size in pixels

                // The tank: a rounded rectangle (signed distance in pixels, negative inside).
                float2 p = (uv - 0.5) * px;
                float r = _Corner * px.x;
                float2 q = abs(p) - (0.5 * px - r);
                float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
                float shape = saturate(0.5 - d);
                float glass = saturate(0.5 - (d + _Outline)); // inside the outline

                // The ink's surface: calm when nearly empty or full, livelier while sloshing.
                float calm = saturate(min(_Fill, 1.0 - _Fill) * 12.0);
                float amp = (_WaveAmp + _Slosh) * calm;
                float t = _Time.y * _WaveSpeed;
                float x = uv.x * _WaveFreq;
                float tilt = _Tilt * (uv.x - 0.5) * calm;
                float front = _Fill + tilt + amp * Wave(x, t, 0.0);
                float back = _Fill + tilt * 0.8 + amp * (Wave(x * 0.8, -t * 0.9, 0.37) + 0.4);
                float aa = 1.0 / px.y;
                float inFront = saturate((front - uv.y) / aa + 0.5);
                float inBack = saturate((back - uv.y) / aa + 0.5);

                fixed3 ink = i.color.rgb;
                float3 col = _EmptyColor.rgb;
                float a = _EmptyColor.a;
                col = lerp(col, ink * 0.45, inBack);                       // the wave behind, in shadow
                a = lerp(a, 1.0, inBack);
                float depth = saturate((front - uv.y) / max(_Fill, 0.15)); // 0 at the surface, 1 at the bottom
                col = lerp(col, ink * lerp(1.1, 0.6, depth), inFront);
                a = lerp(a, 1.0, inFront);
                float foam = saturate(1.0 - (front - uv.y) / (3.0 * aa)) * inFront * saturate((1.0 - _Fill) * 30.0);
                col = lerp(col, lerp(ink, 1.0, 0.6), foam * 0.85);

                // Glass: a soft highlight down the left, brighter near the top.
                float sheen = smoothstep(0.1, 0.16, uv.x) * (1.0 - smoothstep(0.22, 0.3, uv.x));
                col += sheen * lerp(0.06, 0.2, uv.y);

                // The sub's cost: dashed (solid ticks at the sides) until there's enough ink, then
                // solid and glowing.
                float ready = step(_Mark, _Fill);
                float md = abs(uv.y - _Mark) * px.y; // pixels from the mark
                float stroke = saturate(1.25 - md);
                float side = 1.0 - smoothstep(0.18, 0.3, min(uv.x, 1.0 - uv.x));
                float dash = step(0.5, frac(uv.x * px.x / 6.0));
                float markA = stroke * lerp(max(side, dash * 0.6), 1.0, ready) * step(0.001, _Mark);
                col = lerp(col, lerp(float3(0.85, 0.85, 0.85), _MarkColor.rgb, ready), markA);
                a = max(a, markA);
                col += ready * saturate(1.0 - md / 6.0) * 0.18 * _MarkColor.rgb * step(0.001, _Mark);

                fixed4 o = fixed4(lerp(_OutlineColor.rgb, col, glass), lerp(_OutlineColor.a, a, glass) * shape * i.color.a);
                #ifdef UNITY_UI_CLIP_RECT
                o.a *= UnityGet2DClipping(i.worldPos.xy, _ClipRect);
                #endif
                return o;
            }
            ENDCG
        }
    }
}

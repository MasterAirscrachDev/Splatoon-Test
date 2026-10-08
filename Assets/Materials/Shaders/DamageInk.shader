// Enemy ink over the screen for damage taken: a see-through wash of round blobs around a clear
// circle in the middle that closes in as _Amount rises. The blobs sit on a jittered grid, grow (and
// reach further in) with the damage, run together where they meet, and only wobble slightly in
// place. Well outside the circle it's solid wash. Ink colour is the image's colour (the team that
// hit us), times _Color. Drawn on a full-screen UI image; _Aspect is the screen's width / height.
Shader "UI/DamageInk"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Amount ("Amount", Range(0, 1)) = 0
        _Aspect ("Aspect (width / height)", Float) = 1.777
        _ClearStart ("Clear Circle At No Damage (screen heights)", Float) = 1.05
        _ClearEnd ("Clear Circle At Most Damage", Float) = 0.3
        _BlobCell ("Blob Spacing (screen heights)", Float) = 0.17
        _BlobSize ("Blob Size (share of spacing)", Range(0.1, 1)) = 0.62
        _BlobGap ("Missing Blobs", Range(0, 1)) = 0.2
        _Goo ("Blobs Running Together", Range(0.001, 0.1)) = 0.035
        _Wobble ("Wobble (share of spacing)", Range(0, 0.3)) = 0.06
        _WobbleSpeed ("Wobble Speed", Float) = 0.8
        _Opacity ("Opacity", Range(0, 1)) = 0.3
        _RimOpacity ("Rim Opacity", Range(0, 1)) = 0.48
        _Seed ("Seed", Float) = 0

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }

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

            struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float4 worldPos : TEXCOORD1; };

            fixed4 _Color;
            float _Amount, _Aspect, _ClearStart, _ClearEnd, _BlobCell, _BlobSize, _BlobGap, _Goo, _Wobble, _WobbleSpeed;
            float _Opacity, _RimOpacity, _Seed;
            float4 _ClipRect;

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPos = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            float hash21(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            // Smooth union of two distances: blobs that come close run together.
            float smin(float a, float b, float k)
            {
                float h = saturate(0.5 + 0.5 * (b - a) / k);
                return lerp(b, a, h) - k * h * (1.0 - h);
            }

            // Signed distance to the ink (negative inside), in screen heights.
            float inkDistance(float2 p, float clearR)
            {
                float d = 1e3;
                float2 base = floor(p / _BlobCell);
                float grow = saturate(_Amount * 6.0); // nothing at all until hurt
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    float2 id = base + float2(x, y) + _Seed;
                    if (hash21(id + 5.1) < _BlobGap) continue;          // some cells have none: gaps between blobs
                    float2 jitter = float2(hash21(id), hash21(id + 1.7)) - 0.5;
                    float phase = hash21(id + 9.3) * 6.2832;
                    float2 wobble = float2(sin(_Time.y * _WobbleSpeed + phase), cos(_Time.y * _WobbleSpeed * 0.8 + phase * 1.3)) * _Wobble;
                    float2 centre = (base + float2(x, y) + 0.5 + jitter * 0.6 + wobble) * _BlobCell;
                    // A blob is as big as it is far out past the clear circle (bigger further out).
                    float outside = (length(centre) - clearR) / _BlobCell;
                    float size = saturate(outside * 0.8 + 0.35);
                    if (size < 0.3) continue;                            // no specks well inside the circle
                    float r = _BlobCell * _BlobSize * lerp(0.7, 1.15, hash21(id + 3.9)) * size * grow;
                    d = smin(d, length(p - centre) - r, _Goo);
                }
                // Well outside the circle the wash is solid, so the screen's edge never shows gaps.
                float solid = clearR + _BlobCell * 2.2 - length(p);
                return smin(d, solid, _Goo * 2.0);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (_Amount <= 0.001) return 0;
                float2 p = (i.uv - 0.5) * float2(_Aspect, 1.0);         // centred, in screen heights
                float clearR = lerp(_ClearStart, _ClearEnd, _Amount);
                float d = inkDistance(p, clearR);
                float px = 1.0 / max(_ScreenParams.y, 1.0);              // a pixel, in screen heights
                float cover = saturate(0.5 - d / px);
                if (cover <= 0.0) return 0;

                float rim = 1.0 - smoothstep(0.0, 0.012, -d);            // 1 at a blob's edge, 0 just inside
                float alpha = cover * lerp(_Opacity, _RimOpacity, rim) * i.color.a;
                float3 ink = i.color.rgb * _Color.rgb * lerp(1.0, 0.75, rim); // a slightly deeper rim

                #ifdef UNITY_UI_CLIP_RECT
                alpha *= UnityGet2DClipping(i.worldPos.xy, _ClipRect);
                #endif
                return fixed4(ink, alpha);
            }
            ENDCG
        }
    }
}

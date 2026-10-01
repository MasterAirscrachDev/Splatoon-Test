// UI liquid gauge: fills the sprite's shape from the bottom up to _Fill, with a moving wavy
// surface (calmer near empty and full) and a bright rim along it. Fill colour is the Image colour.
Shader "UI/WavyFill"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _EmptyColor ("Empty Colour", Color) = (0, 0, 0, 0.45)
        _Fill ("Fill", Range(0, 1)) = 0.5
        _WaveAmp ("Wave Amplitude", Float) = 0.035
        _WaveFreq ("Waves Across", Float) = 1.5
        _WaveSpeed ("Wave Speed", Float) = 0.6
        _Phase ("Phase", Float) = 0
        _RimBrightness ("Surface Rim", Range(0, 1)) = 0.35

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
            #pragma target 2.0
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

            sampler2D _MainTex;
            fixed4 _Color, _EmptyColor;
            float _Fill, _WaveAmp, _WaveFreq, _WaveSpeed, _Phase, _RimBrightness;
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

            fixed4 frag(v2f i) : SV_Target
            {
                float shape = tex2D(_MainTex, i.uv).a;

                // Two summed sines read as liquid rather than a regular ripple.
                float t = _Time.y * _WaveSpeed + _Phase;
                float wave = sin((i.uv.x * _WaveFreq + t) * 6.2832) * 0.7
                           + sin((i.uv.x * _WaveFreq * 2.3 - t * 1.3) * 6.2832) * 0.3;
                float calm = saturate(4.0 * _Fill * (1.0 - _Fill)); // flat when empty or full
                float surface = _Fill + _WaveAmp * calm * wave;

                float aa = fwidth(i.uv.y);
                float filled = saturate((surface - i.uv.y) / aa + 0.5);
                float rim = saturate(1.0 - abs(surface - i.uv.y) / 0.035) * _RimBrightness * calm;

                fixed4 col = lerp(_EmptyColor, i.color, filled);
                col.rgb += rim * filled;
                col.a *= shape;

                #ifdef UNITY_UI_CLIP_RECT
                col.a *= UnityGet2DClipping(i.worldPos.xy, _ClipRect);
                #endif
                return col;
            }
            ENDCG
        }
    }
}

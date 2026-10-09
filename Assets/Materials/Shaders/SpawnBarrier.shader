// Spawn barrier: a team-coloured hex-grid sphere, cut off at _CutY (its unit-sphere height at the
// pad's top) so it curves out of the pad's rim. Invisible at _Fade 0; fades in as an enemy nears or
// while it's under attack, brightest around _FocusPos and along its silhouette and base. _Flash
// pulses it when it stops something. Two-sided: the team inside sees it too.
Shader "Ink/SpawnBarrier"
{
    Properties
    {
        _Color ("Colour", Color) = (0, 1, 1, 1)
        _Fade ("Fade", Range(0, 1)) = 1
        _Flash ("Flash", Range(0, 1)) = 0
        _FocusPos ("Focus (world)", Vector) = (0, 0, 0, 0)
        _FocusRadius ("Focus Radius", Float) = 3
        _CutY ("Cut Height (unit sphere)", Range(-1, 1)) = -0.8
        _Cells ("Hex Cells Around", Float) = 28
        _LineWidth ("Line Width", Range(0.01, 0.5)) = 0.08
        _BaseAlpha ("Face-on Alpha", Range(0, 1)) = 0.06
        _RimAlpha ("Rim Alpha", Range(0, 1)) = 0.55
        _GridAlpha ("Grid Alpha", Range(0, 1)) = 0.45
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha One   // additive-ish glow over the colour behind
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Fade, _Flash, _FocusRadius, _CutY, _Cells, _LineWidth, _BaseAlpha, _RimAlpha, _GridAlpha;
            float4 _FocusPos;

            struct v2f
            {
                float4 pos     : SV_POSITION;
                float3 local   : TEXCOORD0;
                float3 world   : TEXCOORD1;
                float3 normal  : TEXCOORD2;
                float3 viewDir : TEXCOORD3;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.local = v.vertex.xyz;
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = normalize(UnityWorldSpaceViewDir(o.world));
                return o;
            }

            // Distance to the nearest hex edge (0 on an edge, ~0.5 at a centre) for a point in hex space.
            float HexEdge(float2 p)
            {
                const float2 s = float2(1.0, 1.7320508);
                float4 c = floor(float4(p, p - float2(0.5, 1.0)) / s.xyxy) + 0.5;
                float4 h = float4(p - c.xy * s, p - (c.zw + 0.5) * s);
                float2 q = dot(h.xy, h.xy) < dot(h.zw, h.zw) ? h.xy : h.zw;
                q = abs(q);
                return 0.5 - max(dot(q, s * 0.5), q.x);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.local);
                clip(n.y - _CutY);
                // Around and up the dome (its local y is up), with the grid drifting slowly upward.
                float around = atan2(n.z, n.x) / (2.0 * UNITY_PI) * _Cells;
                float up = asin(n.y) / (0.5 * UNITY_PI) * _Cells * 0.25 - _Time.y * 0.15;
                float edge = HexEdge(float2(around, up));
                float grid = 1.0 - smoothstep(0.0, _LineWidth, edge);

                float rim = pow(1.0 - abs(dot(normalize(i.normal), i.viewDir)), 3.0);
                float base = saturate(1.0 - (n.y - _CutY) * 8.0);           // a band where it meets the pad
                float focus = saturate(1.0 - distance(i.world, _FocusPos.xyz) / _FocusRadius);
                focus *= focus;

                float a = _BaseAlpha + rim * _RimAlpha + grid * _GridAlpha * (0.35 + focus) + base * 0.35 + focus * 0.5;
                a *= _Fade;
                a += _Flash * (0.25 + grid * 0.5);
                return fixed4(_Color.rgb, saturate(a));
            }
            ENDCG
        }
    }
}

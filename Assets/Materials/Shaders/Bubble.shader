// Bubble shield: a thin, wobbling transparent shell, faint face-on and bright at the rim.
// Two-sided, since the owner's camera is usually inside it.
Shader "Ink/Bubble"
{
    Properties
    {
        _Color ("Colour", Color) = (0, 1, 1, 1)
        _CoreAlpha ("Face-on Alpha", Range(0, 1)) = 0.12
        _RimAlpha ("Rim Alpha", Range(0, 1)) = 0.85
        _RimPower ("Rim Sharpness", Float) = 2.5
        _Wobble ("Wobble", Float) = 0.025
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
            float _CoreAlpha, _RimAlpha, _RimPower, _Wobble;

            struct v2f
            {
                float4 pos     : SV_POSITION;
                float3 normal  : TEXCOORD0;
                float3 viewDir : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                float3 p = v.vertex.xyz;
                p += v.normal * _Wobble * sin(_Time.y * 4.0 + p.y * 9.0 + p.x * 5.0); // jelly wobble
                float3 world = mul(unity_ObjectToWorld, float4(p, 1.0)).xyz;
                o.pos = UnityWorldToClipPos(world);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = normalize(_WorldSpaceCameraPos - world);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float facing = abs(dot(normalize(i.normal), normalize(i.viewDir)));
                float rim = pow(1.0 - facing, _RimPower);
                fixed4 col = _Color;
                col.rgb = lerp(col.rgb, 1.0, rim * 0.5); // whiter toward the edge
                col.a = lerp(_CoreAlpha, _RimAlpha, rim);
                return col;
            }
            ENDCG
        }
    }
}

// Soundblaster beam: a see-through team-coloured tube out of the speaker, through walls. Its UVs: u
// round it, v along it in metres (from the speaker). While it winds up (_Blast 0) it's a faint
// guide, pulsing, with thin rings creeping out; blasting (_Blast 1) it's bright, sound-wave rings
// racing out along it. Brighter along its silhouette. Walls hide it (its blast still goes through
// them).
Shader "Ink/SoundBeam"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.2, 0.6, 1)
        _Fade ("Fade", Range(0, 1)) = 1
        _Blast ("Blast", Range(0, 1)) = 0
        _RingSpacing ("Ring Spacing (metres)", Float) = 1.5
        _WindupSpeed ("Windup Ring Speed (m/s)", Float) = 2
        _BlastSpeed ("Blast Ring Speed (m/s)", Float) = 14
        _BaseAlpha ("Face-on Alpha", Range(0, 1)) = 0.03
        _RimAlpha ("Rim Alpha", Range(0, 1)) = 0.2
        _RingAlpha ("Ring Alpha", Range(0, 1)) = 0.3
        _FadeLength ("Fades Out Over (metres, at its far end)", Float) = 30
        _Length ("Length (metres)", Float) = 200
    }

    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" "IgnoreProjector"="True" }
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
            float _Fade, _Blast, _RingSpacing, _WindupSpeed, _BlastSpeed, _BaseAlpha, _RimAlpha, _RingAlpha, _FadeLength, _Length;

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
                float speed = lerp(_WindupSpeed, _BlastSpeed, _Blast);
                float band = frac((i.uv.y - _Time.y * speed) / _RingSpacing);  // rings out along it
                float width = lerp(0.06, 0.3, _Blast);
                float aa = fwidth(i.uv.y / _RingSpacing) * 1.5;
                float ring = smoothstep(1.0 - width - aa, 1.0 - width, band) * band;
                float rim = pow(1.0 - abs(dot(normalize(i.normal), i.viewDir)), 2.0);
                float pulse = lerp(0.75 + 0.25 * sin(_Time.y * 10.0), 1.0, _Blast);
                float far = saturate((_Length - i.uv.y) / max(_FadeLength, 0.01));   // fades out at its far end
                float a = (_BaseAlpha * (1.0 + _Blast) + rim * _RimAlpha * (1.0 + 0.5 * _Blast) + ring * _RingAlpha * (0.4 + 0.8 * _Blast)) * pulse * far * _Fade;
                return fixed4(_Color.rgb, saturate(a));
            }
            ENDCG
        }
    }
}

// A sub's preview (SubPreview): pale in the middle, ringed in a dark shade of the team colour, so it
// reads on our own ink as well as bare floor. Unlit, see-through.
Shader "Ink/SubPreview"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.2, 0.6, 1)
        _Alpha ("Alpha", Range(0, 1)) = 0.9
        _Pale ("Paleness", Range(0, 1)) = 0.8
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Alpha, _Pale;

            struct v2f
            {
                float4 pos     : SV_POSITION;
                float3 normal  : TEXCOORD0;
                float3 viewDir : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityWorldToClipPos(world);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = normalize(_WorldSpaceCameraPos - world);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float facing = saturate(dot(normalize(i.normal), normalize(i.viewDir)));
                float edge = smoothstep(0.7, 0.4, facing);
                fixed3 core = lerp(_Color.rgb, 1.0, _Pale);
                return fixed4(lerp(core, _Color.rgb * 0.35, edge), _Alpha);
            }
            ENDCG
        }
    }
}

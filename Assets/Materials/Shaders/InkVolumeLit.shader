// Lit, opaque variant of Ink/InkVolume for ink that sits in the world: same noisy liquid look and
// Standard BRDF, but drawn in the opaque queue so it receives shadows (the built-in pipeline never
// shadows transparent objects), and lit by point/spot lights too (ForwardAdd). Opacity is not
// used: give it shapes whose edges tuck under whatever they sit on. No shadow caster pass, so its
// screen-space shadow is that of the surface just behind it.
Shader "Ink/InkVolumeLit"
{
    Properties
    {
        _Color      ("Ink Color",              Color)       = (0.05, 0.15, 0.8, 1)

        [Header(Shape)]
        _Direction  ("Direction (XYZ) + Stretch (W)", Vector) = (0, -1, 0, 1)

        [Header(Surface)]
        _Gloss      ("Gloss",                  Range(0,1))  = 0.95
        _SpecTint   ("Specular Tint",          Color)       = (1,1,1,1)
        _ReflStr    ("Reflection Strength",    Range(0,2))  = 1.0
        // Keep equal to the ink surface material's Smoothness so body colour matches surface ink.
        _BodySmoothness ("Body Smoothness (match ink surface)", Range(0,1)) = 0.4

        [Header(Fluid Noise)]
        _NoiseFreq  ("Noise Frequency",        Range(0,12)) = 4
        _ShapeAmp   ("Shape Displacement",     Range(0,0.5))= 0.18
        _NoiseAmp   ("Normal Perturbation",    Range(0,0.6))= 0.18
        _ColorVar   ("Color Variation",        Range(0,0.4))= 0.10
        _FlowSpeed  ("Flow Speed",             Range(0,4))  = 1.0
        _Seed       ("Noise Seed",             Float)       = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+10" }
        Cull Back

        CGINCLUDE
        // Match InkSurface: below 3.0 UNITY_BRDF_PBS falls back to a cheaper BRDF.
        #pragma target 3.0
        #include "UnityCG.cginc"
        #include "UnityLightingCommon.cginc"
        #include "UnityStandardUtils.cginc"
        #include "UnityPBSLighting.cginc"
        #include "AutoLight.cginc"

        float4 _Color;
        float4 _Direction;
        float  _Gloss, _ReflStr, _BodySmoothness;
        float4 _SpecTint;
        float  _NoiseFreq, _ShapeAmp, _NoiseAmp, _ColorVar, _FlowSpeed, _Seed;

        // ── 3-D value noise ───────────────────────────────────────────────────
        float hash3(float3 p)
        {
            p  = frac(p * float3(127.1, 311.7, 74.7));
            p += dot(p, p.yzx + 19.19);
            return frac((p.x + p.y) * p.z);
        }

        float valueNoise3(float3 p)
        {
            float3 i = floor(p);
            float3 f = frac(p);
            float3 u = f * f * (3.0 - 2.0 * f);
            return lerp(
                lerp(lerp(hash3(i),               hash3(i + float3(1,0,0)), u.x),
                     lerp(hash3(i + float3(0,1,0)), hash3(i + float3(1,1,0)), u.x), u.y),
                lerp(lerp(hash3(i + float3(0,0,1)), hash3(i + float3(1,0,1)), u.x),
                     lerp(hash3(i + float3(0,1,1)), hash3(i + float3(1,1,1)), u.x), u.y),
                u.z
            );
        }

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float4 color  : COLOR; // particle colour; white for plain meshes
        };

        struct v2f
        {
            float4 pos       : SV_POSITION;
            float3 worldN    : TEXCOORD0;
            float3 worldPos  : TEXCOORD1;
            float3 objPos    : TEXCOORD2; // pre-deformation object space: noise anchor
            float4 vertColor : COLOR0;
            LIGHTING_COORDS(3, 4)         // shadow / light cookie coordinates
        };

        // Same shape as Ink/InkVolume: noise displacement along the normal, optional stretch.
        v2f vert(appdata v)
        {
            v2f o;
            float3 pos = v.vertex.xyz;
            float3 shapePosA = pos * (_NoiseFreq * 0.5) + _Time.y * _FlowSpeed * 0.4 + _Seed;
            float3 shapePosB = shapePosA * 1.7 + float3(7.3, 13.1, 5.7);
            float  disp = (valueNoise3(shapePosA) * 0.65 + valueNoise3(shapePosB) * 0.35) * 2.0 - 1.0;
            pos += v.normal * disp * _ShapeAmp;

            float stretch = _Direction.w;
            if (stretch > 1.001)
            {
                float3 localDir = normalize(mul((float3x3)unity_WorldToObject, _Direction.xyz));
                pos += localDir * dot(pos, localDir) * (stretch - 1.0);
            }

            o.objPos    = v.vertex.xyz;
            v.vertex.xyz = pos; // the lighting macros read v.vertex
            o.pos       = UnityObjectToClipPos(v.vertex);
            o.worldN    = UnityObjectToWorldNormal(v.normal);
            o.worldPos  = mul(unity_ObjectToWorld, v.vertex).xyz;
            o.vertColor = v.color;
            TRANSFER_VERTEX_TO_FRAGMENT(o);
            return o;
        }

        // One light's contribution (plus ambient and reflections in the base pass).
        float3 InkShade(v2f i, float3 L, float3 lightColor, bool withIndirect)
        {
            float3 N = normalize(i.worldN);
            float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
            float NdotV = saturate(dot(N, V));
            float4 tint = _Color * i.vertColor;

            float3 np = i.objPos * _NoiseFreq + _Time.y * _FlowSpeed + _Seed;
            float  n0 = valueNoise3(np);
            float  n1 = valueNoise3(np + float3(31.7, 17.3,  5.1));
            float  n2 = valueNoise3(np + float3( 5.1, 31.7, 17.3));
            float3 noiseVec = float3(n0, n1, n2) * 2.0 - 1.0;
            noiseVec -= dot(noiseVec, N) * N;
            float3 Np = normalize(N + noiseVec * _NoiseAmp);

            float3 inkRGB = saturate(tint.rgb + (n0 - 0.5) * _ColorVar);
            half3 specColor; half oneMinusReflectivity;
            half3 diffColor = DiffuseAndSpecularFromMetallic(inkRGB, 0, specColor, oneMinusReflectivity);

            UnityLight light;
            light.color = lightColor;
            light.dir   = L;
            light.ndotl = saturate(dot(Np, L));

            UnityIndirect indirect;
            indirect.diffuse = 0;
            indirect.specular = 0;
            if (withIndirect)
            {
                indirect.diffuse = ShadeSH9(float4(Np, 1.0));
                Unity_GlossyEnvironmentData envData = UnityGlossyEnvironmentSetup(_Gloss, V, Np, specColor);
                indirect.specular = Unity_GlossyEnvironment(UNITY_PASS_TEXCUBE(unity_SpecCube0), unity_SpecCube0_HDR, envData) * _ReflStr;
            }

            float3 color = UNITY_BRDF_PBS(diffColor, specColor, oneMinusReflectivity, _BodySmoothness, Np, V, light, indirect).rgb;

            // Tight, bright liquid highlight (Schlick Fresnel from the BRDF's dielectric F0).
            float F0 = specColor.r;
            float F  = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);
            float  NHdot = saturate(dot(Np, normalize(L + V)));
            float  spec  = pow(NHdot, max(1.0, _Gloss * 1024.0));
            color += spec * lightColor * _SpecTint.rgb * (F * 4.0 + 0.3);
            return color;
        }
        ENDCG

        Pass
        {
            // The main directional light (shadowed), ambient SH and reflections.
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment fragBase
            #pragma multi_compile_fwdbase

            fixed4 fragBase(v2f i) : SV_Target
            {
                UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
                return fixed4(InkShade(i, normalize(_WorldSpaceLightPos0.xyz), _LightColor0.rgb * atten, true), 1.0);
            }
            ENDCG
        }

        Pass
        {
            // Each extra light (point, spot, more directionals), added on top.
            Tags { "LightMode"="ForwardAdd" }
            Blend One One
            ZWrite Off
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment fragAdd
            #pragma multi_compile_fwdadd_fullshadows

            fixed4 fragAdd(v2f i) : SV_Target
            {
                UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
                float3 L = normalize(UnityWorldSpaceLightDir(i.worldPos));
                return fixed4(InkShade(i, L, _LightColor0.rgb * atten, false), 0.0);
            }
            ENDCG
        }
    }
}

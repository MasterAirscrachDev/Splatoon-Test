Shader "Ink/InkVolume"
{
    Properties
    {
        _Color      ("Ink Color",              Color)       = (0.05, 0.15, 0.8, 1)

        [Header(Shape)]
        _Direction  ("Direction (XYZ) + Stretch (W)", Vector) = (0, -1, 0, 1)

        [Header(Volume)]
        _Density    ("Ink Density",            Range(1,30)) = 10
        _Opacity    ("Max Opacity",            Range(0,1))  = 0.97

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
        // Set from code on spawn so every projectile looks different
        _Seed       ("Noise Seed",             Float)       = 0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            // ForwardBase so the pass gets the main light and ambient SH.
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            // Match InkSurface: below 3.0 UNITY_BRDF_PBS falls back to a cheaper BRDF.
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"
            #include "UnityStandardUtils.cginc"
            #include "UnityPBSLighting.cginc"

            float4 _Color;
            float4 _Direction;
            float  _Density, _Opacity;
            float  _Gloss, _ReflStr, _BodySmoothness;
            float4 _SpecTint;
            float  _NoiseFreq, _ShapeAmp, _NoiseAmp, _ColorVar, _FlowSpeed, _Seed;

            // ── 3-D value noise ───────────────────────────────────────────────
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
            // ─────────────────────────────────────────────────────────────────

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
                float3 objPos    : TEXCOORD2; // pre-deformation object space — noise anchor
                float4 vertColor : COLOR0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 pos = v.vertex.xyz;

                // ── Vertex noise displacement (shape) ─────────────────────────
                // Object space, so it is locked to (and scales with) the mesh. _Seed varies each blob.
                float3 shapePosA = pos * (_NoiseFreq * 0.5) + _Time.y * _FlowSpeed * 0.4 + _Seed;
                float3 shapePosB = shapePosA * 1.7 + float3(7.3, 13.1, 5.7);
                float  disp = (valueNoise3(shapePosA) * 0.65
                             + valueNoise3(shapePosB) * 0.35) * 2.0 - 1.0;
                pos += v.normal * disp * _ShapeAmp; // object-space: scales with object

                // ── Direction stretch ─────────────────────────────────────────
                float stretch = _Direction.w;
                if (stretch > 1.001)
                {
                    float3 localDir = normalize(mul((float3x3)unity_WorldToObject, _Direction.xyz));
                    float  proj     = dot(pos, localDir);
                    pos += localDir * proj * (stretch - 1.0);
                }

                o.pos       = UnityObjectToClipPos(float4(pos, 1.0));
                o.worldN    = UnityObjectToWorldNormal(v.normal);
                o.worldPos  = mul(unity_ObjectToWorld, float4(pos, 1.0)).xyz;
                o.objPos    = v.vertex.xyz; // pre-deformation, for fragment noise
                o.vertColor = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 N = normalize(i.worldN);
                float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
                float3 L = normalize(_WorldSpaceLightPos0.xyz);

                float NdotV = saturate(dot(N, V));

                float4 tint = _Color * i.vertColor; // vertColor = particle colour, white for plain meshes

                // ── 3-D fluid noise (object space, seed-offset) ───────────────
                float3 np = i.objPos * _NoiseFreq + _Time.y * _FlowSpeed + _Seed;
                float  n0 = valueNoise3(np);
                float  n1 = valueNoise3(np + float3(31.7, 17.3,  5.1));
                float  n2 = valueNoise3(np + float3( 5.1, 31.7, 17.3));

                float3 noiseVec = float3(n0, n1, n2) * 2.0 - 1.0;
                noiseVec -= dot(noiseVec, N) * N; // project onto surface
                float3 Np = normalize(N + noiseVec * _NoiseAmp);

                // ── Beer-Lambert volume depth ─────────────────────────────────
                float absorption = 1.0 - exp(-NdotV * _Density);

                // ── Body color ────────────────────────────────────────────────
                float  colorShift = (n0 - 0.5) * _ColorVar;
                float3 inkRGB     = saturate(tint.rgb + colorShift);

                // Standard BRDF like InkSurface, so blobs match surface ink colour. Absorption only drives alpha.
                half3 specColor; half oneMinusReflectivity;
                half3 diffColor = DiffuseAndSpecularFromMetallic(inkRGB, 0, specColor, oneMinusReflectivity);

                UnityLight mainLight;
                mainLight.color = _LightColor0.rgb;
                mainLight.dir   = L;
                mainLight.ndotl = saturate(dot(Np, L)); // legacy field; the BRDF computes its own

                // ── Environment reflection (Unity reflection probe / skybox) ──
                // SH ambient like the surface, but a sharp (_Gloss) reflection for the liquid look.
                UnityIndirect indirect;
                indirect.diffuse = ShadeSH9(float4(Np, 1.0));
                Unity_GlossyEnvironmentData envData = UnityGlossyEnvironmentSetup(_Gloss, V, Np, specColor);
                indirect.specular = Unity_GlossyEnvironment(UNITY_PASS_TEXCUBE(unity_SpecCube0), unity_SpecCube0_HDR, envData) * _ReflStr;

                float3 color = UNITY_BRDF_PBS(diffColor, specColor, oneMinusReflectivity, _BodySmoothness, Np, V, mainLight, indirect).rgb;

                // ── Blinn-Phong specular (tight, bright liquid highlight) ─────
                // Schlick Fresnel from the same dielectric F0 the BRDF uses.
                float F0 = specColor.r;
                float F  = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);
                float3 H     = normalize(L + V);
                float  NHdot = saturate(dot(Np, H));
                float  spec  = pow(NHdot, max(1.0, _Gloss * 1024.0));
                color += spec * _LightColor0.rgb * _SpecTint.rgb * (F * 4.0 + 0.3);

                // ── Alpha ─────────────────────────────────────────────────────
                // tint.a folds in the particle's own alpha-over-lifetime fade, if any.
                float alpha = tint.a * _Opacity * absorption;

                return float4(color, alpha);
            }
            ENDCG
        }
    }

    Fallback "Transparent/Diffuse"
}

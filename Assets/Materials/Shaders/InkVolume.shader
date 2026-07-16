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
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "UnityLightingCommon.cginc"

            float4 _Color;
            float4 _Direction;
            float  _Density, _Opacity;
            float  _Gloss, _ReflStr;
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
                // Shuriken writes each particle's current colour (Start Colour / Colour over
                // Lifetime) into this stream automatically for mesh-mode particles — no Custom
                // Vertex Streams setup needed. Meshes with no colour channel (the projectile's
                // static blob) get an implicit white default from Unity, so this is a no-op
                // there and _Color/MaterialPropertyBlock keeps driving the colour as before.
                float4 color  : COLOR;
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
                // Sampled in object space so the pattern is locked to the mesh.
                // Displacement is also in object space so it scales with the object.
                // _Seed offsets the domain so every projectile looks different.
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

                // Combines the material colour with the per-particle vertex colour so a
                // ParticleSystem's Start Colour / Colour over Lifetime can drive the tint
                // directly — for the non-particle projectile mesh, vertColor defaults to
                // white and this is exactly _Color, unchanged from before.
                float4 tint = _Color * i.vertColor;

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
                float3 bodyColor  = inkRGB * absorption * lerp(1.0, 0.65, absorption * 0.4);

                float  NdotL  = saturate(dot(Np, L));
                float3 ambient = UNITY_LIGHTMODEL_AMBIENT.rgb * inkRGB * absorption;
                float3 color   = bodyColor * NdotL * _LightColor0.rgb + ambient;

                // ── Fresnel (Schlick) ─────────────────────────────────────────
                // F0 = 0.08 gives a richer, more saturated reflectance than water's 0.02
                float F  = 0.08 + 0.92 * pow(1.0 - NdotV, 5.0);

                // ── Blinn-Phong specular (tight, bright) ──────────────────────
                float3 H     = normalize(L + V);
                float  NHdot = saturate(dot(Np, H));
                float  spec  = pow(NHdot, max(1.0, _Gloss * 1024.0));
                color += spec * _LightColor0.rgb * _SpecTint.rgb * (F * 4.0 + 0.3);

                // ── Environment reflection (Unity reflection probe / skybox) ──
                // This is the key ingredient that makes it read as a liquid.
                float3 reflDir  = reflect(-V, Np);
                float  roughLOD = (1.0 - _Gloss) * 6.0;
                half4  envSamp  = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, reflDir, roughLOD);
                float3 envColor = DecodeHDR(envSamp, unity_SpecCube0_HDR);
                color += envColor * F * _ReflStr;

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

// FluidRenderer's last passes: the ink, opaque, shaded over the scene (_MainTex, a copy of it), and
// its depth into the camera's. The ink's shape comes from the smoothed thickness (_FluidThick) read
// as a height field, partly from the smoothed depth (_FluidDepth).
// The body is lit as the ink on surfaces is (InkSurface: Unity's Standard BRDF, the same
// smoothness, the scene's sun, ambient probe and reflection), eased towards the paint's own look by
// `shading`, so flying ink and painted ink match; on top, the environment reflected at grazing
// angles, a sheen round the outline (the ink's colour lifted towards white) and a highlight. The
// outline is crisp (a pixel or two of antialiasing).
Shader "Fluid/Composite"
{
    Properties { _MainTex ("", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #pragma target 3.5
        #include "UnityCG.cginc"
        #include "UnityPBSLighting.cginc"
        #include "UnityStandardUtils.cginc"

        sampler2D _MainTex, _FluidDepth, _FluidThick, _FluidRawDepth;
        UNITY_DECLARE_TEXCUBE(_Env);
        float4 _SHAr, _SHAg, _SHAb, _SHBr, _SHBg, _SHBb, _SHC; // the scene's ambient probe (a full-screen pass doesn't get it)
        float4 _EnvHDR;
        float4 _FluidProj;        // P00, P11, P02, P12
        float4 _FluidSize;        // w, h, 1/w, 1/h
        float4x4 _FluidCamToWorld;
        float4 _SunDir, _SunColour;
        float4 _Shape;            // depth normals share, roundness, edge sharpness, debug flat-up
        float4 _Look;             // gloss, specular, reflection, ambient (x the probe)
        float4 _Body;             // x: the body's smoothness (the ink surface's), y: how much its shape shades it
        float4 _Look2;            // brightness, rim strength, rim power, rim whiten
        float4 _Rim;              // x: the thickness (metres) the sheen fades out by, inwards from the outline
        float4 _Noise;            // ripple frequency (per metre), strength, flow speed
        float4 _FluidLayer;       // x: how far apart in depth ink still melts together (metres), y: the blend strength (metres)

        // Thickness is stored as the sum of exp(t/k) - 1 (k = _FluidLayer.y, the blend strength in
        // metres) and read back as k ln(1 + sum): where overlapping drops are thin their thicknesses
        // add up and merge; where they're thicker than k the thicker wins, so stacks don't peak.
        float ThickOf(float stored) { return _FluidLayer.y * log(1.0 + max(stored, 0.0)); }

        float Hash(float3 p)
        {
            p = frac(p * float3(127.1, 311.7, 74.7));
            p += dot(p, p.yzx + 19.19);
            return frac((p.x + p.y) * p.z);
        }
        float Noise(float3 p)
        {
            float3 i = floor(p), f = frac(p), u = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(lerp(Hash(i), Hash(i + float3(1, 0, 0)), u.x), lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), u.x), u.y),
                        lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), u.x), lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), u.x), u.y), u.z);
        }

        // The ambient probe in direction n (Unity's ShadeSH9, with our copy of the coefficients).
        float3 Ambient(float3 n)
        {
            float4 n1 = float4(n, 1.0);
            float3 c = float3(dot(_SHAr, n1), dot(_SHAg, n1), dot(_SHAb, n1));
            float4 vB = n.xyzz * n.yzzx;
            c += float3(dot(_SHBr, vB), dot(_SHBg, vB), dot(_SHBb, vB));
            c += _SHC.rgb * (n.x * n.x - n.y * n.y);
            c = max(c, 0.0);
        #ifdef UNITY_COLORSPACE_GAMMA
            c = LinearToGammaSpace(c);
        #endif
            return c;
        }

        float3 ViewPos(float2 uv, float depth)
        {
            float2 ndc = uv * 2.0 - 1.0;
            return float3((ndc - _FluidProj.zw) / _FluidProj.xy * depth, -depth);
        }

        // The ink at view-space point p, with view-space normal n and colour `ink`, `thickness`
        // metres thick there (for the sheen near its outline): lit.
        float3 Shade(float3 p, float3 n, float3 ink, float thickness)
        {
            float3 N = normalize(mul((float3x3)_FluidCamToWorld, n));
            if (_Shape.w > 0.5) N = float3(0, 1, 0); // debug: lit as a flat floor is (colour matching)
            float3 V = normalize(mul((float3x3)_FluidCamToWorld, -normalize(p)));

            // Optional ripples: the normal nudged by flowing noise, anchored in the world.
            if (_Noise.y > 0)
            {
                float3 q = mul(_FluidCamToWorld, float4(p, 1.0)).xyz * _Noise.x + _Time.y * _Noise.z;
                float3 ripple = float3(Noise(q), Noise(q + float3(31.7, 17.3, 5.1)), Noise(q + float3(5.1, 31.7, 17.3))) * 2.0 - 1.0;
                ripple -= dot(ripple, N) * N;
                N = normalize(N + ripple * _Noise.y);
            }

            float gloss = _Look.x, specular = _Look.y, reflection = _Look.z, ambient = _Look.w;
            float brightness = _Look2.x, rimStrength = _Look2.y, rimPower = _Look2.z, rimWhiten = _Look2.w;

            // The body, lit as the ink on surfaces is (InkSurface's Standard BRDF and smoothness)...
            float3 L = normalize(_SunDir.xyz);
            half3 specColour; half oneMinusReflectivity;
            half3 diffColour = DiffuseAndSpecularFromMetallic(ink * brightness, 0, specColour, oneMinusReflectivity);
            UnityLight sun;
            sun.color = _SunColour.rgb;
            sun.dir = L;
            sun.ndotl = saturate(dot(N, L));
            UnityIndirect indirect;
            indirect.diffuse = Ambient(N) * ambient;
            Unity_GlossyEnvironmentData bodyEnv = UnityGlossyEnvironmentSetup(_Body.x, V, N, specColour);
            indirect.specular = Unity_GlossyEnvironment(UNITY_PASS_TEXCUBE(_Env), _EnvHDR, bodyEnv);
            float3 col = UNITY_BRDF_PBS(diffColour, specColour, oneMinusReflectivity, _Body.x, N, V, sun, indirect).rgb;
            // ...eased towards the ink lit as the paint is (facing up, seen from above: from low down a
            // glossy face reflects the sky at a grazing angle and washes out, which ink in the air
            // shouldn't), so its shape shows only as much as `shading` asks.
            float3 up = float3(0, 1, 0);
            UnityLight flatSun = sun;
            flatSun.ndotl = saturate(L.y);
            UnityIndirect flatIndirect;
            flatIndirect.diffuse = Ambient(up) * ambient;
            Unity_GlossyEnvironmentData flatEnv = UnityGlossyEnvironmentSetup(_Body.x, up, up, specColour);
            flatIndirect.specular = Unity_GlossyEnvironment(UNITY_PASS_TEXCUBE(_Env), _EnvHDR, flatEnv);
            float3 flat = UNITY_BRDF_PBS(diffColour, specColour, oneMinusReflectivity, _Body.x, up, up, flatSun, flatIndirect).rgb;
            col = lerp(flat, col, _Body.y);

            // The liquid's extra gloss: the environment sharply reflected at grazing angles.
            float facing = saturate(dot(N, V));
            float F = 0.04 + 0.96 * pow(1.0 - facing, 5.0);
            float3 env = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(_Env, reflect(-V, N), (1.0 - gloss) * 6.0), _EnvHDR);
            col = lerp(col, env, saturate(F * reflection));

            // The sheen round the edges: the ink lifted towards white, in a band where it thins to
            // the outline (thickness, not the normal's angle: tilted faces aren't edges).
            float rim = saturate(pow(saturate(1.0 - thickness / _Rim.x), rimPower) * rimStrength);
            col = lerp(col, lerp(ink * brightness, float3(1, 1, 1), rimWhiten), rim);

            // The highlight.
            float3 H = normalize(L + V);
            col += _SunColour.rgb * pow(saturate(dot(N, H)), exp2(gloss * 10.0 + 1.0)) * specular;
            return col;
        }

        ENDCG

        // 0: shaded over the scene.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag

            // The thickness beside us, or ours if that's other ink at another depth (in front or
            // behind): its outline isn't our slope.
            float ThicknessBeside(float2 uv, float raw, float t)
            {
                float s = tex2D(_FluidRawDepth, uv).r;
                return (s > 0 && raw > 0 && abs(s - raw) > max(_FluidLayer.x, t * 0.5)) ? t : ThickOf(tex2D(_FluidThick, uv).a);
            }

            // The smaller step to a neighbour on either side (the other side may be off the ink, or a
            // different surface behind).
            float3 Slope(float2 uv, float3 p, float2 step)
            {
                float da = tex2D(_FluidDepth, uv + step).r, db = tex2D(_FluidDepth, uv - step).r;
                float3 pa = ViewPos(uv + step, da), pb = ViewPos(uv - step, db);
                bool ha = da > 0, hb = db > 0;
                if (ha && hb) return abs(pa.z - p.z) < abs(p.z - pb.z) ? pa - p : p - pb;
                if (ha) return pa - p;
                if (hb) return p - pb;
                return float3(0, 0, 0);
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float4 scene = tex2D(_MainTex, i.uv);
                float d = tex2D(_FluidDepth, i.uv).r;
                if (d <= 0) return scene;
                float4 tc = tex2D(_FluidThick, i.uv);
                float t = ThickOf(tc.a);
                float2 tx = float2(_FluidSize.z, 0), ty = float2(0, _FluidSize.w);

                // Shape: from the thickness (half of it as a height, per metre across the screen here)...
                float metresX = d * 2.0 / (_FluidProj.x * _FluidSize.x), metresY = d * 2.0 / (_FluidProj.y * _FluidSize.y);
                float raw = tex2D(_FluidRawDepth, i.uv).r;
                float dhdx = (ThicknessBeside(i.uv + tx, raw, t) - ThicknessBeside(i.uv - tx, raw, t)) * 0.25 / metresX;
                float dhdy = (ThicknessBeside(i.uv + ty, raw, t) - ThicknessBeside(i.uv - ty, raw, t)) * 0.25 / metresY;
                float3 fromThickness = normalize(float3(-dhdx * _Shape.y, -dhdy * _Shape.y, 1.0));
                // ...and the depth.
                float3 p = ViewPos(i.uv, d);
                float3 sx = Slope(i.uv, p, tx), sy = Slope(i.uv, p, ty);
                float3 fromDepth = (dot(sx, sx) > 0 && dot(sy, sy) > 0) ? normalize(cross(sx, sy)) : float3(0, 0, 1);
                if (fromDepth.z < 0) fromDepth = -fromDepth;           // facing the camera (+z in view space)
                float3 n = normalize(lerp(fromThickness, fromDepth, _Shape.x));

                float3 ink = tc.a > 1e-12 ? tc.rgb / tc.a : float3(1, 1, 1);
                float3 col = Shade(p, n, ink, t);
                float cover = saturate(t * _Shape.z); // opaque, with a crisp antialiased edge
                return float4(lerp(scene.rgb, col, cover), scene.a);
            }
            ENDCG
        }

        // 1: the ink's depth into the camera's depth buffer (where it's opaque), so what's
        // drawn after it (transparent things: the Inkstrike's column) is hidden behind it.
        Pass
        {
            ZWrite On ZTest LEqual ColorMask 0
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragDepth
            float4 fragDepth(v2f_img i, out float z : SV_Depth) : SV_Target
            {
                float d = tex2D(_FluidDepth, i.uv).r;
                if (d <= 0 || ThickOf(tex2D(_FluidThick, i.uv).a) * _Shape.z < 0.5) discard;
                z = (1.0 / d - _ZBufferParams.w) / _ZBufferParams.z; // eye depth to the buffer's
                return 0;
            }
            ENDCG
        }
    }
}

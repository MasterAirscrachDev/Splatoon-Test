// FluidRenderer's smoothing.
// Pass 0, depth: a depth-aware blur in 2D, sized in world space (_FluidBlur.x pixels at 1m, so the
// same world radius at any distance), which can also bridge small gaps (an empty pixel with ink
// close on opposite sides is filled), so nearby drops melt into one surface.
// Pass 1, thickness + colour, one direction at a time (_FluidStep): a blur at the same world size (sized by the smoothed depth), so
// bridged gaps get ink too and colours blend where teams' drops meet.
// None of them mix ink more than _FluidLayer.x apart in depth (or half the ink's thickness there, for
// big blobs), or bridge between such: a drop in
// front of another keeps its own outline, shape and colour.
Shader "Fluid/Smooth"
{
    Properties { _MainTex ("", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        sampler2D _FluidDepth;
        float4 _FluidStep;  // xy: one texel along the direction (pass 1)
        float4 _FluidSize;  // w, h, 1/w, 1/h
        float4 _FluidBlur;  // x: blur radius in pixels at 1m, y: depth falloff (per m²), z: max radius (pixels), w: gap bridging (share of the radius; 0: none)

        float RadiusAt(float depth) { return clamp(_FluidBlur.x / max(depth, 0.05), 1.0, _FluidBlur.z); }
        float4 _FluidLayer; // x: how far apart in depth (metres) ink can be and still melt together
        float Close(float dz, float layer) { return 1.0 - smoothstep(0.5, 1.0, abs(dz) / layer); }
        sampler2D _FluidThick;
        sampler2D _FluidRawDepth; // the depth before smoothing (crisp where ink at different depths meets)
        // How far apart in depth ink here still melts: the setting, or half the ink's thickness here
        // (a big blob's own drops can be further apart than small ones').
        float Layer(float thickness) { return max(_FluidLayer.x, thickness * 0.5); }
        // Thickness is stored as the sum of exp(t/k) - 1 (k = _FluidLayer.y, the blend strength in
        // metres) and read back as k ln(1 + sum): where overlapping drops are thin (their edges, the
        // necks between neighbours) their thicknesses add up and merge into one body; where they're
        // thicker than k (one drop's middle stacked on another's along the view) the thicker wins,
        // so stacks don't peak into a point.
        float ThickOf(float stored) { return _FluidLayer.y * log(1.0 + max(stored, 0.0)); }

        ENDCG

        // 0: depth, in 2D: 48 samples on a spiral (Vogel) disc, turned per pixel by a little noise,
        // so its sampling leaves fine noise (ironed out by pass 2) rather than a grid. (A blur one
        // direction at a time leaks along rows and columns where drops overlap: jagged teeth.)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            static const int Samples = 48;
            float frag(v2f_img i) : SV_Target
            {
                float d = tex2D(_MainTex, i.uv).r;
                float r = d > 0 ? RadiusAt(d) : _FluidBlur.z * _FluidBlur.w; // an empty pixel looks only nearby (and then only to bridge)
                if (r < 0.5) return d;
                float2 pixel = i.uv * _FluidSize.xy;
                float turn = d > 0 ? frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715)))) * 6.2831853 : 0; // interleaved gradient noise (not when bridging: noisy edges)
                float sigma = max(r * 0.5, 0.5);
                float sum = d > 0 ? d : 0, weight = d > 0 ? 1 : 0;
                float2 around = 0;                    // where the ink is, on average (to bridge a gap: ink all round, so it cancels out)
                float nearest = 1e6, furthest = 0;    // (and bridging only between ink at about one depth)
                float layer = Layer(ThickOf(tex2Dlod(_FluidThick, float4(i.uv, 0, 0)).a));
                [loop] for (int k = 0; k < Samples; k++)
                {
                    float a = k * 2.3999632 + turn;
                    float2 o = float2(cos(a), sin(a)) * (r * sqrt((k + 0.5) / Samples));
                    float s = tex2Dlod(_MainTex, float4(i.uv + o * _FluidSize.zw, 0, 0)).r;
                    if (s <= 0) continue;
                    float r2 = dot(o, o);
                    if (d <= 0 && r2 > pow(RadiusAt(s) * _FluidBlur.w, 2)) continue; // bridging: only close to that ink
                    float w = exp(-r2 / (2.0 * sigma * sigma));
                    if (d > 0) { float dz = s - d; w *= exp(-dz * dz * _FluidBlur.y) * Close(dz, layer); }
                    if (w <= 0) continue;
                    nearest = min(nearest, s); furthest = max(furthest, s);
                    sum += s * w; weight += w;
                    around += o / max(sqrt(r2), 1e-3) * w;
                }
                if (d <= 0) return (_FluidBlur.w > 0 && weight > 0 && length(around) < 0.35 * weight && furthest - nearest < layer) ? sum / weight : 0; // only between ink, not beside it
                return sum / weight;
            }
            ENDCG
        }

        // 1: thickness + colour
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f_img i) : SV_Target
            {
                float d = tex2D(_FluidDepth, i.uv).r;
                float4 c = tex2D(_MainTex, i.uv);
                if (d <= 0) return c;
                float r = RadiusAt(d) * 0.75;
                float layer = Layer(ThickOf(c.a));
                float raw = tex2D(_FluidRawDepth, i.uv).r;
                int reach = (int)ceil(r);
                float sigma = max(r * 0.5, 0.5);
                float4 sum = 0; float weight = 0;
                [loop] for (int k = -reach; k <= reach; k++)
                {
                    float2 uv = i.uv + _FluidStep.xy * k;
                    float s = tex2Dlod(_FluidRawDepth, float4(uv, 0, 0)).r; // (the smoothed depth is noisy at depth jumps: streaks)
                    float w = exp(-k * k / (2.0 * sigma * sigma)) * (s > 0 && raw > 0 ? Close(s - raw, layer) : 1.0); // (empty: rounds the outline)
                    sum += tex2Dlod(_MainTex, float4(uv, 0, 0)) * w;
                    weight += w;
                }
                return sum / weight;
            }
            ENDCG
        }

        // 2: depth, a last light 3 x 3 smooth: irons out the sparse disc's sampling grid.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            float frag(v2f_img i) : SV_Target
            {
                float d = tex2D(_MainTex, i.uv).r;
                if (d <= 0) return 0;
                float sum = 0, weight = 0;
                float layer = Layer(ThickOf(tex2D(_FluidThick, i.uv).a));
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    float s = tex2D(_MainTex, i.uv + float2(x, y) * _FluidSize.zw).r;
                    if (s <= 0) continue;
                    float w = (x == 0 ? 2.0 : 1.0) * (y == 0 ? 2.0 : 1.0) * exp(-(s - d) * (s - d) * _FluidBlur.y) * Close(s - d, layer);
                    sum += s * w; weight += w;
                }
                return sum / weight;
            }
            ENDCG
        }
    }
}

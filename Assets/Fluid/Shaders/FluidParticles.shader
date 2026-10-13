// Ink drops as sphere impostors (camera-facing quads shaded as spheres), drawn procedurally from
// FluidRenderer's buffer (one instance per drop). Hidden behind the scene (its depth texture).
// Pass 0: the nearest drop surface's eye depth (with the true sphere depth, so drops overlap right).
// Pass 1: thickness and colour, added up: rgb = colour x thickness, a = thickness (metres). The
// thickness falls off as (1 - r²)^1.5 rather than a sphere's sqrt(1 - r²): flat at the rim, so drops
// overlapping in a line add up to a smooth surface (a sphere's steep rim leaves creases), but still
// domed like a sphere ((1 - r²)² looked pointed) - it's read as the ink's shape (FluidComposite).
Shader "Fluid/Particles"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Off

        CGINCLUDE
        #pragma target 4.5
        #include "UnityCG.cginc"

        struct Particle { float4 posRadius; float4 colour; };
        StructuredBuffer<Particle> _Particles;
        UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
        float4 _FluidSize; // w, h, 1/w, 1/h
        float _FluidMinPixels; // drops are at least this many pixels across their radius (tinier ones break up into crosses)

        struct v2f
        {
            float4 pos    : SV_POSITION;
            float2 corner : TEXCOORD0;  // -1..1 across the quad
            float3 centre : TEXCOORD1;  // the drop's centre, view space
            float  radius : TEXCOORD2;
            float4 colour : TEXCOORD3;
        };

        v2f vert(float4 vertex : POSITION, uint id : SV_InstanceID)
        {
            Particle p = _Particles[id];
            v2f o;
            o.centre = mul(UNITY_MATRIX_V, float4(p.posRadius.xyz, 1.0)).xyz;
            float pixelsPerMetre = UNITY_MATRIX_P[1][1] * _FluidSize.y * 0.5 / max(-o.centre.z, 0.01);
            o.radius = max(p.posRadius.w, _FluidMinPixels / pixelsPerMetre);
            o.corner = vertex.xy;
            float3 corner = o.centre + float3(vertex.xy * o.radius, o.radius); // the quad in front of the sphere, facing us
            o.pos = mul(UNITY_MATRIX_P, float4(corner, 1.0));
            o.colour = p.colour;
            return o;
        }

        // The sphere under this pixel: its view-space surface point and how far through it is (0 at
        // the rim, 1 at the middle). False off the sphere or behind the scene.
        bool Surface(v2f i, out float3 viewPos, out float through)
        {
            float r2 = dot(i.corner, i.corner);
            viewPos = 0; through = 0;
            if (r2 > 1.0) return false;
            through = sqrt(1.0 - r2);
            viewPos = i.centre + float3(i.corner * i.radius, through * i.radius); // view space: +z towards the camera
            float2 uv = i.pos.xy * _FluidSize.zw;
            float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
            return -viewPos.z < scene; // in front of the scene
        }
        ENDCG

        // 0: depth
        Pass
        {
            ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float frag(v2f i, out float depth : SV_Depth) : SV_Target
            {
                float3 viewPos; float through;
                if (!Surface(i, viewPos, through)) discard;
                float4 clip = mul(UNITY_MATRIX_P, float4(viewPos, 1.0));
                depth = clip.z / clip.w;
                return -viewPos.z; // eye depth
            }
            ENDCG
        }

        // 1: thickness and colour
        Pass
        {
            ZWrite Off ZTest Always
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float3 viewPos; float through;
                if (!Surface(i, viewPos, through)) discard;
                float thickness = 2.0 * i.radius * through * through * through; // (1 - r²)^1.5
                return float4(i.colour.rgb * thickness, thickness);
            }
            ENDCG
        }
    }
}

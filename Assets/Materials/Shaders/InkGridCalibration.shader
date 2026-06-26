Shader "Ink/GridCalibration"
{
    Properties
    {
        _PixelsPerUnit ("Pixels Per Unit", Float) = 32
        _GridColor    ("Grid Line Color",  Color)  = (0.15, 0.45, 1, 1)
        _CellA        ("Cell Color A",     Color)  = (0.85, 0.85, 0.85, 1)
        _CellB        ("Cell Color B",     Color)  = (0.65, 0.65, 0.65, 1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float  _PixelsPerUnit;
            float4 _GridColor, _CellA, _CellB;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos         : SV_POSITION;
                float3 worldPos    : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos         = UnityObjectToClipPos(v.vertex);
                o.worldPos    = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Pick the two world-space axes that lie on this surface.
                // Dominant normal component identifies the "depth" axis to drop.
                float3 n = abs(normalize(i.worldNormal));
                float2 gp;
                if (n.y >= n.x && n.y >= n.z)
                    gp = float2(i.worldPos.x, i.worldPos.z);   // floor / ceiling  (drop Y)
                else if (n.z >= n.x)
                    gp = float2(i.worldPos.x, i.worldPos.y);   // wall facing ±Z   (drop Z)
                else
                    gp = float2(i.worldPos.z, i.worldPos.y);   // wall facing ±X   (drop X)

                // Scale so one grid cell == one ink pixel at the chosen density.
                gp *= _PixelsPerUnit;

                // Checkerboard fill — alternating cells help count pixels by eye.
                float2 cell    = floor(gp);
                float checker  = fmod(abs(cell.x + cell.y), 2.0);
                float4 fill    = checker < 1.0 ? _CellA : _CellB;

                // Anti-aliased grid lines using screen-space derivatives.
                // distToLine is 0 at each integer boundary, 0.5 at cell centres.
                float2 distToLine = abs(frac(gp) - 0.5);
                float2 fw         = fwidth(gp);

                // Smooth 1-pixel line: full intensity at the boundary, zero half a pixel away.
                float lineX   = 1.0 - smoothstep(0.0, fw.x * 0.5, distToLine.x);
                float lineY   = 1.0 - smoothstep(0.0, fw.y * 0.5, distToLine.y);
                float gridVal = max(lineX, lineY);

                return lerp(fill, _GridColor, gridVal);
            }
            ENDCG
        }
    }
}

// The storm's fog as a volume (see StormFog.cs), compiled in game by Windows' d3dcompiler_47 for shader model 3. One pass over the
// screen: each pixel's position comes back from the scene's depth (SceneDepth), and the line from the camera to it is stepped through the
// ring, adding up the fog's density along it. Render space: x east, y up, z north.

float4 Vs(float4 position : POSITION) : POSITION
{
    return position;
}

row_major float4x4 InverseViewProj : register(c0); // clip space back to render space
float4 Eye : register(c4);       // xyz: the camera
float4 Ring : register(c5);      // xy: the safe landblock's centre (east, north); z: the ring's inner half-width; w: its outer
float4 Span : register(c6);      // x: the fog's bottom; y: its top; z: its full density per metre; w: how far the sky is taken to be
float4 Colour : register(c7);
float4 View3d : register(c8);    // the 3D view's viewport: x, y, width, height in pixels
float4 Screen : register(c9);    // xy: the depth texture's (the screen's) size in pixels
float4 Edge : register(c10);     // x: metres over which the fog thins to the ring's edges; y: below its top; zw: the wisps' drift
sampler Depth : register(s0);
sampler Noise : register(s1);
static const int Steps = 24;

// The fog's density per metre at a point: full inside the ring, thinning over the last Edge.x metres to each of its edges and below its top,
// and stirred by drifting wisps (the noise texture's alpha, 0 to 1).
float Density(float3 p)
{
    float d = max(abs(p.x - Ring.x), abs(p.z - Ring.y));
    float edges = smoothstep(0, Edge.x, d - Ring.z) * smoothstep(0, Edge.x, Ring.w - d) * smoothstep(Span.y, Span.y - Edge.y, p.y);
    float wisps = tex2Dlod(Noise, float4(p.xz / 160 + Edge.zw, 0, 0)).a;
    return Span.z * edges * (0.5 + wisps);
}

float4 Fog(float2 pixel : VPOS) : COLOR
{
    float depth = tex2D(Depth, (pixel + 0.5) / Screen.xy).r;
    float2 view = (pixel + 0.5 - View3d.xy) / View3d.zw;
    float4 scene = mul(float4(view.x * 2 - 1, 1 - view.y * 2, depth, 1), InverseViewProj);
    float3 ray = scene.xyz / scene.w - Eye.xyz;
    // The sky (nothing drawn, so depth is at the far plane) and anything past the sky's distance: as far as the sky.
    if (depth >= 0.99999 || length(ray) > Span.w) ray = normalize(ray) * Span.w;
    // Only the part of the line inside the ring's outer box can be fogged: step through that, from a point jittered per pixel so the
    // steps do not show as bands.
    float3 outer = float3(Ring.w, 0, Ring.w), centre = float3(Ring.x, 0, Ring.y);
    float3 low = centre - outer + float3(0, Span.x, 0), high = centre + outer + float3(0, Span.y, 0);
    float3 inverse = 1 / (ray + (ray == 0) * 1e-6);
    float3 t0 = (low - Eye.xyz) * inverse, t1 = (high - Eye.xyz) * inverse;
    float3 near = min(t0, t1), far = max(t0, t1);
    float enter = max(max(near.x, near.y), max(near.z, 0)), leave = min(min(far.x, far.y), min(far.z, 1));
    if (leave <= enter) return 0;
    float step = (leave - enter) / Steps;
    float jitter = frac(sin(dot(pixel, float2(12.9898, 78.233))) * 43758.5453);
    float optical = 0;
    for (int i = 0; i < Steps; i++)
        optical += Density(Eye.xyz + ray * (enter + (i + jitter) * step));
    return float4(Colour.rgb, 1 - exp(-optical * step * length(ray)));
}

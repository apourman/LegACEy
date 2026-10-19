// The storm's fog as a volume (see StormFog.cs), compiled in game by Windows' d3dcompiler_47 for shader model 3. One pass over the
// screen: each pixel's position comes back from the scene's depth (SceneDepth), and it is fogged by how far the line from the camera to
// it runs through the ring, 1 - e^(-metres × Density). Render space: x east, y up, z north.

float4 Vs(float4 position : POSITION) : POSITION
{
    return position;
}

row_major float4x4 InverseViewProj : register(c0); // clip space back to render space
float4 Eye : register(c4);       // xyz: the camera
float4 Ring : register(c5);      // xy: the safe landblock's centre (east, north); z: the ring's inner half-width; w: its outer
float4 Span : register(c6);      // x: the fog's bottom; y: its top; z: Density per metre; w: how far the sky is taken to be
float4 Colour : register(c7);
float4 View3d : register(c8);    // the 3D view's viewport: x, y, width, height in pixels
float4 Screen : register(c9);    // xy: the depth texture's (the screen's) size in pixels
sampler Depth : register(s0);

// Metres of the segment from a to b inside the box from low to high.
float Inside(float3 a, float3 b, float3 low, float3 high)
{
    float3 d = b - a;
    float3 inverse = 1 / (d + (d == 0) * 1e-6);
    float3 t0 = (low - a) * inverse, t1 = (high - a) * inverse;
    float3 near = min(t0, t1), far = max(t0, t1);
    float enter = max(max(near.x, near.y), max(near.z, 0)), leave = min(min(far.x, far.y), min(far.z, 1));
    return max(0, leave - enter) * length(d);
}

float4 Fog(float2 pixel : VPOS) : COLOR
{
    float depth = tex2D(Depth, (pixel + 0.5) / Screen.xy).r;
    float2 view = (pixel + 0.5 - View3d.xy) / View3d.zw;
    float4 scene = mul(float4(view.x * 2 - 1, 1 - view.y * 2, depth, 1), InverseViewProj);
    float3 ray = scene.xyz / scene.w - Eye.xyz;
    // The sky (nothing drawn, so depth is at the far plane) and anything past the sky's distance: as far as the sky.
    if (depth >= 0.99999 || length(ray) > Span.w) ray = normalize(ray) * Span.w;
    float3 end = Eye.xyz + ray;
    float3 outer = float3(Ring.w, 0, Ring.w), inner = float3(Ring.z, 0, Ring.z);
    float3 centre = float3(Ring.x, 0, Ring.y), bottom = float3(0, Span.x, 0), top = float3(0, Span.y, 0);
    float metres = Inside(Eye.xyz, end, centre - outer + bottom, centre + outer + top) - Inside(Eye.xyz, end, centre - inner + bottom, centre + inner + top);
    return float4(Colour.rgb, 1 - exp(-metres * Span.z));
}

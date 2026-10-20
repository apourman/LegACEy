// LegACEy's snow and rain (see Weather.cs), compiled at run time by Windows' d3dcompiler_47 for shader model 3. Every drop sits in one
// vertex buffer written once; this moves it, so a frame costs the same few constants and one draw whatever the weather. The maths is
// Weather.DrawDrops': game space is z-up, render space y-up (Render swaps y and z).

row_major float4x4 WorldViewProj : register(c0);
float4 Eye : register(c4);      // xyz: the camera (game space); w: seconds
float4 Middle : register(c5);   // xyz: the drop box's middle (game space); w: metres around the camera with no drops
float4 Box : register(c6);      // xyz: the drop box's size; w: drop size, metres
float4 Fall : register(c7);     // xy: wind, metres a second; z: fall speed; w: sway, metres
float4 Area : register(c8);     // xy: the weather area's centre; z: its reach from there; w: seconds of fall drawn as a streak (0: flakes)
float4 Right : register(c9);    // xyz: the view's right (render space)
float4 Up : register(c10);      // xyz: the view's up (render space)
float4 Colour : register(c11);  // the drops' colour and alpha

struct Pixel
{
    float4 Position : POSITION;
    float4 Colour : COLOR0;
    float2 Dot : TEXCOORD0;
};

float3 Render(float3 game) { return game.xzy; }

// start: where the drop began, in the box; drop: x speed, y phase; scale.x: size; corner: this vertex's corner of the quad, 0 or 1 each way.
Pixel Vs(float3 start : POSITION, float2 drop : TEXCOORD0, float2 scale : TEXCOORD1, float2 corner : TEXCOORD2)
{
    Pixel pixel;
    float time = Eye.w;
    float3 velocity = float3(Fall.xy * drop.x, -Fall.z * drop.x);
    // The copy of the drop, in the box-sized repeating lattice, that falls in the box around the middle.
    float3 from = Middle.xyz - Box.xyz / 2;
    float3 position = start + velocity * time - from;
    position = from + position - floor(position / Box.xyz) * Box.xyz;
    float sway = sin(time * 1.3 * drop.x + drop.y) * Fall.w;
    position += float3(sway, sway * 0.6, 0);
    // Outside the weather's square, or within the clear around the camera (where a drop would smear across the screen or, indoors, fall in
    // the room; it fades out within a metre of it).
    float2 edge = abs(position.xy - Area.xy);
    float near = saturate((distance(position, Eye.xyz) - Middle.w) / 0.7);
    if (Area.z - max(edge.x, edge.y) < 0 || near <= 0)
    {
        // Past the far plane: clipped, so the quad draws nothing.
        pixel.Position = float4(0, 0, 2, 1);
        pixel.Colour = 0;
        pixel.Dot = 0;
        return pixel;
    }
    float3 at = Render(position);
    float size = Box.w * scale.x;
    float3 a, b, side;
    if (Area.w > 0)
    {
        // A streak from where the drop was a moment ago to where it is, as wide as the drop, turned to face the camera.
        float3 trail = Render(velocity * Area.w);
        a = at - trail;
        b = at;
        side = normalize(cross(trail, at - Render(Eye.xyz))) * size;
    }
    else
    {
        a = at + Up.xyz * size;
        b = at - Up.xyz * size;
        side = Right.xyz * size;
    }
    pixel.Position = mul(float4(lerp(a, b, corner.y) + side * (corner.x * 2 - 1), 1), WorldViewProj);
    pixel.Colour = float4(Colour.rgb, Colour.a * near);
    pixel.Dot = corner;
    return pixel;
}

// A soft white dot: alpha falls from the middle to nothing at the edge.
sampler DotTexture : register(s0);

float4 Ps(float4 colour : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    return float4(colour.rgb, colour.a * tex2D(DotTexture, uv).a);
}

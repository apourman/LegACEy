// LegACEy's water (see Water.cs), compiled at run time by Windows' d3dcompiler_47 for shader model 3. Positions are in render space:
// x east, y up, z north. Two passes over the same triangles, both scaled by what retail drew, so its lighting (day, night, shade) holds:
//   Tint:  blended to 2 × output × retail's colour, so mid grey leaves retail's water as it was.
//   Light: blended to retail's colour × (1 + output), for sun glints and foam, which stay dim when retail's water is dark.

row_major float4x4 WorldViewProj : register(c0);

struct Pixel
{
    float4 Position : POSITION;
    float4 Colour : COLOR0;    // a: how much of the effect shows (the shore, the distance fade and the strength)
    float4 Waves : TEXCOORD0;  // the two wave layers' texture coordinates
    float3 World : TEXCOORD1;
    float2 Shore : TEXCOORD2;  // x: 1 on water, 0 at a dry corner; y: how open the water around is
};

Pixel Vs(float3 position : POSITION, float4 colour : COLOR0, float2 a : TEXCOORD0, float2 b : TEXCOORD1, float2 shore : TEXCOORD2)
{
    Pixel pixel;
    pixel.Position = mul(float4(position, 1), WorldViewProj);
    pixel.Colour = colour;
    pixel.Waves = float4(a, b);
    pixel.World = position;
    pixel.Shore = shore;
    return pixel;
}

// rg: the waves' slope east and north, 0.5 flat; b: their height, 0.5 the mean. Its mip levels average to flat, so far water is calm.
sampler Waves : register(s0);
float4 Eye : register(c0);      // xyz: the camera
float4 Sun : register(c1);      // xyz: towards the sun; w: how far past the designed look the tint is pushed (1 = as designed)
float4 Deep : register(c2);
float4 Shallow : register(c3);
float4 Time : register(c4);     // x: seconds
static const float Bump = 0.9;

float3 Normal(float4 waves, out float height)
{
    float3 a = tex2D(Waves, waves.xy).rgb - 0.5, b = tex2D(Waves, waves.zw).rgb - 0.5;
    height = a.b + 0.7 * b.b;
    float2 slope = (a.rg + 0.7 * b.rg) * 2 * Bump;
    return normalize(float3(-slope.x, 1, -slope.y));
}

float Glint(float3 normal, float3 view)
{
    return pow(saturate(dot(normal, normalize(view + Sun.xyz))), 180) * 3;
}

float4 Tint(Pixel pixel) : COLOR
{
    float height;
    float3 normal = Normal(pixel.Waves, height);
    float3 view = normalize(Eye.xyz - pixel.World);
    // Schlick's fresnel: water reflects more of the sky the lower the angle it is seen at.
    float fresnel = 0.02 + 0.98 * pow(1 - saturate(dot(normal, view)), 5);
    // The ripples' own light and shade around retail's flat lighting: a flat surface is left as it was.
    float shade = 1 + 1.6 * (dot(normal, Sun.xyz) - Sun.y);
    float3 colour = lerp(Shallow.rgb, Deep.rgb, pixel.Shore.y) * (0.5 + 0.35 * fresnel) * shade + 0.25 * Glint(normal, view);
    colour = 0.5 + (colour - 0.5) * Sun.w;
    return float4(lerp(0.5, colour, pixel.Colour.a), 1);
}

float4 Light(Pixel pixel) : COLOR
{
    float height;
    float3 normal = Normal(pixel.Waves, height);
    float3 view = normalize(Eye.xyz - pixel.World);
    // Foam where the water meets the shore, on the wave crests, drifting in and out.
    float edge = pixel.Shore.x - 0.12 * sin(Time.x * 0.9 + 0.05 * (pixel.World.x + pixel.World.z));
    float band = saturate(1 - abs(edge - 0.55) / 0.3);
    float foam = band * smoothstep(-0.05, 0.25, height + 0.3 * band - 0.15);
    return float4((Glint(normal, view) + 0.8 * foam).xxx * pixel.Colour.a, 1);
}

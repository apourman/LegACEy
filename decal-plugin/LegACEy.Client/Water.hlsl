// LegACEy's water (see Water.cs), compiled at run time by Windows' d3dcompiler_47 for shader model 3. Positions are in render space:
// x east, y up, z north. Two passes over the same triangles, both scaled by what retail drew, so its lighting (day, night, shade) holds:
//   Tint:  blended to 2 × output × retail's colour, so mid grey leaves retail's water as it was.
//   Light: blended to retail's colour × (1 + output), for sun glints and foam, which stay dim when retail's water is dark.

row_major float4x4 WorldViewProj : register(c0);
float4 Camera : register(c4);   // xyz: the camera
float4 Swell : register(c5);    // x: seconds; y: strength (0 to 2); z: metres from the camera where the swells have faded out

struct Pixel
{
    float4 Position : POSITION;
    float4 Colour : COLOR0;    // a: how much of the effect shows (the shore, the distance fade and the strength)
    float4 Waves : TEXCOORD0;  // the two wave layers' texture coordinates
    float3 World : TEXCOORD1;
    float2 Shore : TEXCOORD2;  // x: 1 on water, 0 at a dry corner; y: how open the water around is
    float3 Swell : TEXCOORD3;  // the swells' normal
};

// Swells: Gerstner waves (direction east and north, wavelength in metres, height in metres). They only ever rise: retail's water is
// the floor, as anything drawn below it would be hidden behind it.
static const float4 Swells[4] = { float4(0.8, 0.6, 38, 0.28), float4(0.5, 0.86, 23, 0.18), float4(0.97, -0.26, 15, 0.10), float4(0.2, 0.98, 9, 0.05) };

Pixel Vs(float3 position : POSITION, float4 colour : COLOR0, float2 a : TEXCOORD0, float2 b : TEXCOORD1, float2 shore : TEXCOORD2)
{
    Pixel pixel;
    // Full near the camera, gone by Swell.z; small in the shallows and nothing at the shore.
    float reach = saturate((Swell.z - length(position.xz - Camera.xz)) / 40);
    float scale = reach * shore.x * (0.25 + 0.75 * shore.y) * Swell.y;
    float3 normal = float3(0, 1, 0);
    for (int i = 0; i < 4; i++)
    {
        float2 direction = Swells[i].xy;
        float k = 6.2832 / Swells[i].z, height = Swells[i].w * scale;
        // Deep-water speed, slowed a little for lake-sized water.
        float angle = k * dot(direction, position.xz) - 0.7 * sqrt(9.8 * k) * Swell.x + i * 1.7;
        // Sharper crests: each wave carries the water towards its crest, by a fixed share of its wavelength.
        position.xz += 0.12 / k * direction * cos(angle) * scale;
        position.y += height * (sin(angle) + 1);
        normal.xz -= direction * k * height * cos(angle);
        normal.y -= 0.12 * sin(angle) * scale;
    }
    pixel.Swell = normalize(normal);
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

float3 Normal(float4 waves, float3 swell, float3 view, out float height)
{
    float3 a = tex2D(Waves, waves.xy).rgb - 0.5, b = tex2D(Waves, waves.zw).rgb - 0.5;
    height = a.b + 0.7 * b.b;
    float2 slope = (a.rg + 0.7 * b.rg) * 2 * Bump;
    float3 normal = normalize(swell + float3(-slope.x, 0, -slope.y));
    // A pixel seen cannot face away from the camera: the far side of a crest, tipped back towards it (or it shades as a bright line).
    return normalize(normal + view * max(0, 0.15 - dot(normal, view)));
}

float Glint(float3 normal, float3 view)
{
    return pow(saturate(dot(normal, normalize(view + Sun.xyz))), 180) * 3;
}

float4 Tint(Pixel pixel) : COLOR
{
    float height;
    float3 view = normalize(Eye.xyz - pixel.World);
    float3 normal = Normal(pixel.Waves, pixel.Swell, view, height);
    // Schlick's fresnel: water reflects more of the sky the lower the angle it is seen at.
    float fresnel = 0.02 + 0.98 * pow(1 - saturate(dot(normal, view)), 5);
    // The ripples' own light and shade around retail's flat lighting: a flat surface is left as it was.
    float shade = 1 + 1.6 * (dot(normal, Sun.xyz) - Sun.y);
    // Seen from above, the water's own tint; at a low angle, a pale sky's reflection, which brightens without tinting.
    float3 water = lerp(Shallow.rgb, Deep.rgb, pixel.Shore.y) * 0.5 * shade;
    float3 colour = lerp(water, float3(0.62, 0.64, 0.66), fresnel) + 0.25 * Glint(normal, view);
    // At most 1.6 × retail's colour.
    colour = min(0.5 + (colour - 0.5) * Sun.w, 0.8);
    return float4(lerp(0.5, colour, pixel.Colour.a), 1);
}

float4 Light(Pixel pixel) : COLOR
{
    float height;
    float3 view = normalize(Eye.xyz - pixel.World);
    float3 normal = Normal(pixel.Waves, pixel.Swell, view, height);
    // Foam where the water meets the shore, on the wave crests, drifting in and out.
    float edge = pixel.Shore.x - 0.12 * sin(Time.x * 0.9 + 0.05 * (pixel.World.x + pixel.World.z));
    float band = saturate(1 - abs(edge - 0.55) / 0.3);
    float foam = band * smoothstep(-0.05, 0.25, height + 0.3 * band - 0.15);
    return float4((Glint(normal, view) + 0.8 * foam).xxx * pixel.Colour.a, 1);
}

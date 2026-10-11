using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using LegACEy.Client.GameArt;
using static LegACEy.Client.DecalPlugin.D3D9;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Draws a <see cref="CharacterModel"/> with the game's own Direct3D 9 device, into a rectangle of the back buffer, from the post-UI
/// hook. Textures go to the card once per model (managed pool, so they survive a device reset); each frame is a handful of draw
/// calls, and every device state is put back afterwards.
/// </summary>
internal sealed unsafe class ModelRenderer : IDisposable
{
    private const float FieldOfView = MathF.PI / 6;

    private readonly IntPtr _device;
    private readonly List<Mesh> _meshes = new();
    private CharacterModel? _model;

    public ModelRenderer(IntPtr device) => _device = device;

    public void Draw(CharacterModel model, Rectangle area, float yaw, float zoom, float focus)
    {
        var device = _device;
        area.Intersect(Viewport(device));
        if (area.Width <= 0 || area.Height <= 0) return;
        if (!ReferenceEquals(model, _model)) Load(model);

        var saved = SaveState(device);
        try
        {
            SetViewport(device, area);
            // the game's depth buffer is finished with by now; clear only our rectangle of it
            ClearDepth(device, area, 1f);

            var middle = model.Bottom + (model.Top - model.Bottom) * focus;
            var distance = (model.Top - model.Bottom) * LegACEy.Client.Demo.ModelView.Framing / 2 / MathF.Tan(FieldOfView / 2) / zoom;
            // System.Numerics builds the same right-handed matrices as D3DX's RotationZ, LookAtRH and PerspectiveFovRH.
            SetTransform(device, TransformWorld, Matrix4x4.CreateRotationZ(yaw));
            SetTransform(device, TransformView, Matrix4x4.CreateLookAt(new Vector3(0, -distance, middle), new Vector3(0, 0, middle), Vector3.UnitZ));
            SetTransform(device, TransformProjection, Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, area.Width / (float)area.Height, 0.1f, distance + 5));

            UseFixedFunction(device, FvfXyz | FvfNormal | FvfTex1);
            SetRenderState(device, RenderState.ZEnable, 1);
            SetRenderState(device, RenderState.ZWriteEnable, 1);
            SetRenderState(device, RenderState.ZFunc, CmpLessEqual);
            SetRenderState(device, RenderState.StencilEnable, 0);
            SetRenderState(device, RenderState.ScissorTestEnable, 0);
            SetRenderState(device, RenderState.FogEnable, 0);
            SetRenderState(device, RenderState.AlphaBlendEnable, 0);
            SetRenderState(device, RenderState.AlphaRef, 0x80);
            SetRenderState(device, RenderState.AlphaFunc, CmpGreaterEqual);
            SetRenderState(device, RenderState.CullMode, CullNone);
            SetRenderState(device, RenderState.FillMode, FillSolid);
            SetRenderState(device, RenderState.ShadeMode, ShadeGouraud);
            SetRenderState(device, RenderState.ColorWriteEnable, 0xF);

            SetRenderState(device, RenderState.Lighting, 1);
            SetRenderState(device, RenderState.SpecularEnable, 0);
            SetRenderState(device, RenderState.ColorVertex, 0);
            SetRenderState(device, RenderState.NormalizeNormals, 1);
            SetRenderState(device, RenderState.Ambient, unchecked((int)0xFF606060));
            SetMaterial(device, new Material { Diffuse = Vector4.One, Ambient = Vector4.One });
            SetLight(device, 0, new Light
            {
                Type = 3, // D3DLIGHT_DIRECTIONAL
                Diffuse = new Vector4(0.8f, 0.8f, 0.8f, 1f),
                Direction = Vector3.Normalize(new Vector3(-0.4f, 1f, -0.6f))
            });
            LightEnable(device, 0, true);
            for (var i = 1; i < 8; i++)
                LightEnable(device, i, false);

            SetStageState(device, 0, StageState.ColorOp, OpModulate);
            SetStageState(device, 0, StageState.ColorArg1, ArgTexture);
            SetStageState(device, 0, StageState.ColorArg2, ArgDiffuse);
            SetStageState(device, 0, StageState.AlphaOp, OpSelectArg1);
            SetStageState(device, 0, StageState.AlphaArg1, ArgTexture);
            SetStageState(device, 0, StageState.TexCoordIndex, 0);
            SetStageState(device, 0, StageState.TextureTransformFlags, 0);
            SetStageState(device, 1, StageState.ColorOp, OpDisable);
            SetStageState(device, 1, StageState.AlphaOp, OpDisable);
            SetSamplerState(device, 0, SamplerState.MinFilter, FilterLinear);
            SetSamplerState(device, 0, SamplerState.MagFilter, FilterLinear);
            SetSamplerState(device, 0, SamplerState.MipFilter, FilterNone);
            SetSamplerState(device, 0, SamplerState.AddressU, AddressWrap);
            SetSamplerState(device, 0, SamplerState.AddressV, AddressWrap);

            foreach (var mesh in _meshes)
            {
                SetRenderState(device, RenderState.AlphaTestEnable, mesh.Clipped ? 1 : 0);
                SetTexture(device, 0, mesh.Texture);
                fixed (ModelVertex* vertices = mesh.Vertices)
                    DrawPrimitiveUP(device, TriangleList, mesh.Vertices.Length / 3, vertices, sizeof(ModelVertex));
            }
        }
        finally
        {
            RestoreState(saved);
        }
    }

    public void Dispose()
    {
        Unload();
        _model = null;
    }

    private void Load(CharacterModel model)
    {
        Unload();
        foreach (var mesh in model.Meshes)
        {
            if (mesh.Vertices.Count == 0) continue;
            // ModelVertex is already a D3DFVF_XYZ | NORMAL | TEX1 vertex: position, normal, then u and v.
            _meshes.Add(new Mesh(Upload(mesh.Texture), mesh.Clipped, mesh.Vertices.ToArray()));
        }
        _model = model;
    }

    private IntPtr Upload(GameImage image)
    {
        var texture = CreateTexture(_device, image.Width, image.Height);
        try { D3D9.Upload(texture, new Rectangle(0, 0, image.Width, image.Height), image.Pixels, image.Width * 4); }
        catch
        {
            Release(ref texture);
            throw;
        }
        return texture;
    }

    private void Unload()
    {
        foreach (var mesh in _meshes)
        {
            var texture = mesh.Texture;
            Release(ref texture);
        }
        _meshes.Clear();
    }

    private readonly record struct Mesh(IntPtr Texture, bool Clipped, ModelVertex[] Vertices);
}

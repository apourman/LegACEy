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
        area.Intersect(Viewport(_device));
        if (area.Width <= 0 || area.Height <= 0) return;
        // The camera stands back far enough for the model's height. A model wider than it is tall (a shield, a sword lying down) is
        // fitted by its width at any turn instead, so it shows whole; a character keeps its tight height framing. An empty model or
        // no zoom gives no distance: CreateLookAt would make NaNs.
        var height = model.Top - model.Bottom;
        var span = 2 * model.Radius > height ? Math.Max(height, 2 * model.Radius * area.Height / (float)area.Width) : height;
        var fit = span * LegACEy.Client.Demo.ModelView.Framing / 2 / MathF.Tan(FieldOfView / 2);
        if (!(fit > 0) || !(zoom > 0)) return;
        if (!ReferenceEquals(model, _model)) Load(model);

        var saved = SaveState(_device);
        try
        {
            SetViewport(_device, area);
            // the game's depth buffer is finished with by now; clear only our rectangle of it
            ClearDepth(_device, area, 1f);

            var middle = model.Bottom + (model.Top - model.Bottom) * focus;
            var distance = fit / zoom;
            // System.Numerics builds the same right-handed matrices as D3DX's RotationZ, LookAtRH and PerspectiveFovRH.
            SetTransform(_device, TransformWorld, Matrix4x4.CreateRotationZ(yaw));
            SetTransform(_device, TransformView, Matrix4x4.CreateLookAt(new Vector3(0, -distance, middle), new Vector3(0, 0, middle), Vector3.UnitZ));
            SetTransform(_device, TransformProjection, Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, area.Width / (float)area.Height, 0.1f, distance + 5));

            UseFixedFunction(_device, FvfXyz | FvfNormal | FvfTex1);
            SetRenderState(_device, RenderState.ZEnable, 1);
            SetRenderState(_device, RenderState.ZWriteEnable, 1);
            SetRenderState(_device, RenderState.ZFunc, CmpLessEqual);
            SetRenderState(_device, RenderState.StencilEnable, 0);
            SetRenderState(_device, RenderState.ScissorTestEnable, 0);
            SetRenderState(_device, RenderState.FogEnable, 0);
            SetRenderState(_device, RenderState.AlphaBlendEnable, 0);
            SetRenderState(_device, RenderState.AlphaRef, 0x80);
            SetRenderState(_device, RenderState.AlphaFunc, CmpGreaterEqual);
            SetRenderState(_device, RenderState.CullMode, CullNone);
            SetRenderState(_device, RenderState.FillMode, FillSolid);
            SetRenderState(_device, RenderState.ShadeMode, ShadeGouraud);
            SetRenderState(_device, RenderState.ColorWriteEnable, 0xF);

            SetRenderState(_device, RenderState.Lighting, 1);
            SetRenderState(_device, RenderState.SpecularEnable, 0);
            SetRenderState(_device, RenderState.ColorVertex, 0);
            SetRenderState(_device, RenderState.NormalizeNormals, 1);
            SetRenderState(_device, RenderState.Ambient, unchecked((int)0xFF606060));
            SetMaterial(_device, new Material { Diffuse = Vector4.One, Ambient = Vector4.One });
            // Managed DirectX's Lights[0] started from its own zeroed default, not the device's light; these are the fields it changed.
            SetLight(_device, 0, new Light
            {
                Type = 3, // D3DLIGHT_DIRECTIONAL
                Diffuse = new Vector4(0.8f, 0.8f, 0.8f, 1f),
                Direction = Vector3.Normalize(new Vector3(-0.4f, 1f, -0.6f))
            });
            LightEnable(_device, 0, true);
            for (var i = 1; i < 8; i++)
                LightEnable(_device, i, false);

            SetStageState(_device, 0, StageState.ColorOp, OpModulate);
            SetStageState(_device, 0, StageState.ColorArg1, ArgTexture);
            SetStageState(_device, 0, StageState.ColorArg2, ArgDiffuse);
            SetStageState(_device, 0, StageState.AlphaOp, OpSelectArg1);
            SetStageState(_device, 0, StageState.AlphaArg1, ArgTexture);
            SetStageState(_device, 0, StageState.TexCoordIndex, 0);
            SetStageState(_device, 0, StageState.TextureTransformFlags, 0);
            SetStageState(_device, 1, StageState.ColorOp, OpDisable);
            SetStageState(_device, 1, StageState.AlphaOp, OpDisable);
            SetSamplerState(_device, 0, SamplerState.MinFilter, FilterLinear);
            SetSamplerState(_device, 0, SamplerState.MagFilter, FilterLinear);
            SetSamplerState(_device, 0, SamplerState.MipFilter, FilterNone);
            SetSamplerState(_device, 0, SamplerState.AddressU, AddressWrap);
            SetSamplerState(_device, 0, SamplerState.AddressV, AddressWrap);

            foreach (var mesh in _meshes)
            {
                SetRenderState(_device, RenderState.AlphaTestEnable, mesh.Clipped ? 1 : 0);
                SetTexture(_device, 0, mesh.Texture);
                fixed (ModelVertex* vertices = mesh.Vertices)
                    DrawPrimitiveUP(_device, TriangleList, mesh.Vertices.Length / 3, vertices, sizeof(ModelVertex));
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
        _model = null;
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
            Release(texture);
            throw;
        }
        return texture;
    }

    private void Unload()
    {
        foreach (var mesh in _meshes)
            Release(mesh.Texture);
        _meshes.Clear();
    }

    private readonly record struct Mesh(IntPtr Texture, bool Clipped, ModelVertex[] Vertices);
}

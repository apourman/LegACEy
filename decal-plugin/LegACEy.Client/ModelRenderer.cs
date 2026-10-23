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
    private readonly IntPtr _device;
    private readonly List<Mesh> _meshes = new();
    private CharacterModel? _model;
    private HashSet<int> _outlined = new();
    private float _shellThickness;

    public ModelRenderer(IntPtr device) => _device = device;

    /// <param name="outlined">Model parts outlined in <paramref name="colour"/> (RGB): what a drop adds or a lift takes off.</param>
    public void Draw(CharacterModel model, Rectangle area, Quaternion orientation, float zoom, float focus, IReadOnlyCollection<int> outlined, uint colour)
    {
        area.Intersect(Viewport(_device));
        if (area.Width <= 0 || area.Height <= 0) return;
        if (LegACEy.Client.Demo.ModelView.Camera(model, area.Width, area.Height, orientation, zoom, focus) is not { } camera) return;
        if (!ReferenceEquals(model, _model)) Load(model);

        var saved = SaveState(_device);
        try
        {
            SetViewport(_device, area);
            // the game's depth buffer is finished with by now; clear only our rectangle of it
            ClearDepth(_device, area, 1f);

            SetTransform(_device, TransformWorld, camera.World);
            SetTransform(_device, TransformView, camera.View);
            SetTransform(_device, TransformProjection, camera.Projection);

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
                DrawMesh(mesh, mesh.Vertices);

            if (outlined.Count == 0) return;
            if (!_outlined.SetEquals(outlined))
            {
                _outlined = new HashSet<int>(outlined);
                foreach (var mesh in _meshes) mesh.Outlined = mesh.Shell = null;
            }
            // A rim about 1.5 pixels wide: the outlined parts pushed out along their normals by that much at the model's distance, drawn
            // flat in the colour where nothing nearer is (a hand in front of a coat keeps its place), then the parts themselves again over
            // the push, at the depth they already hold, so only the rim outside them is left.
            var thickness = 1.5f * (model.Top - model.Bottom) * LegACEy.Client.Demo.ModelView.Framing / zoom / area.Height;
            if (thickness != _shellThickness)
            {
                _shellThickness = thickness;
                foreach (var mesh in _meshes) mesh.Shell = null;
            }
            SetRenderState(_device, RenderState.ZWriteEnable, 0);
            SetRenderState(_device, RenderState.TextureFactor, unchecked((int)(0xFF000000 | colour)));
            SetStageState(_device, 0, StageState.ColorOp, OpSelectArg1);
            SetStageState(_device, 0, StageState.ColorArg1, ArgFactor);
            foreach (var mesh in _meshes)
                DrawMesh(mesh, mesh.Shell ??= Pushed(mesh.Outlined ??= OutlinedOf(mesh), thickness));
            SetStageState(_device, 0, StageState.ColorOp, OpModulate);
            SetStageState(_device, 0, StageState.ColorArg1, ArgTexture);
            foreach (var mesh in _meshes)
                DrawMesh(mesh, mesh.Outlined!);
        }
        finally
        {
            RestoreState(saved);
        }
    }

    private void DrawMesh(Mesh mesh, ModelVertex[] vertices)
    {
        if (vertices.Length == 0) return;
        SetRenderState(_device, RenderState.AlphaTestEnable, mesh.Clipped ? 1 : 0);
        SetTexture(_device, 0, mesh.Texture);
        fixed (ModelVertex* first = vertices)
            DrawPrimitiveUP(_device, TriangleList, vertices.Length / 3, first, sizeof(ModelVertex));
    }

    /// <summary>The mesh's triangles that belong to an outlined part.</summary>
    private ModelVertex[] OutlinedOf(Mesh mesh)
    {
        var outlined = new List<ModelVertex>();
        for (var i = 0; i + 2 < mesh.Vertices.Length; i += 3)
            if (_outlined.Contains(mesh.Parts[i]))
                outlined.AddRange(new[] { mesh.Vertices[i], mesh.Vertices[i + 1], mesh.Vertices[i + 2] });
        return outlined.ToArray();
    }

    /// <summary>The vertices moved out along their normals by the distance.</summary>
    private static ModelVertex[] Pushed(ModelVertex[] vertices, float distance)
    {
        var pushed = (ModelVertex[])vertices.Clone();
        for (var i = 0; i < pushed.Length; i++)
        {
            var normal = Vector3.Normalize(new Vector3(pushed[i].NX, pushed[i].NY, pushed[i].NZ));
            if (float.IsNaN(normal.X)) continue;
            pushed[i].X += normal.X * distance;
            pushed[i].Y += normal.Y * distance;
            pushed[i].Z += normal.Z * distance;
        }
        return pushed;
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
            _meshes.Add(new Mesh(Upload(mesh.Texture), mesh.Clipped, mesh.Vertices.ToArray(), mesh.Parts.ToArray()));
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

    private sealed record Mesh(IntPtr Texture, bool Clipped, ModelVertex[] Vertices, int[] Parts)
    {
        /// <summary>The triangles of the outlined parts, worked out once per set of outlined parts.</summary>
        public ModelVertex[]? Outlined { get; set; }
        /// <summary>Those triangles pushed out into the rim's shell, worked out again when its thickness changes.</summary>
        public ModelVertex[]? Shell { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using LegACEy.Client.GameArt;
using Microsoft.DirectX;
using Microsoft.DirectX.Direct3D;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Draws a <see cref="CharacterModel"/> with the game's own Direct3D 9 device, into a rectangle of the back buffer,
/// from the post-UI hook. Textures go to the card once per model (managed pool, so they survive a device reset);
/// each frame is a handful of draw calls, and every device state is put back afterwards.
/// </summary>
internal sealed class PaperdollRenderer : IDisposable
{
    private const float FieldOfView = (float)(Math.PI / 6);

    private readonly Device _device;
    private readonly List<Mesh> _meshes = new();
    private CharacterModel? _model;

    public PaperdollRenderer(Device device) => _device = device;

    public void Draw(CharacterModel model, Rectangle area, float yaw, float zoom)
    {
        var screen = _device.Viewport;
        area.Intersect(new Rectangle(screen.X, screen.Y, screen.Width, screen.Height));
        if (area.Width <= 0 || area.Height <= 0) return;
        if (!ReferenceEquals(model, _model)) Load(model);

        using var saved = new StateBlock(_device, StateBlockType.All);
        saved.Capture();
        try
        {
            _device.Viewport = new Viewport { X = area.X, Y = area.Y, Width = area.Width, Height = area.Height, MinZ = 0, MaxZ = 1 };
            // the game's depth buffer is finished with by now; clear only our rectangle of it
            _device.Clear(ClearFlags.ZBuffer, 0, 1f, 0, new[] { area });

            var middle = (model.Bottom + model.Top) / 2;
            var distance = (model.Top - model.Bottom) * 0.6f / (float)Math.Tan(FieldOfView / 2) / zoom;
            _device.Transform.World = Matrix.RotationZ(yaw);
            _device.Transform.View = Matrix.LookAtRH(new Vector3(0, -distance, middle), new Vector3(0, 0, middle), new Vector3(0, 0, 1));
            _device.Transform.Projection = Matrix.PerspectiveFovRH(FieldOfView, area.Width / (float)area.Height, 0.1f, distance + 5);

            _device.VertexShader = null;
            _device.PixelShader = null;
            _device.VertexFormat = CustomVertex.PositionNormalTextured.Format;
            _device.SetRenderState(RenderStates.ZEnable, true);
            _device.SetRenderState(RenderStates.ZBufferWriteEnable, true);
            _device.SetRenderState(RenderStates.ZBufferFunction, (int)Compare.LessEqual);
            _device.SetRenderState(RenderStates.StencilEnable, false);
            _device.SetRenderState(RenderStates.ScissorTestEnable, false);
            _device.SetRenderState(RenderStates.FogEnable, false);
            _device.SetRenderState(RenderStates.AlphaBlendEnable, false);
            _device.SetRenderState(RenderStates.ReferenceAlpha, 0x80);
            _device.SetRenderState(RenderStates.AlphaFunction, (int)Compare.GreaterEqual);
            _device.SetRenderState(RenderStates.CullMode, (int)Cull.None);
            _device.SetRenderState(RenderStates.FillMode, (int)FillMode.Solid);
            _device.SetRenderState(RenderStates.ShadeMode, (int)ShadeMode.Gouraud);
            _device.SetRenderState(RenderStates.ColorWriteEnable, 0xF);

            _device.SetRenderState(RenderStates.Lighting, true);
            _device.SetRenderState(RenderStates.SpecularEnable, false);
            _device.SetRenderState(RenderStates.ColorVertex, false);
            _device.SetRenderState(RenderStates.NormalizeNormals, true);
            _device.SetRenderState(RenderStates.Ambient, unchecked((int)0xFF606060));
            _device.Material = new Material { DiffuseColor = new ColorValue(1f, 1f, 1f), AmbientColor = new ColorValue(1f, 1f, 1f) };
            var light = _device.Lights[0];
            light.Type = LightType.Directional;
            light.DiffuseColor = new ColorValue(0.8f, 0.8f, 0.8f);
            light.Direction = Vector3.Normalize(new Vector3(-0.4f, 1f, -0.6f));
            light.Enabled = true;
            light.Update();
            for (var i = 1; i < 8; i++)
            {
                _device.Lights[i].Enabled = false;
                _device.Lights[i].Update();
            }

            _device.SetTextureStageState(0, TextureStageStates.ColorOperation, (int)TextureOperation.Modulate);
            _device.SetTextureStageState(0, TextureStageStates.ColorArgument1, (int)TextureArgument.TextureColor);
            _device.SetTextureStageState(0, TextureStageStates.ColorArgument2, (int)TextureArgument.Diffuse);
            _device.SetTextureStageState(0, TextureStageStates.AlphaOperation, (int)TextureOperation.SelectArg1);
            _device.SetTextureStageState(0, TextureStageStates.AlphaArgument1, (int)TextureArgument.TextureColor);
            _device.SetTextureStageState(0, TextureStageStates.TextureCoordinateIndex, 0);
            _device.SetTextureStageState(0, TextureStageStates.TextureTransform, (int)TextureTransform.Disable);
            _device.SetTextureStageState(1, TextureStageStates.ColorOperation, (int)TextureOperation.Disable);
            _device.SetTextureStageState(1, TextureStageStates.AlphaOperation, (int)TextureOperation.Disable);
            _device.SetSamplerState(0, SamplerStageStates.MinFilter, (int)TextureFilter.Linear);
            _device.SetSamplerState(0, SamplerStageStates.MagFilter, (int)TextureFilter.Linear);
            _device.SetSamplerState(0, SamplerStageStates.MipFilter, (int)TextureFilter.None);
            _device.SetSamplerState(0, SamplerStageStates.AddressU, (int)TextureAddress.Wrap);
            _device.SetSamplerState(0, SamplerStageStates.AddressV, (int)TextureAddress.Wrap);

            foreach (var mesh in _meshes)
            {
                _device.SetRenderState(RenderStates.AlphaTestEnable, mesh.Clipped);
                _device.SetTexture(0, mesh.Texture);
                _device.DrawUserPrimitives(PrimitiveType.TriangleList, mesh.Vertices.Length / 3, mesh.Vertices);
            }
        }
        finally
        {
            _device.SetTexture(0, null);
            saved.Apply();
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
            var vertices = new CustomVertex.PositionNormalTextured[mesh.Vertices.Count];
            for (var i = 0; i < vertices.Length; i++)
            {
                var v = mesh.Vertices[i];
                vertices[i] = new CustomVertex.PositionNormalTextured(v.X, v.Y, v.Z, v.NX, v.NY, v.NZ, v.U, v.V);
            }
            _meshes.Add(new Mesh(Upload(mesh.Texture), mesh.Clipped, vertices));
        }
        _model = model;
    }

    private Texture Upload(GameImage image)
    {
        var texture = new Texture(_device, image.Width, image.Height, 1, Usage.None, Format.A8R8G8B8, Pool.Managed);
        var bits = texture.LockRectangle(0, new Rectangle(0, 0, image.Width, image.Height), LockFlags.None, out var pitch);
        try
        {
            for (var row = 0; row < image.Height; row++)
                Marshal.Copy(image.Pixels, row * image.Width * 4, IntPtr.Add(bits.InternalData, row * pitch), image.Width * 4);
        }
        finally
        {
            texture.UnlockRectangle(0);
        }
        return texture;
    }

    private void Unload()
    {
        foreach (var mesh in _meshes)
            mesh.Texture.Dispose();
        _meshes.Clear();
    }

    private sealed class Mesh
    {
        public Mesh(Texture texture, bool clipped, CustomVertex.PositionNormalTextured[] vertices)
        {
            Texture = texture;
            Clipped = clipped;
            Vertices = vertices;
        }

        public Texture Texture { get; }
        public bool Clipped { get; }
        public CustomVertex.PositionNormalTextured[] Vertices { get; }
    }
}

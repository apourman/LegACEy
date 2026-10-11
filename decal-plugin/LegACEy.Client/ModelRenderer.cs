using System;
using System.Drawing;
using LegACEy.Client.GameArt;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Draws a character model into a screen area on the game's device.</summary>
/// <remarks>
/// ponytail: draws nothing yet. The Managed DirectX renderer (still in the DecalPlugin project) moves here as direct D3D9 calls, with
/// its transforms computed in System.Numerics.
/// </remarks>
internal sealed class ModelRenderer : IDisposable
{
    public ModelRenderer(IntPtr device)
    {
    }

    public void Draw(CharacterModel model, Rectangle area, float yaw, float zoom, float focus)
    {
    }

    public void Dispose()
    {
    }
}

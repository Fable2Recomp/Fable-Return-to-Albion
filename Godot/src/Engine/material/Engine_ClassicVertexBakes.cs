using System;
using Godot;

namespace Refii.Models;

/// <summary>
/// Every vertex-baked placement's colours in one texture (global `xr_vertex_bakes`), one texel
/// per vertex in the shared mesh's vertex order, a block per placement.
/// </summary>
/// <remarks>
/// The title streams a placement's bake as a second vertex stream beside the shared model
/// (the AMB1 pair reads it as COLOR). Godot has no per-instance stream, and a mesh copy per
/// placement made every baked prop a mesh of its own, so none of them merged into instanced
/// draws. Here the shared mesh stays on the node, the block's start rides the instance uniform
/// <see cref="BaseUniform"/>, each surface's start inside the block is its material's
/// `xr_bake_surface`, and the composed AMB1 stage reads COLOR from this texture at
/// base + surface + VERTEX_ID (XR_VERTEX_BAKE_FETCH, Engine_ClassicShaders). RGBA8, as the
/// bake and the vertex colour it replaces both are.
/// </remarks>
public static class Engine_ClassicVertexBakes
{
    public static readonly StringName BaseUniform = "xr_bake_base";
    public static readonly StringName SurfaceUniform = "xr_bake_surface";
    private static readonly StringName Global = "xr_vertex_bakes";

    /// <summary>Texels per row; the shader takes the width from the texture.</summary>
    private const int Width = 2048;

    /// <summary>Rows grow in steps of this many.</summary>
    private const int RowStep = 64;

    private static byte[] _bytes = Array.Empty<byte>();
    private static int _rows;
    private static int _used;
    private static bool _dirty;
    private static Image? _image;
    private static ImageTexture? _texture;
    private static bool _registered;

    /// <summary>Texels in use, for the log.</summary>
    public static int Used => _used;

    /// <summary>Stores a placement's colours (per surface, in the mesh's order); the block's first texel. Main thread.</summary>
    public static int Add(Color[][] surfaces)
    {
        int total = 0;
        foreach (Color[] colours in surfaces)
        {
            total += colours.Length;
        }
        int start = _used;
        Grow(start + total);
        int at = start * 4;
        foreach (Color[] colours in surfaces)
        {
            foreach (Color c in colours)
            {
                _bytes[at] = (byte)Math.Clamp((int)MathF.Round(c.R * 255f), 0, 255);
                _bytes[at + 1] = (byte)Math.Clamp((int)MathF.Round(c.G * 255f), 0, 255);
                _bytes[at + 2] = (byte)Math.Clamp((int)MathF.Round(c.B * 255f), 0, 255);
                _bytes[at + 3] = (byte)Math.Clamp((int)MathF.Round(c.A * 255f), 0, 255);
                at += 4;
            }
        }
        _used = start + total;
        _dirty = true;
        return start;
    }

    /// <summary>Uploads what was added since the last call. Main thread, once a frame.</summary>
    public static void Flush()
    {
        if (!_dirty || _rows == 0)
        {
            return;
        }
        _dirty = false;
        if (_image is not null && _texture is not null && _image.GetHeight() == _rows)
        {
            _image.SetData(Width, _rows, false, Image.Format.Rgba8, _bytes);
            _texture.Update(_image);
            return;
        }
        _image = Image.CreateFromData(Width, _rows, false, Image.Format.Rgba8, _bytes);
        _texture = ImageTexture.CreateFromImage(_image);
        if (!_registered)
        {
            _registered = true;
            bool declared = false;
            foreach (StringName name in RenderingServer.GlobalShaderParameterGetList())
            {
                declared |= name == Global;
            }
            if (!declared)
            {
                RenderingServer.GlobalShaderParameterAdd(Global, RenderingServer.GlobalShaderParameterType.Sampler2D, _texture);
            }
        }
        RenderingServer.GlobalShaderParameterSet(Global, _texture);
    }

    /// <summary>Drops every block: a region change drops the placements with it.</summary>
    public static void Clear()
    {
        _used = 0;
        Array.Clear(_bytes);
        _dirty = _rows != 0;
    }

    private static void Grow(int texels)
    {
        int rows = (texels + Width - 1) / Width;
        if (rows <= _rows)
        {
            return;
        }
        int grown = (rows + RowStep - 1) / RowStep * RowStep;
        Array.Resize(ref _bytes, Width * grown * 4);
        _rows = grown;
    }
}

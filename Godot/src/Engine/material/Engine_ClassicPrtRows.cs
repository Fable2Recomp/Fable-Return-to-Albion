using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// Every transfer placement's g_PRTConstants (VS c28.., twelve rows) in one float texture
/// (global `xr_prt_constants`), a row per placement.
/// </summary>
/// <remarks>
/// The title uploads a placement's twelve PRT rows per draw (`sub_82238330`). A material per
/// placement carried them here, and no two transfer props ever merged into one instanced draw.
/// Now every placement of a model shares the AMB2 pair (Engine_ClassicMaterial.WithPrtRows), the
/// composed stage reads `vs_g_PRTConstants[i]` from this texture (XR_PRT_ROW_FETCH,
/// Engine_ClassicShaders), and the instance uniform <see cref="RowUniform"/> names the row.
/// </remarks>
public static class Engine_ClassicPrtRows
{
    public static readonly StringName RowUniform = "xr_prt_row";
    private static readonly StringName Global = "xr_prt_constants";

    /// <summary>Texels per row: the stage's twelve registers.</summary>
    public const int Registers = 12;

    private const int RowStep = 256;

    private static float[] _values = Array.Empty<float>();
    private static byte[] _bytes = Array.Empty<byte>();
    private static int _rows;
    private static int _next = 1;
    private static readonly Stack<int> _free = new();
    private static bool _dirty;
    private static Image? _image;
    private static ImageTexture? _texture;
    private static bool _registered;

    /// <summary>A row of its own for one placement (row 0 stays zero). Main thread.</summary>
    public static int Acquire()
    {
        int row = _free.Count != 0 ? _free.Pop() : _next++;
        Grow(row + 1);
        return row;
    }

    /// <summary>Bumped by <see cref="Clear"/>: a row handed out before it is not released into the new set.</summary>
    public static int Generation { get; private set; }

    public static void Release(int row, int generation)
    {
        if (generation != Generation || row <= 0 || row >= _next)
        {
            return;
        }
        Array.Clear(_values, row * Registers * 4, Registers * 4);
        _free.Push(row);
        _dirty = true;
    }

    /// <summary>Writes a placement's rows (up to <see cref="Registers"/> vectors).</summary>
    public static void Write(int row, IList<Vector4> constants)
    {
        if (row <= 0 || row >= _rows)
        {
            return;
        }
        int at = row * Registers * 4;
        int n = Math.Min(constants.Count, Registers);
        for (int i = 0; i < Registers; i++)
        {
            Vector4 v = i < n ? constants[i] : Vector4.Zero;
            _values[at++] = v.X;
            _values[at++] = v.Y;
            _values[at++] = v.Z;
            _values[at++] = v.W;
        }
        _dirty = true;
    }

    /// <summary>Uploads what changed. Main thread, once a frame.</summary>
    public static void Flush()
    {
        if (!_dirty || _rows == 0)
        {
            return;
        }
        _dirty = false;
        if (_bytes.Length != _values.Length * 4)
        {
            _bytes = new byte[_values.Length * 4];
        }
        Buffer.BlockCopy(_values, 0, _bytes, 0, _bytes.Length);
        if (_image is not null && _texture is not null && _image.GetHeight() == _rows)
        {
            _image.SetData(Registers, _rows, false, Image.Format.Rgbaf, _bytes);
            _texture.Update(_image);
            return;
        }
        _image = Image.CreateFromData(Registers, _rows, false, Image.Format.Rgbaf, _bytes);
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

    /// <summary>Drops every row: a region change drops the placements with it.</summary>
    public static void Clear()
    {
        ++Generation;
        Array.Clear(_values);
        _free.Clear();
        _next = 1;
        _dirty = _rows != 0;
    }

    private static void Grow(int rows)
    {
        if (rows <= _rows)
        {
            return;
        }
        int grown = (rows + RowStep - 1) / RowStep * RowStep;
        Array.Resize(ref _values, grown * Registers * 4);
        _rows = grown;
        _dirty = true;
    }
}

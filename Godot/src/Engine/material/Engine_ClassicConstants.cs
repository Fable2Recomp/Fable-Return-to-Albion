using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The frame's shader constant file, as the two textures the translated shaders read it from
/// (<c>xr_ps_constants</c>, <c>xr_vs_constants</c> in xenos_common.gdshaderinc).
/// </summary>
/// <remarks>
/// ONE TEXEL PER REGISTER, ONE ROW PER LIGHT SET: 256 float4 registers per stage laid out as
/// the device holds them, RGBAF. Row 0 is the frame's constants (<see cref="SetFrame"/>, by
/// the registers <see cref="Engine_ClassicRegisters"/> names). The rows above it are copies of
/// row 0 whose light block holds one draw's lights: an instance takes a row with
/// <see cref="AcquireRow"/>, writes its block with <see cref="SetRowBlock"/>, and names the row
/// in its <c>xr_light_row</c> instance uniform. The frame constants outside the light block are
/// mirrored into every live row when row 0 changes, so a row is always "this frame, these
/// lights".
///
/// UPDATED IN PLACE. The textures are set as globals once; afterwards only their contents move
/// (<see cref="ImageTexture.Update"/>, same RID). Setting a sampler global queues a uniform-set
/// rebuild on every material that reads it, which at frame rate held the render thread at 50 ms
/// once already.
/// </remarks>
public static class Engine_ClassicConstants
{
    public const int Registers = 256;
    public const int Floats = Registers * 4;
    /// <summary>Rows per stage: the frame's, plus this many light sets in flight at once.</summary>
    public const int Rows = 512;

    private static readonly StringName PixelGlobal = "xr_ps_constants";
    private static readonly StringName VertexGlobal = "xr_vs_constants";

    private sealed class StageState
    {
        public readonly float[] Values = new float[Floats * Rows];
        /// <summary>The uploaded rows: the textures hold rows [0, Height) only (see Publish).</summary>
        public int Height;
        public byte[] Bytes = Array.Empty<byte>();
        public bool Dirty = true;
        public Image? Image;
        public ImageTexture? Texture;
        /// <summary>First and last register of this stage's per-row light block.</summary>
        public int BlockStart, BlockEnd;
    }

    private static readonly object Gate = new();
    private static readonly StageState _pixel = new()
    {
        BlockStart = Engine_ClassicRegisters.Ps.LightPositions,
        BlockEnd = Engine_ClassicRegisters.Ps.LightBlockEnd,
    };
    private static readonly StageState _vertex = new()
    {
        BlockStart = Engine_ClassicRegisters.Vs.LightPositions,
        BlockEnd = Engine_ClassicRegisters.Vs.LightBlockEnd,
    };
    private static readonly Stack<int> _freeRows = new();
    /// <summary>The highest row ever taken: rows are handed out lowest first and reused, so the
    /// live ones stay packed below it.</summary>
    private static int _highRow;
    private static readonly HashSet<int> _liveRows = new();
    private static bool _registered;

    private static StageState Stage(bool pixel) => pixel ? _pixel : _vertex;

    /// <summary>
    /// Writes <paramref name="values"/> (four floats per register) into row 0 of a stage at
    /// <paramref name="register"/>, and into every live row outside the light block.
    /// </summary>
    public static void SetFrame(bool pixel, int register, ReadOnlySpan<float> values)
    {
        StageState state = Stage(pixel);
        if (register < 0 || values.Length == 0 || register * 4 + values.Length > Floats)
        {
            return;
        }
        lock (Gate)
        {
            if (!Write(state, 0, register, values))
            {
                return;
            }
            // The frame's share of each live row follows; the row's own light block does not.
            int first = register, last = register + (values.Length + 3) / 4;
            foreach (int row in _liveRows)
            {
                for (int r = first; r < last; r++)
                {
                    if (r >= state.BlockStart && r < state.BlockEnd)
                    {
                        continue;
                    }
                    Write(state, row, r, values.Slice((r - register) * 4, Math.Min(4, values.Length - (r - register) * 4)));
                }
            }
        }
    }

    /// <summary>Row 0 of a stage, all registers, into <paramref name="into"/> (at least <see cref="Floats"/>); any thread.</summary>
    public static void CopyFrame(bool pixel, float[] into)
    {
        StageState state = Stage(pixel);
        lock (Gate)
        {
            Array.Copy(state.Values, 0, into, 0, Math.Min(Floats, into.Length));
        }
    }

    /// <summary>A whole row of a stage into <paramref name="into"/> (at least <see cref="Floats"/>); row 0 when out of range. Any thread.</summary>
    public static void CopyRow(bool pixel, int row, float[] into)
    {
        StageState state = Stage(pixel);
        if (row < 0 || row >= Rows)
        {
            row = 0;
        }
        lock (Gate)
        {
            Array.Copy(state.Values, row * Floats, into, 0, Math.Min(Floats, into.Length));
        }
    }

    /// <summary>Row 0's values from <paramref name="register"/>, <paramref name="count"/> registers (four floats each), for the debug channel.</summary>
    public static float[] ReadFrame(bool pixel, int register, int count)
    {
        StageState state = Stage(pixel);
        var result = new float[Math.Max(count, 0) * 4];
        if (register < 0 || register + count > Registers)
        {
            return result;
        }
        lock (Gate)
        {
            Array.Copy(state.Values, register * 4, result, 0, result.Length);
        }
        return result;
    }

    /// <summary>Rows handed out and live, for the debug channel.</summary>
    public static int LiveRows
    {
        get { lock (Gate) { return _liveRows.Count; } }
    }

    /// <summary>A row for one draw's lights, seeded from the frame's row; 0 when none is free.</summary>
    public static int AcquireRow()
    {
        lock (Gate)
        {
            if (_freeRows.Count == 0)
            {
                if (_liveRows.Count + 1 >= Rows)
                {
                    return 0;
                }
                for (int row = Rows - 1; row >= 1; row--)
                {
                    _freeRows.Push(row);
                }
            }
            int taken = _freeRows.Pop();
            _liveRows.Add(taken);
            _highRow = Math.Max(_highRow, taken);
            foreach (StageState state in new[] { _pixel, _vertex })
            {
                Array.Copy(state.Values, 0, state.Values, taken * Floats, Floats);
                state.Dirty = true;
            }
            return taken;
        }
    }

    /// <summary>Hands a row back; its contents are left for the next taker to overwrite.</summary>
    public static void ReleaseRow(int row)
    {
        if (row <= 0 || row >= Rows)
        {
            return;
        }
        lock (Gate)
        {
            if (_liveRows.Remove(row))
            {
                _freeRows.Push(row);
            }
        }
    }

    /// <summary>Writes a row's light block (or any registers of it) for one stage.</summary>
    public static void SetRowBlock(bool pixel, int row, int register, ReadOnlySpan<float> values)
    {
        if (row <= 0 || row >= Rows)
        {
            return;
        }
        lock (Gate)
        {
            Write(Stage(pixel), row, register, values);
        }
    }

    /// <summary>
    /// Creates the textures and hands them to the two globals. Main thread; idempotent.
    /// </summary>
    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }
            _registered = true;
            foreach (StageState state in new[] { _pixel, _vertex })
            {
                Resize(state, RowStep);
            }
        }

        ImageTexture pixelTexture = _pixel.Texture!;
        ImageTexture vertexTexture = _vertex.Texture!;

        // The globals are declared in project.godot; added here as well so a tool scene that
        // runs without the project's list still resolves them.
        bool hasPixel = false, hasVertex = false;
        foreach (StringName name in RenderingServer.GlobalShaderParameterGetList())
        {
            hasPixel |= name == PixelGlobal;
            hasVertex |= name == VertexGlobal;
        }
        if (!hasPixel)
        {
            RenderingServer.GlobalShaderParameterAdd(
                PixelGlobal, RenderingServer.GlobalShaderParameterType.Sampler2D, pixelTexture);
        }
        if (!hasVertex)
        {
            RenderingServer.GlobalShaderParameterAdd(
                VertexGlobal, RenderingServer.GlobalShaderParameterType.Sampler2D, vertexTexture);
        }
        RenderingServer.GlobalShaderParameterSet(PixelGlobal, pixelTexture);
        RenderingServer.GlobalShaderParameterSet(VertexGlobal, vertexTexture);
        Publish();
    }

    /// <summary>
    /// Pushes what changed since the last call into the textures. Main thread, once a frame.
    /// </summary>
    public static void Publish()
    {
        lock (Gate)
        {
            // Only the rows in use travel: of 512 a frame typically holds a few dozen, and the
            // whole texture was 2 MB per stage copied three times and uploaded every frame.
            int height = Math.Min(Rows, (_highRow / RowStep + 1) * RowStep);
            foreach ((StageState state, StringName global) in new[] { (_pixel, PixelGlobal), (_vertex, VertexGlobal) })
            {
                if (state.Texture is null)
                {
                    continue;
                }
                if (height > state.Height)
                {
                    Resize(state, height);
                    RenderingServer.GlobalShaderParameterSet(global, state.Texture!);
                    state.Dirty = true;
                }
                if (!state.Dirty || state.Image is null)
                {
                    continue;
                }
                Buffer.BlockCopy(state.Values, 0, state.Bytes, 0, state.Bytes.Length);
                state.Image.SetData(Registers, state.Height, false, Image.Format.Rgbaf, state.Bytes);
                state.Texture.Update(state.Image);
                state.Dirty = false;
            }
        }
    }

    /// <summary>The uploaded height grows in steps of this many rows.</summary>
    private const int RowStep = 32;

    /// <summary>A stage's image and texture at <paramref name="height"/> rows (a new texture: Update needs the same size).</summary>
    private static void Resize(StageState state, int height)
    {
        state.Height = height;
        state.Bytes = new byte[Floats * height * 4];
        Buffer.BlockCopy(state.Values, 0, state.Bytes, 0, state.Bytes.Length);
        state.Image = Image.CreateFromData(Registers, height, false, Image.Format.Rgbaf, state.Bytes);
        state.Texture = ImageTexture.CreateFromImage(state.Image);
    }

    /// <summary>One register run into one row; true when anything changed. Caller holds the gate.</summary>
    private static bool Write(StageState state, int row, int register, ReadOnlySpan<float> values)
    {
        int at = row * Floats + register * 4;
        int length = Math.Min(values.Length, (row + 1) * Floats - at);
        if (length <= 0)
        {
            return false;
        }
        ReadOnlySpan<float> incoming = values.Slice(0, length);
        Span<float> target = state.Values.AsSpan(at, length);
        if (incoming.SequenceEqual(target))
        {
            return false;
        }
        incoming.CopyTo(target);
        state.Dirty = true;
        return true;
    }
}

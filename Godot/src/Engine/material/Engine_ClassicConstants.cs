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
///
/// A COLUMN BLOCK PER VIEW. On split screen the world is drawn through two cameras in one
/// frame, and both read the one upload: the main thread writes the file once, the render thread
/// applies the upload before it draws any viewport, so a register can hold one value per frame,
/// not one per view. The second view's registers sit beside the first's instead — the texture
/// is <see cref="Registers"/> x 2 wide, register r of the second view at column 256 + r of the
/// same row — and a stage picks its block from the camera drawing it
/// (<c>CAMERA_VISIBLE_LAYERS</c> holds <see cref="Engine_ClassicViews.SecondViewLayer"/> for the
/// second view, xenos_common.gdshaderinc <c>xr_const_at</c>). One shader, one material, no
/// render-thread hand-off. A single view keeps the 256-wide texture and never reads past it.
///
/// THE SECOND BLOCK FOLLOWS THE FIRST except at the view's own registers
/// (<see cref="IsViewRegister"/>): a frame write lands in both blocks but those, which the views'
/// feeds write per view (<see cref="SetFrameView"/>); a row write lands in both as it is, and a
/// row built from a copy of one view's frame is written per view (<see cref="CopyFrameView"/>,
/// <see cref="SetRowBlockView"/>).
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
        /// <summary>The second view's rows, while the screen is split (allocated on the first split).</summary>
        public float[]? Second;
        /// <summary>The uploaded width in texels: 256 for one view, 512 for two.</summary>
        public int Width = Registers;
        /// <summary>The registers that are the view's own (<see cref="IsViewRegister"/>), for <see cref="MapFor"/>.</summary>
        public readonly bool[] ViewRegisters = new bool[Registers];
        /// <summary>The register map the view mask and block bounds were taken from.</summary>
        public Engine_ClassicRegisters.Map? MapFor;
        /// <summary>The uploaded rows: the textures hold rows [0, Height) only (see Publish).</summary>
        public int Height;
        public byte[] Bytes = Array.Empty<byte>();
        public bool Dirty = true;
        public Image? Image;
        public ImageTexture? Texture;
        /// <summary>First and last register of this stage's per-row light block.</summary>
        public int BlockStart, BlockEnd;
        public readonly bool Pixel;

        public StageState(bool pixel) => Pixel = pixel;
    }

    private static readonly object Gate = new();
    private static readonly StageState _pixel = new(pixel: true);
    private static readonly StageState _vertex = new(pixel: false);
    private static readonly Stack<int> _freeRows = new();
    /// <summary>The highest row ever taken: rows are handed out lowest first and reused, so the
    /// live ones stay packed below it.</summary>
    private static int _highRow;
    private static readonly HashSet<int> _liveRows = new();
    private static bool _registered;
    /// <summary>Views the file holds: 1, or 2 on split screen.</summary>
    private static int _views = 1;

    /// <summary>A stage, its view mask and light block bounds those of the title running.</summary>
    private static StageState Stage(bool pixel)
    {
        StageState state = pixel ? _pixel : _vertex;
        Engine_ClassicRegisters.Map map = Engine_ClassicRegisters.Active;
        if (!ReferenceEquals(state.MapFor, map))
        {
            lock (Gate)
            {
                TakeMap(state, map);
            }
        }
        return state;
    }

    /// <summary>The title's view registers and light block into a stage. Caller holds the gate.</summary>
    private static void TakeMap(StageState state, Engine_ClassicRegisters.Map map)
    {
        if (ReferenceEquals(state.MapFor, map))
        {
            return;
        }
        System.Array.Clear(state.ViewRegisters);
        foreach (int r in state.Pixel ? map.Ps.ViewRegisters : map.Vs.ViewRegisters)
        {
            state.ViewRegisters[r] = true;
        }
        state.BlockStart = state.Pixel ? map.Ps.LightPositions : map.Vs.LightPositions;
        state.BlockEnd = state.Pixel ? map.Ps.LightBlockEnd : map.Vs.LightBlockEnd;
        state.MapFor = map;
    }

    /// <summary>Whether <paramref name="register"/> of a stage is a view's own (eye, projection, cascades, view-space by-id params).</summary>
    public static bool IsViewRegister(bool pixel, int register) =>
        register >= 0 && register < Registers && Stage(pixel).ViewRegisters[register];

    /// <summary>Views the file holds now.</summary>
    public static int Views
    {
        get { lock (Gate) { return _views; } }
    }

    /// <summary>
    /// Holds a block of registers per view for <paramref name="views"/> views (1 or 2). Going to two
    /// seeds the second block from the first; the views' feeds then overwrite their own registers
    /// before the frame publishes. Main thread, before the frame's writes.
    /// </summary>
    public static void SetViews(int views)
    {
        views = Math.Clamp(views, 1, 2);
        lock (Gate)
        {
            if (views == _views)
            {
                return;
            }
            _views = views;
            foreach (StageState state in Stages)
            {
                if (views == 2)
                {
                    state.Second ??= new float[Floats * Rows];
                    Array.Copy(state.Values, state.Second, state.Values.Length);
                }
                state.Dirty = true;
            }
        }
    }

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
            WriteFrame(state, state.Values, register, values, skipViewRegisters: false);
            // The second view's block takes the frame too, but not at the registers that are the
            // view's own: its feed writes those (SetFrameView).
            if (_views == 2 && state.Second is { } second)
            {
                WriteFrame(state, second, register, values, skipViewRegisters: true);
            }
        }
    }

    /// <summary>
    /// As <see cref="SetFrame"/> for one view only: view 0 is the frame as a single view has it,
    /// view 1 the second view's block, written only while the screen is split.
    /// </summary>
    public static void SetFrameView(int view, bool pixel, int register, ReadOnlySpan<float> values)
    {
        StageState state = Stage(pixel);
        if (register < 0 || values.Length == 0 || register * 4 + values.Length > Floats)
        {
            return;
        }
        lock (Gate)
        {
            if (view == 0)
            {
                WriteFrame(state, state.Values, register, values, skipViewRegisters: false);
            }
            else if (_views == 2 && state.Second is { } second)
            {
                WriteFrame(state, second, register, values, skipViewRegisters: false);
            }
        }
    }

    /// <summary>Row 0 of one block and its share of every live row. Caller holds the gate.</summary>
    private static void WriteFrame(StageState state, float[] block, int register, ReadOnlySpan<float> values, bool skipViewRegisters)
    {
        int first = register, last = register + (values.Length + 3) / 4;
        if (!skipViewRegisters)
        {
            if (!Write(state, block, 0, register, values))
            {
                return;
            }
        }
        else
        {
            bool changed = false;
            for (int r = first; r < last; r++)
            {
                if (!state.ViewRegisters[r])
                {
                    changed |= Write(state, block, 0, r, values.Slice((r - register) * 4, Math.Min(4, values.Length - (r - register) * 4)));
                }
            }
            if (!changed)
            {
                return;
            }
        }
        // The frame's share of each live row follows; the row's own light block does not.
        foreach (int row in _liveRows)
        {
            for (int r = first; r < last; r++)
            {
                if ((r >= state.BlockStart && r < state.BlockEnd) || (skipViewRegisters && state.ViewRegisters[r]))
                {
                    continue;
                }
                Write(state, block, row, r, values.Slice((r - register) * 4, Math.Min(4, values.Length - (r - register) * 4)));
            }
        }
    }

    /// <summary>Row 0 of a stage, all registers, into <paramref name="into"/> (at least <see cref="Floats"/>); any thread.</summary>
    public static void CopyFrame(bool pixel, float[] into) => CopyFrameView(0, pixel, into);

    /// <summary>Row 0 of a view's block (view 0 when the screen is not split); any thread.</summary>
    public static void CopyFrameView(int view, bool pixel, float[] into)
    {
        StageState state = Stage(pixel);
        lock (Gate)
        {
            float[] block = view != 0 && _views == 2 && state.Second is { } second ? second : state.Values;
            Array.Copy(block, 0, into, 0, Math.Min(Floats, into.Length));
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

    /// <summary>As <see cref="CopyRow"/> from a view's copy of the row (view 0 when the screen is not split). Any thread.</summary>
    public static void CopyRowView(int view, bool pixel, int row, float[] into)
    {
        StageState state = Stage(pixel);
        if (row < 0 || row >= Rows)
        {
            row = 0;
        }
        lock (Gate)
        {
            float[] block = view != 0 && _views == 2 && state.Second is { } second ? second : state.Values;
            Array.Copy(block, row * Floats, into, 0, Math.Min(Floats, into.Length));
        }
    }

    /// <summary>Row 0's values from <paramref name="register"/>, <paramref name="count"/> registers (four floats each), for the debug channel.</summary>
    public static float[] ReadFrame(bool pixel, int register, int count)
    {
        var result = new float[Math.Max(count, 0) * 4];
        ReadFrameView(0, pixel, register, result);
        return result;
    }

    /// <summary>
    /// A view's row 0 from <paramref name="register"/> into <paramref name="into"/> (four floats a
    /// register, as many as it holds); view 0 when the screen is not split. Any thread, no allocation.
    /// </summary>
    public static void ReadFrameView(int view, bool pixel, int register, Span<float> into)
    {
        StageState state = Stage(pixel);
        int count = into.Length / 4;
        if (register < 0 || register + count > Registers)
        {
            into.Clear();
            return;
        }
        lock (Gate)
        {
            float[] block = view != 0 && _views == 2 && state.Second is { } second ? second : state.Values;
            block.AsSpan(register * 4, count * 4).CopyTo(into);
        }
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
            foreach (StageState state in Stages)
            {
                Array.Copy(state.Values, 0, state.Values, taken * Floats, Floats);
                if (state.Second is { } second)
                {
                    Array.Copy(second, 0, second, taken * Floats, Floats);
                }
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

    /// <summary>
    /// Writes a row's light block (or any registers of it) for one stage, in every view's copy of
    /// the row. A row its owner builds from a copy of the frame (<see cref="CopyFrame"/>) carries
    /// the first view's eye: such an owner writes each view's copy with <see cref="SetRowBlockView"/>.
    /// </summary>
    public static void SetRowBlock(bool pixel, int row, int register, ReadOnlySpan<float> values)
    {
        if (row <= 0 || row >= Rows)
        {
            return;
        }
        StageState state = Stage(pixel);
        lock (Gate)
        {
            Write(state, state.Values, row, register, values);
            if (_views == 2 && state.Second is { } second)
            {
                Write(state, second, row, register, values);
            }
        }
    }

    /// <summary>
    /// Writes registers of one view's copy of a row as they are, view-own registers included (a
    /// row whose view-space constants its owner computes per view). View 1 only while split.
    /// </summary>
    public static void SetRowBlockView(int view, bool pixel, int row, int register, ReadOnlySpan<float> values)
    {
        if (row <= 0 || row >= Rows)
        {
            return;
        }
        StageState state = Stage(pixel);
        lock (Gate)
        {
            if (view == 0)
            {
                Write(state, state.Values, row, register, values);
            }
            else if (_views == 2 && state.Second is { } second)
            {
                Write(state, second, row, register, values);
            }
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
            foreach (StageState state in Stages)
            {
                Resize(state, RowStep);
            }
        }

        ImageTexture pixelTexture = _pixel.Texture!;
        ImageTexture vertexTexture = _vertex.Texture!;

        // The globals are declared in project.godot; added here as well so a tool scene that
        // runs without the project's list still resolves them.
        bool hasPixel = false, hasVertex = false;
        foreach (StringName name in Refii.Core.Engine_ServerSyncs.GlobalParameters())
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
            int width = Registers * _views;
            for (int s = 0; s < 2; s++)
            {
                StageState state = s == 0 ? _pixel : _vertex;
                StringName global = s == 0 ? PixelGlobal : VertexGlobal;
                if (state.Texture is null)
                {
                    continue;
                }
                if (height > state.Height || width != state.Width)
                {
                    // Grown, or split / rejoined: a new texture of the new size (Update needs the same).
                    Resize(state, Math.Max(height, state.Height));
                    RenderingServer.GlobalShaderParameterSet(global, state.Texture!);
                    state.Dirty = true;
                }
                if (!state.Dirty || state.Image is null)
                {
                    continue;
                }
                Pack(state);
                state.Image.SetData(state.Width, state.Height, false, Image.Format.Rgbaf, state.Bytes);
                state.Texture.Update(state.Image);
                state.Dirty = false;
            }
        }
    }

    /// <summary>The uploaded height grows in steps of this many rows.</summary>
    private const int RowStep = 32;

    private static readonly StageState[] Stages = { _pixel, _vertex };

    /// <summary>A stage's image and texture at <paramref name="height"/> rows and the views' width (a new texture: Update needs the same size). Caller holds the gate.</summary>
    private static void Resize(StageState state, int height)
    {
        state.Height = height;
        state.Width = Registers * _views;
        state.Bytes = new byte[state.Width * 16 * height];
        Pack(state);
        state.Image = Image.CreateFromData(state.Width, height, false, Image.Format.Rgbaf, state.Bytes);
        state.Texture = ImageTexture.CreateFromImage(state.Image);
    }

    /// <summary>
    /// The uploaded rows into the stage's bytes: the rows as they are for one view, each row's two
    /// view blocks side by side for two. Caller holds the gate.
    /// </summary>
    private static void Pack(StageState state)
    {
        if (state.Width == Registers || state.Second is not { } second)
        {
            Buffer.BlockCopy(state.Values, 0, state.Bytes, 0, Math.Min(state.Bytes.Length, state.Values.Length * 4));
            return;
        }
        const int rowBytes = Floats * 4;
        for (int row = 0; row < state.Height; row++)
        {
            Buffer.BlockCopy(state.Values, row * rowBytes, state.Bytes, row * 2 * rowBytes, rowBytes);
            Buffer.BlockCopy(second, row * rowBytes, state.Bytes, (row * 2 + 1) * rowBytes, rowBytes);
        }
    }

    /// <summary>One register run into one row of one block; true when anything changed. Caller holds the gate.</summary>
    private static bool Write(StageState state, float[] block, int row, int register, ReadOnlySpan<float> values)
    {
        // A register the title does not declare (Engine_ClassicRegisters.Absent) takes nothing.
        if (register < 0)
        {
            return false;
        }
        int at = row * Floats + register * 4;
        int length = Math.Min(values.Length, (row + 1) * Floats - at);
        if (length <= 0)
        {
            return false;
        }
        ReadOnlySpan<float> incoming = values.Slice(0, length);
        Span<float> target = block.AsSpan(at, length);
        if (incoming.SequenceEqual(target))
        {
            return false;
        }
        incoming.CopyTo(target);
        state.Dirty = true;
        return true;
    }
}

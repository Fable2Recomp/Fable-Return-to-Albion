using System;
using System.Threading;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// The tree cards' atlas (<see cref="AndroidTreeCards"/>): a texture of its own, 8 cells across, with four
/// box mips, written a few cells at a time. It runs on the capture camera's compositor: at the end of
/// each capture render it copies each of that render's cells into its own cell of the atlas and makes
/// that cell's mips (tree_card_atlas.glsl). Cells not in the job keep their picture.
/// </summary>
/// <remarks>
/// A job is handed over on the render thread's queue, ahead of the frame's draw, and the callback marks
/// it done (<see cref="Done"/>), so the main thread sees which cells hold their new picture. When the
/// atlas needs more rows, a new texture is made, the old rows copied in, and the texture and the
/// material's row count are re-pointed together on the main thread (<see cref="Resized"/>), so no frame
/// samples one with the other. The capture's colour must be RGBA16F (a transparent HDR viewport).
/// </remarks>
public partial class AndroidTreeCardAtlas : CompositorEffect
{
    public const string ShaderPath = "res://src/android/tree_card_atlas.glsl";

    /// <summary>What the cards sample; empty until the first job lands.</summary>
    public Texture2Drd Texture { get; } = new();

    /// <summary>Called on the main thread with the rows of the atlas just bound to <see cref="Texture"/>.</summary>
    public Action<int>? Resized;

    private sealed record Job(long Id, int Rows, int BatchColumns, int[] Cells);

    private RenderingDevice? _rd;
    private Rid _shader, _pipeline, _nearest, _target;
    private int _rows;
    private readonly Rid[] _views = new Rid[AndroidTreeCardMaths.Mips];
    private readonly Engine_UniformSetKey?[] _sets = new Engine_UniformSetKey?[AndroidTreeCardMaths.Mips];
    private readonly byte[] _push = new byte[32];
    private bool _ready;
    private Job? _job;
    private Action<Image?>? _dump;
    private long _done;

    /// <summary>The last job the render thread finished. Any thread.</summary>
    public long Done => Interlocked.Read(ref _done);

    public AndroidTreeCardAtlas()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;
        RenderingServer.CallOnRenderThread(Callable.From(Initialise));
    }

    /// <summary>
    /// The next capture render's job: its cell <c>i</c> (in a grid <paramref name="batchColumns"/> cells
    /// across) goes to atlas cell <c>cells[i]</c> of an atlas <paramref name="rows"/> rows high. Main thread.
    /// </summary>
    public void Submit(long id, int rows, int batchColumns, int[] cells)
    {
        // A statement body: an assignment expression would make this a Func whose Job result Godot converts.
        RenderingServer.CallOnRenderThread(Callable.From(() => { _job = new Job(id, rows, batchColumns, cells); }));
    }

    /// <summary>The atlas's mip 0 as an RGBAH image, handed to <paramref name="done"/> on the main thread (debug).</summary>
    public void Dump(Action<Image?> done) => RenderingServer.CallOnRenderThread(Callable.From(() => { _dump = done; }));

    private void Initialise()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null || GD.Load<RDShaderFile>(ShaderPath) is not { } file)
        {
            GD.PushError("[tree-cards] the atlas did not initialise (" + ShaderPath + ")");
            return;
        }

        _shader = _rd.Shader(file.GetSpirV());
        if (!_shader.IsValid)
        {
            return;
        }

        _pipeline = _rd.ComputePipeline(_shader);
        _nearest = _rd.Sampler(new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        });
        _ready = _pipeline.IsValid && _nearest.IsValid;
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (_rd is not null)
        {
            Engine_RdRetire.Pump(_rd);
        }

        if (!_ready || _rd is null)
        {
            return;
        }

        if (_job is { } job && renderData.GetRenderSceneBuffers() is RenderSceneBuffersRD buffers)
        {
            _job = null;
            Rid source = buffers.GetColorLayer(0);
            if (source.IsValid && _rd.TextureGetFormat(source) is { Format: RenderingDevice.DataFormat.R16G16B16A16Sfloat })
            {
                if (EnsureTarget(job.Rows))
                {
                    Write(source, job);
                    Interlocked.Exchange(ref _done, job.Id);
                }
            }
        }

        if (_dump is { } dump)
        {
            _dump = null;
            Image? image = null;
            int width = AndroidTreeCardMaths.Columns * AndroidTreeCardMaths.CellPixels;
            int height = _rows * AndroidTreeCardMaths.CellPixels;
            int top = width * height * AndroidTreeCardMaths.BytesPerTexel;
            // The layer's data holds every mip; the image takes the first.
            if (_target.IsValid && _rd.TextureGetData(_target, 0) is { } bytes && bytes.Length >= top)
            {
                image = Image.CreateFromData(width, height, false, Image.Format.Rgbah, bytes[..top]);
            }

            Callable.From(() => dump(image)).CallDeferred();
        }
    }

    /// <summary>An atlas <paramref name="rows"/> rows high, the old one's rows copied in when it grows.</summary>
    private bool EnsureTarget(int rows)
    {
        if (_target.IsValid && rows <= _rows)
        {
            return true;
        }

        if (_rd is not { } rd || Engine_RdHealth.IsDeviceLost)
        {
            return false;
        }

        int cell = AndroidTreeCardMaths.CellPixels;
        int width = AndroidTreeCardMaths.Columns * cell;
        Rid target = rd.Texture(new RDTextureFormat
        {
            Width = (uint)width, Height = (uint)(rows * cell), Mipmaps = AndroidTreeCardMaths.Mips,
            Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit |
                RenderingDevice.TextureUsageBits.CanCopyFromBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
        }, new RDTextureView());
        if (!target.IsValid)
        {
            return false;
        }

        var views = new Rid[AndroidTreeCardMaths.Mips];
        for (int mip = 0; mip < views.Length; mip++)
        {
            views[mip] = rd.TextureSharedFromSlice(new RDTextureView(), target, 0, (uint)mip, 1,
                RenderingDevice.TextureSliceType.Slice2D);
            if (!views[mip].IsValid)
            {
                for (int i = 0; i < mip; i++)
                {
                    if (views[i].IsValid)
                    {
                        rd.FreeRid(views[i]);
                    }
                }

                rd.FreeRid(target);
                return false;
            }
        }

        rd.TextureClear(target, new Color(0f, 0f, 0f, 0f), 0, AndroidTreeCardMaths.Mips, 0, 1);
        Engine_TracedMemory.Alloc(Engine_RdRetire.TracedPool, target.Id, (ulong)AndroidTreeCardMaths.AtlasBytes(rows * AndroidTreeCardMaths.Columns));

        if (_target.IsValid)
        {
            // The cells already captured keep their place: the atlas only grows downwards.
            for (int mip = 0; mip < views.Length; mip++)
            {
                var size = new Vector2I(Math.Max(1, width >> mip), Math.Max(1, (_rows * cell) >> mip));
                Rid set = Engine_RdCalls.CachedUniformSet(_shader, 0, Sampled(_views[mip]), Stored(views[mip]));
                long list = rd.ComputeListBegin();
                rd.ComputeListBindComputePipeline(list, _pipeline);
                rd.ComputeListBindUniformSet(list, set, 0);
                Dispatch(list, Vector2I.Zero, Vector2I.Zero, size, false);
                rd.ComputeListEnd();
            }

            for (int mip = 0; mip < _views.Length; mip++)
            {
                Engine_RdRetire.Retire(_views[mip]);
            }

            Engine_RdRetire.Retire(_target);
        }

        _target = target;
        Array.Copy(views, _views, views.Length);
        _rows = rows;
        Texture2Drd texture = Texture;
        Action<int>? resized = Resized;
        Callable.From(() =>
        {
            texture.TextureRdRid = target;
            resized?.Invoke(rows);
        }).CallDeferred();
        return true;
    }

    /// <summary>The job's cells copied from the capture into the atlas, then each cell's mips from the one above.</summary>
    private void Write(Rid source, Job job)
    {
        int cell = AndroidTreeCardMaths.CellPixels;
        Span<Rid> rids = stackalloc Rid[2];
        for (int mip = 0; mip < AndroidTreeCardMaths.Mips; mip++)
        {
            Rid read = mip == 0 ? source : _views[mip - 1];
            Engine_UniformSetKey key = _sets[mip] ??= new Engine_UniformSetKey(2);
            rids[0] = read;
            rids[1] = _views[mip];
            Rid set = key.For(_rd!, rids);
            if (!set.IsValid)
            {
                set = key.Hold(Engine_RdCalls.CachedUniformSet(_shader, 0, Sampled(read), Stored(_views[mip])));
            }

            int side = Math.Max(1, cell >> mip);
            long list = _rd!.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(list, _pipeline);
            _rd.ComputeListBindUniformSet(list, set, 0);
            for (int i = 0; i < job.Cells.Length; i++)
            {
                Vector2I at = AndroidTreeCardMaths.CellPixel(job.Cells[i]);
                Vector2I to = new(at.X >> mip, at.Y >> mip);
                Vector2I from = mip == 0
                    ? AndroidTreeCardMaths.CellPixel(i, job.BatchColumns)
                    : new Vector2I(at.X >> (mip - 1), at.Y >> (mip - 1));
                Dispatch(list, from, to, new Vector2I(side, side), mip > 0);
            }

            _rd.ComputeListEnd();
        }
    }

    private void Dispatch(long list, Vector2I from, Vector2I to, Vector2I size, bool box)
    {
        Span<int> words = stackalloc int[8] { from.X, from.Y, to.X, to.Y, size.X, size.Y, box ? 1 : 0, 0 };
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(words).CopyTo(_push);
        _rd!.ComputeListSetPushConstant(list, _push, (uint)_push.Length);
        _rd.ComputeListDispatch(list, (uint)((size.X + 7) / 8), (uint)((size.Y + 7) / 8), 1);
    }

    private RDUniform Sampled(Rid texture)
    {
        var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 };
        uniform.AddId(_nearest);
        uniform.AddId(texture);
        return uniform;
    }

    private static RDUniform Stored(Rid texture)
    {
        var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 };
        uniform.AddId(texture);
        return uniform;
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete)
        {
            return;
        }

        Rid shader = _shader, pipeline = _pipeline, nearest = _nearest, target = _target;
        Rid[] views = (Rid[])_views.Clone();
        _ready = false;
        Engine_RdRetire.OnRenderThread(() =>
        {
            if (RenderingServer.GetRenderingDevice() is not { } rd)
            {
                return;
            }

            foreach (Rid view in views)
            {
                if (view.IsValid)
                {
                    rd.FreeRid(view);
                }
            }

            if (target.IsValid)
            {
                Engine_TracedMemory.Free(Engine_RdRetire.TracedPool, target.Id);
                rd.FreeRid(target);
            }

            if (nearest.IsValid) rd.FreeRid(nearest);
            if (pipeline.IsValid) rd.FreeRid(pipeline);
            if (shader.IsValid) rd.FreeRid(shader);
        });
    }
}

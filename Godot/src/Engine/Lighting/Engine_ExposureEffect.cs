using System;
using Godot;
using Godot.Collections;

namespace Refii.Lighting;

/// <summary>
/// The guest's auto-exposure, running on the frame Godot just rendered.
///
/// THREE compute passes on the HDR colour buffer, and NOTHING CROSSES THE BUS. Bin this frame's
/// luminance into a 512-bucket histogram, reduce it to the guest's three adapted values and its
/// exposure scalar, and apply the resulting curve — all on the GPU, with the state living in a
/// storage buffer only the device touches.
///
/// NOTHING MAY CROSS THE BUS. `BufferGetData` flushes the rendering device whether or not the data
/// it wants is already resident, serialising the CPU against the GPU; sampling it less often only
/// converts a constant loss into a periodic stall. The result never leaves the GPU.
///
/// GODOT'S TONEMAPPER MUST BE OFF WHEREVER THIS RUNS, and <see cref="Engine_LevelLighting"/> sets
/// it to Linear for exactly that reason. The guest tone-maps once, and so must we: a curve applied
/// on top of Godot's Reinhardt compresses the frame twice and no amount of correcting the white
/// point recovers it.
///
/// The arithmetic is not here. It lives in <see cref="Engine_AutoExposure"/> so it can be run and
/// checked without a GPU — see <c>ExposureCheck</c> — and this class is only the plumbing.
/// </summary>
public partial class Engine_ExposureEffect : CompositorEffect
{
	public const string HistogramShaderPath =
		"res://src/Engine/Lighting/fable_exposure_histogram.glsl";

	public const string ApplyShaderPath =
		"res://src/Engine/Lighting/fable_exposure_apply.glsl";

	public const string ReduceShaderPath =
		"res://src/Engine/Lighting/fable_exposure_reduce.glsl";

	/// <summary>min, avg, max, exposure, prevExposure, curveScale, invWhiteSq, brightness.</summary>
	private const int StateFloats = 8;

	private const int StateBytes = StateFloats * sizeof(float);

	/// <summary>Bytes in the histogram buffer: 512 buckets of one uint.</summary>
	private const int HistogramBytes = Engine_AutoExposure.Buckets * sizeof(uint);

	private RenderingDevice? _rd;

	private Rid _histogramShader;
	private Rid _histogramPipeline;
	private Rid _applyShader;
	private Rid _applyPipeline;
	private Rid _histogramBuffer;
	private Rid _reduceShader;
	private Rid _reducePipeline;
	private Rid _stateBuffer;
	private bool _firstFrame = true;

	private bool _ready;

	private bool _measuredAny;

	private double _lastFrameSeconds;

	public Engine_ExposureEffect()
	{
		// POST-TRANSPARENT is the last callback that sees the frame in linear HDR, before Godot's
		// own post-processing. Anything later would be operating on an already-encoded image.
		EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;

		RenderingServer.CallOnRenderThread(Callable.From(Initialise));
	}

	private void Initialise()
	{
		_rd = RenderingServer.GetRenderingDevice();
		if (_rd is null)
		{
			// Headless, or a driver without compute. Not an error: the effect simply does nothing
			// and the frame goes through untouched.
			return;
		}

		if (!TryBuild(HistogramShaderPath, out _histogramShader, out _histogramPipeline) ||
			!TryBuild(ReduceShaderPath, out _reduceShader, out _reducePipeline) ||
			!TryBuild(ApplyShaderPath, out _applyShader, out _applyPipeline))
		{
			return;
		}

		_histogramBuffer = _rd.StorageBufferCreate(HistogramBytes, new byte[HistogramBytes]);

		// SEEDED, NOT ZEROED. The exposure scalar starts at the guest's own 4.0 and the curve terms
		// have to be usable on the very first frame — a zero brightness would render one black
		// frame before the reduction ever runs.
		var seed = new float[StateFloats] { 0.0f, 0.3f, 0.6f, 4.0f, 4.0f, 0.3f, 1.0f / 0.36f, 4.0f };
		var seedBytes = new byte[StateBytes];
		System.Buffer.BlockCopy(seed, 0, seedBytes, 0, StateBytes);
		_stateBuffer = _rd.StorageBufferCreate(StateBytes, seedBytes);

		_ready = _histogramBuffer.IsValid && _stateBuffer.IsValid;
	}

	private bool TryBuild(string path, out Rid shader, out Rid pipeline)
	{
		shader = default;
		pipeline = default;

		var file = GD.Load<RDShaderFile>(path);
		if (file is null)
		{
			GD.PrintErr($"[exposure] could not load {path}");
			return false;
		}

		RDShaderSpirV spirv = file.GetSpirV();
		if (spirv.CompileErrorCompute.Length != 0)
		{
			GD.PrintErr($"[exposure] {path} failed to compile: {spirv.CompileErrorCompute}");
			return false;
		}

		shader = _rd!.ShaderCreateFromSpirV(spirv);
		if (!shader.IsValid)
		{
			return false;
		}

		pipeline = _rd.ComputePipelineCreate(shader);
		return pipeline.IsValid;
	}

	public override void _Notification(int what)
	{
		if (what != NotificationPredelete || _rd is null)
		{
			return;
		}

		// Rids are not reference counted. A compositor effect outlives individual frames but not
		// the level, and leaking a pipeline per level change is a slow leak that reads as a driver
		// problem rather than as ours.
		Free(_histogramPipeline);
		Free(_reducePipeline);
		Free(_applyPipeline);
		Free(_histogramShader);
		Free(_reduceShader);
		Free(_applyShader);
		Free(_histogramBuffer);
		Free(_stateBuffer);
	}

	private void Free(Rid rid)
	{
		if (rid.IsValid)
		{
			_rd!.FreeRid(rid);
		}
	}

	public override void _RenderCallback(int effectCallbackType, RenderData renderData)
	{
		if (!_ready || _rd is null)
		{
			return;
		}

		if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers)
		{
			return;
		}

		Vector2I size = buffers.GetInternalSize();
		if (size.X <= 0 || size.Y <= 0)
		{
			return;
		}

		// NOTHING IS READ BACK. The order is bin -> reduce -> apply, all on the GPU, so the CPU
		// never waits on the device. Reduce consumes the histogram this frame's binning just
		// wrote, which drops the guest's one-frame measurement lag; the loop moves at 1% per
		// 30 Hz tick, so a frame of latency either way is invisible and the sync it saves is not.
		float dt = Elapsed();

		for (uint view = 0; view < buffers.GetViewCount(); view++)
		{
			Rid colour = buffers.GetColorLayer(view);

			_rd.BufferClear(_histogramBuffer, 0, HistogramBytes);
			Histogram(colour, size);
			Reduce(dt);
			Apply(colour, size);
		}

		_firstFrame = false;
	}

	/// <summary>Real seconds since the last frame, for the temporal blend.</summary>
	private float Elapsed()
	{
		double now = Time.GetTicksUsec() / 1_000_000.0;
		float dt = _measuredAny ? (float)(now - _lastFrameSeconds) : 0.0f;
		_lastFrameSeconds = now;
		_measuredAny = true;
		return dt;
	}

	private void Reduce(float dt)
	{
		var uniforms = new Array<RDUniform>
		{
			Buffer(_histogramBuffer, 0),
			Buffer(_stateBuffer, 1),
		};

		var push = new byte[16];
		BitConverter.TryWriteBytes(push.AsSpan(0), dt);
		BitConverter.TryWriteBytes(push.AsSpan(4), _firstFrame ? 1.0f : 0.0f);

		Dispatch(
			_reducePipeline,
			UniformSetCacheRD.GetCache(_reduceShader, 0, uniforms),
			push,
			1,
			1);
	}

	private void Histogram(Rid colour, Vector2I size)
	{
		var uniforms = new Array<RDUniform>
		{
			Image(colour, 0),
			Buffer(_histogramBuffer, 1),
		};

		Vector2I quarter = new(Math.Max(size.X / 4, 1), Math.Max(size.Y / 4, 1));

		Dispatch(
			_histogramPipeline,
			UniformSetCacheRD.GetCache(_histogramShader, 0, uniforms),
			Push(size, Engine_AutoExposure.Buckets / Engine_AutoExposure.Range,
				Engine_AutoExposure.Range),
			(quarter.X + 15) / 16,
			(quarter.Y + 15) / 16);
	}

	private void Apply(Rid colour, Vector2I size)
	{
		var uniforms = new Array<RDUniform>
		{
			Image(colour, 0),
			Buffer(_stateBuffer, 1),
		};

		Dispatch(
			_applyPipeline,
			UniformSetCacheRD.GetCache(_applyShader, 0, uniforms),
			Push(size, 0.0f, 0.0f),
			(size.X + 7) / 8,
			(size.Y + 7) / 8);
	}

	private void Dispatch(Rid pipeline, Rid uniformSet, byte[] push, int groupsX, int groupsY)
	{
		long list = _rd!.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(list, pipeline);
		_rd.ComputeListBindUniformSet(list, uniformSet, 0);
		_rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
		_rd.ComputeListDispatch(list, (uint)groupsX, (uint)groupsY, 1);
		_rd.ComputeListEnd();
	}

	private static RDUniform Image(Rid texture, int binding)
	{
		var uniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.Image,
			Binding = binding,
		};

		uniform.AddId(texture);
		return uniform;
	}

	private static RDUniform Buffer(Rid buffer, int binding)
	{
		var uniform = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.StorageBuffer,
			Binding = binding,
		};

		uniform.AddId(buffer);
		return uniform;
	}

	/// <summary>
	/// The push constant block: two ints and two floats, which is the 16 bytes Vulkan wants.
	/// </summary>
	private static byte[] Push(Vector2I size, float a, float b, float c = 0.0f)
	{
		// 32 bytes, not 16: Vulkan wants the block padded to a multiple of 16 and the apply pass
		// carries a fifth value. The histogram pass reads only the first four and ignores the rest.
		var bytes = new byte[32];
		BitConverter.TryWriteBytes(bytes.AsSpan(0), size.X);
		BitConverter.TryWriteBytes(bytes.AsSpan(4), size.Y);
		BitConverter.TryWriteBytes(bytes.AsSpan(8), a);
		BitConverter.TryWriteBytes(bytes.AsSpan(12), b);
		BitConverter.TryWriteBytes(bytes.AsSpan(16), c);
		return bytes;
	}
}

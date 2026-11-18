using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Refii.Core;

/// <summary>
/// A census of the value-returning RenderingServer calls our code still makes, by call site:
/// how many, how long the caller waited, on which thread.
/// </summary>
/// <remarks>
/// WHY THEY ARE COUNTED. A getter (`FUNCnR`/`FUNCnRC`/`FUNCnSC` in Godot's
/// rendering_server_default.h) is a plain read under the Safe thread model. With the separate
/// render thread (`thread_model = 2`) it is `push_and_ret`: the caller waits until the render
/// thread has run everything queued before it — the rest of a `_draw` in flight, the commands
/// behind it, and, because that thread is a WorkerThreadPool pump that runs other pool tasks
/// while it yields (`WorkerThreadPool::_wait_collaboratively`), a shader or pipeline compile it
/// picked up. During a load every one of those is plentiful, so a getter inside a budgeted loop
/// can spend the whole budget waiting. Under Safe the same count costs next to nothing, so the
/// two models' counts say how much of a load's difference is waiting.
///
/// Counted at OUR call sites only (the engine's own getters cannot be seen from here), one
/// interlocked add pair per call; the wait is the call's wall time, which under Safe is the
/// read itself.
/// </remarks>
public static class Engine_ServerSyncs
{
	/// <summary>The call sites, fixed so the counters are plain arrays.</summary>
	public enum Site
	{
		/// <summary>`ShaderMaterial.Duplicate()` itself: its property list asks the server for the
		/// shader's uniforms, and for each never-set one's default (Engine_MaterialCopy falls back to it).</summary>
		MaterialDuplicate,
		/// <summary>A shader's uniform list, once per shader (Engine_MaterialCopy).</summary>
		ShaderUniformList,
		/// <summary>A shader uniform's default value, once per shader and uniform (Engine_MaterialCopy).</summary>
		ShaderParamDefault,
		/// <summary>`MultiMesh.GetInstanceTransform` where no build record holds the transform.</summary>
		MultimeshTransform,
		/// <summary>`MultiMesh.Buffer` read back where no build record holds the transforms.</summary>
		MultimeshBuffer,
		/// <summary>`MultiMesh.GetAabb` (a batch's `GetAabb`) with no custom box to answer it.</summary>
		MultimeshAabb,
		/// <summary>`MultiMesh.GetInstanceColor`.</summary>
		MultimeshColor,
		/// <summary>`GetInstanceShaderParameter`, and `SetInstanceShaderParameter` with nil (it reads the default).</summary>
		InstanceParam,
		/// <summary>`ArrayMesh.SurfaceGetArrays`: a GPU readback as well.</summary>
		SurfaceArrays,
		/// <summary>`Texture2D.GetImage`: a GPU readback as well.</summary>
		TextureImage,
		/// <summary>`RenderingServer.TextureGetRdTexture` from a thread that is not the render thread.</summary>
		TextureRd,
		/// <summary>`RenderingServer.GlobalShaderParameterGetList`.</summary>
		GlobalParamList,
	}

	private static readonly int SiteCount = System.Enum.GetValues<Site>().Length;

	private static readonly long[] Counts = new long[SiteCount];
	private static readonly long[] Ticks = new long[SiteCount];
	private static readonly long[] MainCounts = new long[SiteCount];
	private static readonly long[] MainTicks = new long[SiteCount];
	private static readonly long[] PeakTicks = new long[SiteCount];

	private static readonly GpuUploadPacer DefaultPacer = new();

	/// <summary>Names the calling thread as the main thread; called once from the frame-start node.</summary>
	public static void MarkMainThread() => DefaultPacer.MarkMainThread();

	/// <summary>Clears the recorded main thread ID and drains any pending items (for isolated unit tests).</summary>
	internal static void ResetMainThreadForTest() => DefaultPacer.Reset();

	/// <summary>Records that the main thread is actively ticking its frame loop.</summary>
	public static void NotifyMainFrameTick() => DefaultPacer.NotifyMainFrameTick();

	/// <summary>Whether the caller is the main thread (false before <see cref="MarkMainThread"/>).</summary>
	public static bool OnMainThread => DefaultPacer.OnMainThread;

	/// <summary>
	/// Maximum background GPU upload staging bytes per drawn frame before background workers yield
	/// for the next frame fence (32 MB/frame in play, 96 MB/frame under load cover on Vulkan;
	/// 24 MB / 64 MB on D3D12).
	/// </summary>
	internal const long MaxBackgroundUploadBytesPerFrame = 32L * 1024L * 1024L;
	internal const long MaxLoadCoverUploadBytesPerFrame = 96L * 1024L * 1024L;
	internal const long MaxD3D12BackgroundUploadBytesPerFrame = 24L * 1024L * 1024L;
	internal const long MaxD3D12LoadCoverUploadBytesPerFrame = 64L * 1024L * 1024L;

	/// <summary>Per-frame staging upload ceiling tailored by driver and load-cover state.</summary>
	public static long EffectiveMaxUploadBytesPerFrame(bool loadCoverActive, bool isD3D12)
	{
		if (isD3D12)
		{
			return loadCoverActive ? MaxD3D12LoadCoverUploadBytesPerFrame : MaxD3D12BackgroundUploadBytesPerFrame;
		}

		return loadCoverActive ? MaxLoadCoverUploadBytesPerFrame : MaxBackgroundUploadBytesPerFrame;
	}

	/// <summary>Bytes staged into the current frame's command list across both main-thread and worker uploads.</summary>
	internal static long FrameUploadBytes => DefaultPacer.FrameUploadBytes;

	/// <summary>Number of background worker GPU upload items still waiting in the queue.</summary>
	public static int PendingUploadCount => DefaultPacer.PendingWorkerUploads;

	/// <summary>True while a loading screen cover holds over the world, permitting larger GPU upload drains per frame.</summary>
	public static bool LoadCoverActive
	{
		get => DefaultPacer.LoadCoverActive;
		set => DefaultPacer.LoadCoverActive = value;
	}

	internal sealed class GpuUploadPacer : System.IDisposable
	{
		private sealed class GpuUploadWorkItem
		{
			public readonly long Bytes;
			public readonly System.Action Action;
			public readonly ManualResetEventSlim Done = new(false);
			public System.Exception? Error;
			public int State;

			public GpuUploadWorkItem(long bytes, System.Action action)
			{
				Bytes = bytes;
				Action = action;
			}

			public bool TryExecute(GpuUploadPacer owner)
			{
				if (Interlocked.CompareExchange(ref State, 1, 0) != 0)
				{
					return false;
				}

				if (Engine_RdHealth.IsDeviceLost)
				{
					Engine_RdHealth.NoteSuppressed();
					Volatile.Write(ref State, 2);
					Done.Set();
					return false;
				}

				try
				{
					lock (owner._gpuUploadGate)
					{
						Interlocked.Add(ref owner._frameUploadBytes, Bytes);
						Action();
					}
				}
				catch (System.Exception ex)
				{
					Error = ex;
				}
				finally
				{
					Volatile.Write(ref State, 2);
					Done.Set();
				}

				return true;
			}

			public void Cancel()
			{
				if (Interlocked.CompareExchange(ref State, 2, 0) == 0)
				{
					Done.Set();
				}
			}
		}

		private readonly object _gpuUploadGate = new();
		private readonly System.Collections.Concurrent.ConcurrentQueue<GpuUploadWorkItem> _workerUploads = new();
		private readonly ManualResetEventSlim _uploadAvailable = new(false);
		private int _mainThreadId = -1;
		private long _lastMainFrameTickMs;
		private bool _inDraw;
		private long _drawnFrame;
		private long _frameUploadBytes;

		public bool LoadCoverActive { get; set; }

		public bool OnMainThread => System.Environment.CurrentManagedThreadId == Volatile.Read(ref _mainThreadId);

		public long FrameUploadBytes => Interlocked.Read(ref _frameUploadBytes);

		public int PendingWorkerUploads => _workerUploads.Count;

		public void MarkMainThread()
		{
			Volatile.Write(ref _mainThreadId, System.Environment.CurrentManagedThreadId);
			Volatile.Write(ref _lastMainFrameTickMs, System.Environment.TickCount64);
		}

		public void NotifyMainFrameTick() =>
			Volatile.Write(ref _lastMainFrameTickMs, System.Environment.TickCount64);

		public void MarkDrawBegin()
		{
			lock (_gpuUploadGate)
			{
				Volatile.Write(ref _inDraw, true);
			}
		}

		public void MarkDrawEnd()
		{
			lock (_gpuUploadGate)
			{
				Volatile.Write(ref _inDraw, false);
				_drawnFrame++;
				Interlocked.Exchange(ref _frameUploadBytes, 0L);
				Monitor.PulseAll(_gpuUploadGate);
			}
		}

		public void BeginMainFrameUploadBudget()
		{
			NotifyMainFrameTick();
			Interlocked.Exchange(ref _frameUploadBytes, 0L);
		}

		public int PumpWorkerGpuUploads(double budgetMs, bool waitForWorkers = false)
		{
			NotifyMainFrameTick();
			if (!OnMainThread && Volatile.Read(ref _mainThreadId) != -1)
			{
				return 0;
			}

			long start = Stopwatch.GetTimestamp();
			long maxTicks = (long)(System.Math.Max(0.5, budgetMs) * (Stopwatch.Frequency / 1000.0));
			if (Engine_RdHealth.IsDeviceLost)
			{
				while (_workerUploads.TryDequeue(out GpuUploadWorkItem? cancelled))
				{
					cancelled.Cancel();
				}
				return 0;
			}

			int drained = 0;
			while (true)
			{
				if (!_workerUploads.TryPeek(out GpuUploadWorkItem? next))
				{
					if (!waitForWorkers || !LoadCoverActive)
					{
						break;
					}

					long waitMaxBytes = EffectiveMaxUploadBytesPerFrame(
						LoadCoverActive, Refii.Textures.Engine_TextureUpload.IsD3D12Driver);
					if (Interlocked.Read(ref _frameUploadBytes) >= waitMaxBytes)
					{
						break;
					}

					long elapsed = Stopwatch.GetTimestamp() - start;
					if (elapsed >= maxTicks)
					{
						break;
					}

					_uploadAvailable.Reset();
					if (!_workerUploads.IsEmpty)
					{
						continue;
					}

					int remainingMs = (int)System.Math.Ceiling((maxTicks - elapsed) * 1000.0 / Stopwatch.Frequency);
					if (remainingMs <= 0)
					{
						break;
					}

					_uploadAvailable.Wait(System.Math.Min(1, remainingMs));
					continue;
				}

				if (Engine_RdHealth.IsDeviceLost)
				{
					while (_workerUploads.TryDequeue(out GpuUploadWorkItem? cancelled))
					{
						cancelled.Cancel();
					}
					break;
				}

				if (Volatile.Read(ref next.State) != 0)
				{
					_workerUploads.TryDequeue(out _);
					continue;
				}

				long maxBytes = EffectiveMaxUploadBytesPerFrame(
					LoadCoverActive, Refii.Textures.Engine_TextureUpload.IsD3D12Driver);
				long currentBytes = Interlocked.Read(ref _frameUploadBytes);
				if (currentBytes >= maxBytes && currentBytes > 0)
				{
					break;
				}

				if (!_workerUploads.TryDequeue(out GpuUploadWorkItem? item))
				{
					continue;
				}

				if (item.TryExecute(this))
				{
					++drained;
				}

				NotifyMainFrameTick();

				if (Stopwatch.GetTimestamp() - start >= maxTicks)
				{
					break;
				}
			}

			return drained;
		}

		public void RunGpuUpload(long estimatedBytes, System.Action action)
		{
			if (Engine_RdHealth.IsDeviceLost)
			{
				Engine_RdHealth.NoteSuppressed();
				return;
			}

			long bytes = System.Math.Max(4096L, estimatedBytes);
			bool isMain = OnMainThread || Volatile.Read(ref _mainThreadId) == -1;
			if (isMain)
			{
				lock (_gpuUploadGate)
				{
					if (Engine_RdHealth.IsDeviceLost)
					{
						Engine_RdHealth.NoteSuppressed();
						return;
					}
					Interlocked.Add(ref _frameUploadBytes, bytes);
					action();
				}

				return;
			}

			var item = new GpuUploadWorkItem(bytes, action);
			_workerUploads.Enqueue(item);
			_uploadAvailable.Set();
			item.Done.Wait();
			item.Done.Dispose();
			if (item.Error is not null)
			{
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(item.Error).Throw();
			}
		}

		public void Reset()
		{
			Volatile.Write(ref _mainThreadId, -1);
			LoadCoverActive = false;
			Interlocked.Exchange(ref _frameUploadBytes, 0L);
			_uploadAvailable.Reset();
			while (_workerUploads.TryDequeue(out GpuUploadWorkItem? item))
			{
				item.Cancel();
			}
		}

		public void Dispose() => Reset();
	}

	/// <summary>rendering_server FramePreDraw callback: pauses off-thread GPU resource uploads during draw().</summary>
	public static void MarkDrawBegin() => DefaultPacer.MarkDrawBegin();

	/// <summary>rendering_server FramePostDraw callback: advances the drawn-frame counter and resets the frame staging budget.</summary>
	public static void MarkDrawEnd() => DefaultPacer.MarkDrawEnd();

	/// <summary>
	/// Resets the per-frame upload budget at the start of a new main-thread frame (covering
	/// headless or minimized runs where <c>FramePostDraw</c> does not fire).
	/// </summary>
	public static void BeginMainFrameUploadBudget() => DefaultPacer.BeginMainFrameUploadBudget();

	/// <summary>
	/// Executes queued worker-thread GPU uploads on the main thread within the frame's time and
	/// staging-byte budgets so <see cref="Godot.RenderingDevice"/> is never accessed concurrently
	/// with main-thread <c>_Process</c> or <c>RenderingServer::draw()</c>.
	/// </summary>
	public static void PumpWorkerGpuUploads(double budgetMs) => DefaultPacer.PumpWorkerGpuUploads(budgetMs, false);

	/// <summary>
	/// Executes queued worker-thread GPU uploads on the main thread and, when <paramref name="waitForWorkers"/>
	/// is true during active load cover, services chained worker uploads within the frame window.
	/// </summary>
	public static int PumpWorkerGpuUploads(double budgetMs, bool waitForWorkers) =>
		DefaultPacer.PumpWorkerGpuUploads(budgetMs, waitForWorkers);

	/// <summary>
	/// Dispatches a <see cref="Godot.RenderingDevice"/> resource upload on the main thread
	/// (or inline when already on the main thread) and paces staging buffer usage across frames.
	/// </summary>
	public static void RunGpuUpload(long estimatedBytes, System.Action action) =>
		DefaultPacer.RunGpuUpload(estimatedBytes, action);

	/// <summary>
	/// Dispatches a value-returning <see cref="Godot.RenderingDevice"/> resource upload on the main
	/// thread (or inline when already on the main thread) and paces staging buffer usage across frames.
	/// </summary>
	public static T RunGpuUpload<T>(long estimatedBytes, System.Func<T> func)
	{
		T result = default!;
		RunGpuUpload(estimatedBytes, () =>
		{
			result = func();
		});
		return result;
	}

	/// <summary>A stamp to hand to <see cref="End"/> after the call.</summary>
	public static long Begin() => Stopwatch.GetTimestamp();

	/// <summary>Counts one call at a site and the time since <paramref name="from"/>. Any thread.</summary>
	public static void End(Site site, long from)
	{
		long ticks = Stopwatch.GetTimestamp() - from;
		int i = (int)site;
		Interlocked.Increment(ref Counts[i]);
		Interlocked.Add(ref Ticks[i], ticks);
		if (OnMainThread)
		{
			Interlocked.Increment(ref MainCounts[i]);
			Interlocked.Add(ref MainTicks[i], ticks);
		}

		long peak = Volatile.Read(ref PeakTicks[i]);
		while (ticks > peak)
		{
			long seen = Interlocked.CompareExchange(ref PeakTicks[i], ticks, peak);
			if (seen == peak)
			{
				break;
			}
			peak = seen;
		}
	}

	/// <summary>
	/// `GetAabb()` of a drawable, without the server where the answer is already ours: a batch
	/// (`MultiMeshInstance3D`) asks `multimesh_get_aabb`, a `FUNC1RC`, which answers the batch's
	/// custom box whenever one is set (`MeshStorage::_multimesh_get_aabb`), and that box is the
	/// one `MultiMesh.CustomAabb` holds. A batch without one still asks (counted). Anything else
	/// is its own `GetAabb` (a mesh node's is the mesh's cached box).
	/// </summary>
	public static Godot.Aabb LocalAabb(Godot.GeometryInstance3D node)
	{
		if (node is not Godot.MultiMeshInstance3D batch)
		{
			return node.GetAabb();
		}

		if (batch.Multimesh is not { } multimesh)
		{
			return default;
		}

		Godot.Aabb custom = multimesh.CustomAabb;
		if (custom != default)
		{
			return custom;
		}

		long from = Begin();
		Godot.Aabb read = multimesh.GetAabb();
		End(Site.MultimeshAabb, from);
		return read;
	}

	/// <summary>`MultiMesh.Buffer`, read back (counted).</summary>
	public static float[] Buffer(Godot.MultiMesh multimesh)
	{
		long from = Begin();
		float[] buffer = multimesh.Buffer;
		End(Site.MultimeshBuffer, from);
		return buffer;
	}

	/// <summary>`MultiMesh.GetInstanceColor` (counted).</summary>
	public static Godot.Color InstanceColor(Godot.MultiMesh multimesh, int instance)
	{
		long from = Begin();
		Godot.Color colour = multimesh.GetInstanceColor(instance);
		End(Site.MultimeshColor, from);
		return colour;
	}

	/// <summary>`ArrayMesh.SurfaceGetArrays`, a GPU readback (counted; serialized onto the main thread when called from a worker).</summary>
	public static Godot.Collections.Array SurfaceArrays(Godot.Mesh mesh, int surface)
	{
		long from = Begin();
		Godot.Collections.Array arrays = RunGpuUpload(4096L, () => mesh.SurfaceGetArrays(surface));
		End(Site.SurfaceArrays, from);
		return arrays;
	}

	/// <summary>`Texture2D.GetImage`, a GPU readback (counted; serialized onto the main thread when called from a worker); null for a null texture.</summary>
	public static Godot.Image? Image(Godot.Texture2D? texture)
	{
		if (texture is null)
		{
			return null;
		}

		long from = Begin();
		Godot.Image? image = RunGpuUpload(4096L, () => texture.GetImage());
		End(Site.TextureImage, from);
		return image;
	}

	/// <summary>`RenderingServer.TextureGetRdTexture` (counted; a plain read on the render thread).</summary>
	public static Godot.Rid RdTexture(Godot.Rid texture)
	{
		long from = Begin();
		Godot.Rid rd = Godot.RenderingServer.TextureGetRdTexture(texture);
		End(Site.TextureRd, from);
		return rd;
	}

	/// <summary>`RenderingServer.GlobalShaderParameterGetList` (counted).</summary>
	public static Godot.Collections.Array<Godot.StringName> GlobalParameters()
	{
		long from = Begin();
		Godot.Collections.Array<Godot.StringName> names = Godot.RenderingServer.GlobalShaderParameterGetList();
		End(Site.GlobalParamList, from);
		return names;
	}

	/// <summary>The counters as they stand, for a later <see cref="Report"/> against them.</summary>
	public readonly struct Snapshot
	{
		internal readonly long[]? Counts;
		internal readonly long[]? Ticks;
		internal readonly long[]? MainCounts;
		internal readonly long[]? MainTicks;

		internal Snapshot(long[] counts, long[] ticks, long[] mainCounts, long[] mainTicks)
		{
			Counts = counts;
			Ticks = ticks;
			MainCounts = mainCounts;
			MainTicks = mainTicks;
		}

		public bool IsEmpty => Counts is null;
	}

	public static Snapshot Take()
	{
		var counts = new long[SiteCount];
		var ticks = new long[SiteCount];
		var mainCounts = new long[SiteCount];
		var mainTicks = new long[SiteCount];
		for (int i = 0; i < SiteCount; i++)
		{
			counts[i] = Interlocked.Read(ref Counts[i]);
			ticks[i] = Interlocked.Read(ref Ticks[i]);
			mainCounts[i] = Interlocked.Read(ref MainCounts[i]);
			mainTicks[i] = Interlocked.Read(ref MainTicks[i]);
		}

		return new Snapshot(counts, ticks, mainCounts, mainTicks);
	}

	/// <summary>Calls and waits since <paramref name="since"/> (since the start when empty), between
	/// two snapshots when <paramref name="until"/> is given: totals, then each site that was called.</summary>
	public static Dictionary<string, object?> Report(Snapshot since = default, Snapshot until = default)
	{
		Snapshot now = until.IsEmpty ? Take() : until;
		double toMs = 1000.0 / Stopwatch.Frequency;
		long allCount = 0, allTicks = 0, mainCount = 0, mainTicks = 0;
		var sites = new Dictionary<string, object?>();
		for (int i = 0; i < SiteCount; i++)
		{
			long count = At(now.Counts, i) - At(since.Counts, i);
			if (count == 0)
			{
				continue;
			}

			long ticks = At(now.Ticks, i) - At(since.Ticks, i);
			long onMain = At(now.MainCounts, i) - At(since.MainCounts, i);
			long onMainTicks = At(now.MainTicks, i) - At(since.MainTicks, i);
			allCount += count;
			allTicks += ticks;
			mainCount += onMain;
			mainTicks += onMainTicks;
			sites[((Site)i).ToString()] = new Dictionary<string, object?>
			{
				["count"] = count,
				["ms"] = System.Math.Round(ticks * toMs, 1),
				["mainCount"] = onMain,
				["mainMs"] = System.Math.Round(onMainTicks * toMs, 1),
				// Since the process started, not since the window: a peak cannot be subtracted.
				["peakMs"] = System.Math.Round(Interlocked.Read(ref PeakTicks[i]) * toMs, 2),
			};
		}

		return new Dictionary<string, object?>
		{
			["count"] = allCount,
			["ms"] = System.Math.Round(allTicks * toMs, 1),
			["mainCount"] = mainCount,
			["mainMs"] = System.Math.Round(mainTicks * toMs, 1),
			["sites"] = sites,
		};
	}

	private static long At(long[]? values, int i) => values is null ? 0 : values[i];

	/// <summary>One line for the log: total calls and waits, and the three costliest sites.</summary>
	public static string Describe(Snapshot since, Snapshot until)
	{
		double toMs = 1000.0 / Stopwatch.Frequency;
		var rows = new List<(long Ticks, string Row)>();
		long allCount = 0, allTicks = 0, mainTicks = 0;
		for (int i = 0; i < SiteCount; i++)
		{
			long count = At(until.Counts, i) - At(since.Counts, i);
			if (count == 0)
			{
				continue;
			}
			long ticks = At(until.Ticks, i) - At(since.Ticks, i);
			allCount += count;
			allTicks += ticks;
			mainTicks += At(until.MainTicks, i) - At(since.MainTicks, i);
			rows.Add((ticks, $"{(Site)i} {count}x {ticks * toMs:0} ms"));
		}

		rows.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
		var line = new System.Text.StringBuilder($"{allCount} server sync(s), {allTicks * toMs:0} ms waited ({mainTicks * toMs:0} ms on main)");
		for (int i = 0; i < rows.Count && i < 3; i++)
		{
			line.Append(i == 0 ? ": " : ", ").Append(rows[i].Row);
		}

		return line.ToString();
	}
}

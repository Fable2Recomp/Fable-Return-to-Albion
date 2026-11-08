using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The world's Android teardown: the region retirement pump, which holds native preload while
/// the old region and its producers retire, and the phone's large-object collection.
/// </summary>
/// <remarks>
/// Desktop clears the region's caches at its teardown (<see cref="ReleaseRegionAtOnce"/>).
/// Android defers the clear until outgoing producers and queued native deletion retire; bounded
/// collection/finalizer passes precede the next build. Borrowed resources are never disposed here.
/// </remarks>
public partial class GameWorld
{
	/// <summary>
	/// A phone's `[guest-entities]` report comes once, under the loading cover. Every report walks the
	/// whole entity list and asks 512 of them for their components on the main thread; ten of them a
	/// region, 120 frames apart, are a longer frame every 6-7 s of a phone's first minute of play.
	/// </summary>
	private static int EntityReportFrames => Refii.Core.Engine_Platform.IsAndroid ? 30 : 120;

	private static int EntityReportLimit => Refii.Core.Engine_Platform.IsAndroid ? 1 : 10;

	private bool _applicationPaused;

	/// <summary>Pauses only Android retirement's active deadline when the application stops drawing in the background.</summary>
	/// <param name="what">Godot's application lifecycle notification.</param>
	public override void _Notification(int what)
	{
		if (!Refii.Core.Engine_Platform.IsAndroid) return;
		if (what != NotificationApplicationPaused && what != NotificationApplicationResumed) return;
		_applicationPaused = what == NotificationApplicationPaused;
		if (_retirement?.SetApplicationPaused(_applicationPaused, Time.GetTicksMsec() / 1000.0) == RegionRetirementWork.Failed)
			GD.PrintErr("[region-retirement] " + _retirement.Status.Error + "; next build remains blocked; relaunch the app to recover");
	}

	/// <summary>
	/// `android_loh_compaction` off (the default), or the runtime refused CompactOnce (Mono's
	/// GCSettings setter throws PlatformNotSupportedException): collects the unreferenced decode
	/// buffers without promising LOH compaction, and reports only the measured live bytes rather
	/// than desktop committed-heap semantics.
	/// </summary>
	/// <remarks>
	/// `android_loh_compaction` (default off): Android compacts too. The Mono runtime rejects
	/// CompactOnce, while the NativeAOT export takes CompactOnce. A runtime that refuses the
	/// setting takes the ordinary collection as before.
	/// </remarks>
	/// <param name="before">Live managed bytes before the collection.</param>
	/// <returns>True when the collection ran here and the caller must not compact.</returns>
	private static bool CollectWithoutCompaction(long before)
	{
		if (AndroidCompactsLargeObjects())
		{
			return false;
		}

		System.GC.Collect();
		GD.Print($"[game-world] collected region-load buffers without LOH compaction: " +
			$"{before / 1048576.0:0} MB -> {System.GC.GetTotalMemory(false) / 1048576.0:0} MB live");
		if (Refii.Level.LevelReads.LastCompleted is { } reads)
			GD.Print($"[level-reads] release boundary; completed-build snapshot (not live bytes): {reads}");
		return true;
	}

	/// <summary>`android_loh_compaction`, and whether this runtime takes CompactOnce at all.</summary>
	private static bool AndroidCompactsLargeObjects()
	{
		bool wanted;
		try
		{
			wanted = Refii.Core.Engine_RefiiConfig.Bool(
				Refii.Core.Engine_RefiiConfig.Read(out _), "android_loh_compaction", false);
		}
		catch (System.Exception)
		{
			return false;
		}

		if (!wanted)
		{
			return false;
		}

		try
		{
			System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
				System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
			return true;
		}
		catch (System.PlatformNotSupportedException)
		{
			return false;
		}
	}

	/// <summary>Last Android retirement observations; these are diagnostics, not proof of complete reclamation.</summary>
	/// <param name="Gate">Ordering, deadline and producer observation.</param>
	/// <param name="ManagedBeforeBytes">Managed live-byte estimate before reference retirement.</param>
	/// <param name="ManagedAfterBytes">Latest estimate after a pass or completion.</param>
	/// <param name="TextureBeforeBytes">Renderer-reported texture bytes before retirement.</param>
	/// <param name="TextureAfterBytes">Latest renderer-reported texture bytes.</param>
	/// <param name="BufferBeforeBytes">Renderer-reported buffer bytes before retirement.</param>
	/// <param name="BufferAfterBytes">Latest renderer-reported buffer bytes.</param>
	public readonly record struct RegionRetirementDiagnostics(GameRegionRetirement.Snapshot Gate,
		long ManagedBeforeBytes, long ManagedAfterBytes, long TextureBeforeBytes, long TextureAfterBytes,
		long BufferBeforeBytes, long BufferAfterBytes);

	/// <summary>The latest value-only Android gate observation, preserving a completed gate until the next transition.</summary>
	public RegionRetirementDiagnostics? RetirementStatus => _retirement is null ? null : new(
		_retirement.Status, _retirementManagedBefore, _retirementManagedAfter,
		_retirementTextureBefore, _retirementTextureAfter, _retirementBufferBefore, _retirementBufferAfter);

	private bool _buildWasHeld;
	private GameRegionRetirement? _retirement;
	private System.Threading.Tasks.Task? _retiringBuild;
	private System.Threading.Tasks.Task? _retirementCollection;
	private long _retirementManagedBefore, _retirementManagedAfter;
	private long _retirementTextureBefore, _retirementTextureAfter;
	private long _retirementBufferBefore, _retirementBufferAfter;

	/// <summary>Whether a retirement still holds the next build back.</summary>
	private bool RetirementBlocksBuild => _retirement?.Status.NewBuildBlocked == true;

	/// <summary>
	/// Starts the retirement of the standing region, at the top of <see cref="TearDownRegion"/>:
	/// before the nodes go, so the before-counters are the region's, and before the in-flight build
	/// is forgotten, so the pump can wait for its worker.
	/// </summary>
	private void BeginRegionRetirement()
	{
		_retirement = new GameRegionRetirement(unchecked((uint)Engine.GetFramesDrawn()), Time.GetTicksMsec() / 1000.0, _applicationPaused);
		_retiringBuild = _build;
		ObserveRetirementMemory();
		_retirementManagedBefore = _retirementManagedAfter;
		_retirementTextureBefore = _retirementTextureAfter;
		_retirementBufferBefore = _retirementBufferAfter;
		SetWorldBuildHeld(true);
		GD.Print($"[region-retirement] started: managed {_retirementManagedBefore} B, textures {_retirementTextureBefore} B, " +
			$"buffers {_retirementBufferBefore} B; {_standing}");
	}

	/// <summary>
	/// The guest has left the game. A completed boundary can have kept the destination fence while
	/// assets were unavailable. Its later title edge must release it even with no mounted region.
	/// </summary>
	private void ReleaseRetiredFence()
	{
		if (_retirement is { } gate && !gate.Status.NewBuildBlocked)
			SetWorldBuildHeld(false);
	}

	/// <summary>Holds native preload while the old region and its producers retire, without blocking the Godot loop on finalizers.</summary>
	private bool PumpRegionRetirement()
	{
		if (_retirement is null || !_retirement.Status.NewBuildBlocked)
		{
			if (Refii.Core.Engine_Platform.IsAndroid)
			{
				AndroidMemoryRelease.PumpLoad(WorldBuildHeld);
				// A hold that gave up can still have bakes reading this metadata on their workers, so it keeps the copies.
				if (_buildWasHeld && !WorldBuildHeld && _region is not null && _propHoldFrames < PropHoldFrameLimit) AndroidMemoryRelease.ForgetMeshCopies(_region);
				_buildWasHeld = WorldBuildHeld;
			}
			return true;
		}
		ulong frame = unchecked((uint)Engine.GetFramesDrawn());
		int workers = Refii.Core.Engine_RegionWorkers.GlobalActive;
		if (_retiringBuild is { IsCompleted: false }) ++workers;
		else
		{
			_retiringBuild = null;
			if (_builtRegion is { } abandoned)
			{
				_builtRegion = null;
				abandoned.QueueFree();
				_retirement.RequireDeletionFrame(frame);
				GD.Print("[region-retirement] queued the abandoned worker's detached region for deletion");
			}
			_buildError = string.Empty;
		}
		bool finished = _retirementCollection is { IsCompleted: true };
		string error = string.Empty;
		if (finished)
		{
			try { _retirementCollection!.GetAwaiter().GetResult(); }
			catch (System.Exception exception) { error = $"collection failed: {exception.GetType().Name}: {exception.Message}"; }
			_retirementCollection = null;
			ObserveRetirementMemory();
			GD.Print($"[region-retirement] pass {_retirement.Status.FinalizerPasses + 1}: managed {_retirementManagedAfter} B, " +
				$"textures {_retirementTextureAfter} B, buffers {_retirementBufferAfter} B; renderer counters, not OS residency");
		}
		RegionRetirementWork work = _retirement.Advance(frame, Time.GetTicksMsec() / 1000.0, workers, finished, error);
		if (work is RegionRetirementWork.ClearAndCollect or RegionRetirementWork.Collect)
		{
			try
			{
				if (work == RegionRetirementWork.ClearAndCollect) Refii.Core.Engine_LevelCaches.ReleaseRegion();
				// The last pass compacts: the post-load compaction would otherwise come only after
				// the next build's peak, on the gaps this region leaves.
				bool compact = _retirement.NextCollectionIsLast && AndroidMemoryRelease.Wanted() && AndroidCompactsLargeObjects();
				// Waiting on the finalizer thread from _Process can block engine progress needed by
				// native destructors. A separate task permits rendered frames while it finishes.
				_retirementCollection = System.Threading.Tasks.Task.Run(() =>
				{
					if (compact)
					{
						Compact(System.GC.GetTotalMemory(false));
						return;
					}
					System.GC.Collect();
					System.GC.WaitForPendingFinalizers();
				});
			}
			catch (System.Exception exception)
			{
				_retirementCollection = System.Threading.Tasks.Task.FromException(exception);
			}
		}
		else if (work == RegionRetirementWork.Complete)
		{
			ObserveRetirementMemory();
			GD.Print($"[region-retirement] completed in {_retirement.Status.ElapsedSeconds:0.00}s after {_retirement.Status.FinalizerPasses} pass(es): " +
				$"managed {_retirementManagedBefore}->{_retirementManagedAfter} B, textures {_retirementTextureBefore}->{_retirementTextureAfter} B, " +
				$"buffers {_retirementBufferBefore}->{_retirementBufferAfter} B; unreferenced retirement measured, allocation release not guaranteed");
			bool inGame = HostBridge.IsInGame;
			HostBridge.LevelChange? change = inGame ? HostBridge.GetLevelChange() : null;
			HostBridge.CurrentLevel? current = inGame ? HostBridge.GetCurrentLevel() : null;
			uint? currentKey = current is { } destination && destination.Level.Length != 0 ? destination.Key : null;
			uint? fullLoadState = change is { FullLoad: true } command ? command.State : null;
			bool preserve = GameRegionRetirement.PreservePreloadHold(inGame, currentKey, _torndownKey, fullLoadState);
			SetWorldBuildHeld(preserve);
			GD.Print(preserve ? "[region-retirement] destination exists; preload hold transfers to its build" :
				"[region-retirement] released preload hold so the guest can advance to a destination or title");
			return true;
		}
		else if (work == RegionRetirementWork.Failed)
		{
			GD.PrintErr("[region-retirement] " + _retirement.Status.Error + "; next build remains blocked; relaunch the app to recover");
		}
		return false;
	}

	/// <summary>Captures main-thread scalar counters without retaining native resources or reading back pixels.</summary>
	private void ObserveRetirementMemory()
	{
		_retirementManagedAfter = System.GC.GetTotalMemory(false);
		_retirementTextureAfter = (long)Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed);
		_retirementBufferAfter = (long)Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed);
	}
}

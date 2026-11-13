using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Refii.Ui;

/// <summary>
/// Incremental, frame-budgeted prebuilder for front-end UI presentations (`scrollingbackground` and
/// `frontendmainmenu`). Designed to start immediately after shader warmup finishes while startup
/// `.bik` logo videos play, spreading document parsing, individual texture decode/GPU upload,
/// pattern-book instantiation, and owned-book construction across separate frames so D3D12 never
/// receives a single multi-hundred-megabyte synchronous burst and video playback stays smooth.
/// </summary>
public sealed class Engine_UiFrontEndPrebuild
{
	public enum Stage
	{
		Idle = 0,
		LoadBackgroundDocument = 1,
		LoadMainMenuDocument = 2,
		WarmTextures = 3,
		BuildBackgroundPatternBook = 4,
		BuildMainMenuPatternBook = 5,
		PrebuildMainMenuOwnedBook = 6,
		Complete = 7,
	}

	/// <summary>
	/// Target wall-time budget per frame for draining texture preloads during startup videos
	/// (~1.5 ms keeps 60 Hz+ video frames completely stutter-free while draining cached hits fast).
	/// </summary>
	public const double DefaultTextureBudgetMs = 1.5;

	/// <summary>Upper bound on cached/skipped texture probes scanned in one frame.</summary>
	public const int MaxTextureProbesPerFrame = 8;

	private readonly List<string> _textureQueue = new();
	private readonly HashSet<string> _seenTextures = new(StringComparer.OrdinalIgnoreCase);

	public Stage CurrentStage { get; private set; } = Stage.Idle;
	public int NextTextureIndex { get; private set; }
	public int TotalQueuedTextures => _textureQueue.Count;
	public int OwnedBookRetries { get; private set; }

	/// <summary>Resets the state machine (e.g., on returning to the title screen or exiting front end).</summary>
	public void Reset()
	{
		CurrentStage = Stage.Idle;
		_textureQueue.Clear();
		_seenTextures.Clear();
		NextTextureIndex = 0;
		OwnedBookRetries = 0;
	}

	/// <summary>
	/// Collects unique normalized texture paths referenced by <paramref name="screen"/> nodes.
	/// Pure managed logic safe for unit tests.
	/// </summary>
	public static void CollectUniqueTextures(
		Engine_UiScreen? screen,
		List<string> destination,
		HashSet<string> seen)
	{
		if (screen is null)
		{
			return;
		}

		UiNode[] nodes = screen.Nodes;
		for (int i = 0; i < nodes.Length; i++)
		{
			string? rawPath = nodes[i].Quad?.TexturePath;
			if (string.IsNullOrEmpty(rawPath))
			{
				continue;
			}

			string normalized = rawPath.Replace('\\', '/');
			if (seen.Add(normalized))
			{
				destination.Add(normalized);
			}
		}
	}

	/// <summary>
	/// Advances at most ONE spread-out prebuild unit for the current frame.
	/// Returns true when work executed this frame, or false if idle/complete.
	/// </summary>
	public bool Step(
		bool canRun,
		Func<string, Engine_UiScreen?> loadDocument,
		Func<string, bool> warmOneTexture,
		Action<string> buildPatternBook,
		Func<string, bool> prebuildOwnedBook,
		double textureBudgetMs = DefaultTextureBudgetMs,
		int maxRetries = 240)
	{
		if (!canRun || CurrentStage == Stage.Complete)
		{
			return false;
		}

		switch (CurrentStage)
		{
			case Stage.Idle:
				CurrentStage = Stage.LoadBackgroundDocument;
				goto case Stage.LoadBackgroundDocument;

			case Stage.LoadBackgroundDocument:
				CollectUniqueTextures(loadDocument("scrollingbackground"), _textureQueue, _seenTextures);
				CurrentStage = Stage.LoadMainMenuDocument;
				return true;

			case Stage.LoadMainMenuDocument:
				CollectUniqueTextures(loadDocument("frontendmainmenu"), _textureQueue, _seenTextures);
				CurrentStage = _textureQueue.Count > 0
					? Stage.WarmTextures
					: Stage.BuildBackgroundPatternBook;
				return true;

			case Stage.WarmTextures:
			{
				long startTicks = Stopwatch.GetTimestamp();
				double ticksToMs = 1000.0 / Stopwatch.Frequency;
				int probes = 0;
				while (NextTextureIndex < _textureQueue.Count && probes < MaxTextureProbesPerFrame)
				{
					string path = _textureQueue[NextTextureIndex++];
					bool didDecodeOrUpload = warmOneTexture(path);
					++probes;

					// Yield immediately after one actual uncached decode/upload so D3D12 never
					// batches multiple front-end textures into a single startup video frame.
					if (didDecodeOrUpload)
					{
						break;
					}

					if ((Stopwatch.GetTimestamp() - startTicks) * ticksToMs >= textureBudgetMs)
					{
						break;
					}
				}

				if (NextTextureIndex >= _textureQueue.Count)
				{
					CurrentStage = Stage.BuildBackgroundPatternBook;
				}
				return true;
			}

			case Stage.BuildBackgroundPatternBook:
				buildPatternBook("scrollingbackground");
				CurrentStage = Stage.BuildMainMenuPatternBook;
				return true;

			case Stage.BuildMainMenuPatternBook:
				buildPatternBook("frontendmainmenu");
				CurrentStage = Stage.PrebuildMainMenuOwnedBook;
				return true;

			case Stage.PrebuildMainMenuOwnedBook:
				if (prebuildOwnedBook("frontendmainmenu") || ++OwnedBookRetries > maxRetries)
				{
					CurrentStage = Stage.Complete;
				}
				return true;

			default:
				CurrentStage = Stage.Complete;
				return false;
		}
	}
}

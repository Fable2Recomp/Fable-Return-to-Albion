using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Refii.Assets;
using Refii.Core;
using Refii.Textures;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Unit and feature tests verifying D3D12/DXIL UI shader derivative invariants,
/// fast boot prebuild marker round-trips, and managed heap census coverage.
/// </summary>
[TestSuite]
public sealed class Dx12AndMemoryOptimizationTests
{
	private static string FindRepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(Path.Combine(dir, "src", "Engine", "ui")))
			{
				return dir;
			}
			string candidate = Path.Combine(dir, "Godot");
			if (Directory.Exists(Path.Combine(candidate, "src", "Engine", "ui")))
			{
				return candidate;
			}
			dir = Path.GetDirectoryName(dir);
		}
		throw new DirectoryNotFoundException("Could not locate Godot/src/Engine/ui from test base directory.");
	}

	/// <summary>
	/// Under D3D12 (SPIRV-Cross -> DXIL -> DXC), implicit-derivative calls (`texture(...)` and `fwidth(...)`)
	/// must execute prior to any non-uniform `discard` in `ui_text.gdshader` and `ui_quad.gdshader`.
	/// </summary>
	[TestCase]
	public void UiShadersSampleAllImplicitDerivativesBeforeAnyDiscard()
	{
		string godotRoot = FindRepoRoot();
		foreach (string shaderFile in new[] { "ui_text.gdshader", "ui_quad.gdshader" })
		{
			string fullPath = Path.Combine(godotRoot, "src", "Engine", "ui", shaderFile);
			AssertThat(File.Exists(fullPath)).IsTrue();
			string source = File.ReadAllText(fullPath);
			int fragIdx = source.IndexOf("void fragment()", StringComparison.Ordinal);
			AssertThat(fragIdx).IsGreaterEqual(0);
			string fragBody = source[fragIdx..];

			int firstDiscard = fragBody.IndexOf("discard;", StringComparison.Ordinal);
			AssertThat(firstDiscard).IsGreater(0);

			int lastTextureSample = Math.Max(
				fragBody.LastIndexOf("texture(", StringComparison.Ordinal),
				fragBody.LastIndexOf("textureLod(", StringComparison.Ordinal));
			AssertThat(lastTextureSample).IsGreater(0);
			AssertThat(lastTextureSample).IsLess(firstDiscard);

			int lastFwidth = fragBody.LastIndexOf("fwidth(", StringComparison.Ordinal);
			if (lastFwidth >= 0)
			{
				AssertThat(lastFwidth).IsLess(firstDiscard);
			}
		}
	}

	/// <summary>
	/// Verifies that `Engine_TexPrebuild.WriteFastMarker` and `TryReadFastMarker` round-trip
	/// when the fingerprint and mounted file count match, and invalidate when the mounted file count changes.
	/// </summary>
	[TestCase]
	public void FastPrebuildMarkerRoundTripsAndInvalidatesOnFileCountChange()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), "refii-test-marker-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempDir);
		string markerPath = Path.Combine(tempDir, "orm_prebuild.txt");
		try
		{
			Directory.CreateDirectory(Engine_TexCache.CacheRoot);
			Engine_TexPrebuild.WriteFastMarker(markerPath, 140141, built: 1949, total: 1949, failed: 0);

			bool hit = Engine_TexPrebuild.TryReadFastMarker(
				markerPath, 140141, out int built, out int total, out int failed);
			AssertThat(hit).IsTrue();
			AssertThat(built).IsEqual(1949);
			AssertThat(total).IsEqual(1949);
			AssertThat(failed).IsEqual(0);

			bool missOnDifferentCount = Engine_TexPrebuild.TryReadFastMarker(
				markerPath, 140142, out _, out _, out _);
			AssertThat(missOnDifferentCount).IsFalse();
		}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, recursive: true);
			}
		}
	}

	/// <summary>
	/// Verifies that the BNK filesystem and UI bitmap font cache census helpers
	/// execute safely without unmanaged Godot interop when unmounted.
	/// </summary>
	[TestCase]
	public void HeapCensusHelpersAreSafeWhenUnmounted()
	{
		(int files, int archives, long bytes) = Engine_GameAssets.GetHeapCensus();
		AssertThat(files).IsGreaterEqual(0);
		AssertThat(archives).IsGreaterEqual(0);
		AssertThat(bytes).IsGreaterEqual(0L);
		AssertThat(Engine_BitmapFontTexture.CachedFontCount).IsGreaterEqual(0);
		AssertThat(Engine_BitmapFontTexture.CachedFontBytes).IsGreaterEqual(0L);
	}

	/// <summary>
	/// Verifies that background worker calls to <see cref="Engine_ServerSyncs.RunGpuUpload{T}"/>
	/// never execute off the main thread (even after waiting) and complete cleanly on the main
	/// thread when <see cref="Engine_ServerSyncs.PumpWorkerGpuUploads"/> runs.
	/// </summary>
	[TestCase]
	public void WorkerGpuUploadsExecuteExclusivelyOnMainThreadAndRespectLoadCoverBudget()
	{
		Engine_ServerSyncs.ResetMainThreadForTest();
		try
		{
			Engine_ServerSyncs.MarkMainThread();
			int mainThreadId = Environment.CurrentManagedThreadId;
			Engine_ServerSyncs.BeginMainFrameUploadBudget();

			// Queue two 40 MB uploads from a worker thread: under normal play (48 MB cap), the first
			// pump drains 1 item (40 MB < 48 MB) and pauses before the second; with LoadCoverActive (96 MB cap),
			// the pump drains the second item immediately in the same frame.
			var executedThreads = new System.Collections.Concurrent.ConcurrentQueue<int>();
			var workerTask = System.Threading.Tasks.Task.Run(() =>
			{
				int a = Engine_ServerSyncs.RunGpuUpload(40L * 1024L * 1024L, () =>
				{
					executedThreads.Enqueue(Environment.CurrentManagedThreadId);
					return 11;
				});
				int b = Engine_ServerSyncs.RunGpuUpload(40L * 1024L * 1024L, () =>
				{
					executedThreads.Enqueue(Environment.CurrentManagedThreadId);
					return 31;
				});
				return a + b;
			});

			// Wait briefly for the worker to enqueue its first upload, then verify it has not self-executed.
			System.Threading.Thread.Sleep(30);
			AssertThat(workerTask.IsCompleted).IsFalse();

			Engine_ServerSyncs.PumpWorkerGpuUploads(10.0);
			System.Threading.Thread.Sleep(20);
			// Second 40 MB upload arrives; with LoadCoverActive == false, _frameUploadBytes is 40 MB (< 48 MB),
			// so a second pump drains it and brings total to 80 MB (>= 48 MB), capping further normal-play drains.
			Engine_ServerSyncs.LoadCoverActive = true;
			Engine_ServerSyncs.PumpWorkerGpuUploads(10.0);

			int sum = workerTask.GetAwaiter().GetResult();
			AssertThat(sum).IsEqual(42);
			AssertThat(executedThreads.Count).IsEqual(2);
			foreach (int tid in executedThreads)
			{
				AssertThat(tid).IsEqual(mainThreadId);
			}
		}
		finally
		{
			Engine_ServerSyncs.ResetMainThreadForTest();
		}
	}

	/// <summary>
	/// Guards against regressions that reintroduce C++ ResourceLoader mutex inversions on entity
	/// worker threads or lock-order inversions in Engine_ShadowProxy.MergedFor.
	/// </summary>
	[TestCase]
	public void EntityLoaderAndShadowProxyAvoidResourceLoaderAndLockOrderInversions()
	{
		string godotRoot = FindRepoRoot();

		string gameEntitiesSource = File.ReadAllText(Path.Combine(godotRoot, "src", "game", "GameEntities.cs"));
		AssertThat(gameEntitiesSource.Contains("ResourceLoader.Load<Mesh>(", StringComparison.Ordinal)).IsFalse();

		string shadowProxySource = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "entities", "Engine_ShadowProxy.cs"));
		int mergedForIdx = shadowProxySource.IndexOf("private static ArrayMesh? MergedFor(", StringComparison.Ordinal);
		int takeMergeIdx = shadowProxySource.IndexOf("public static (double WaitMs, double BuildMs) TakeMergeTimes()", StringComparison.Ordinal);
		AssertThat(mergedForIdx).IsGreater(0);
		AssertThat(takeMergeIdx).IsGreater(mergedForIdx);
		string mergedForBody = shadowProxySource[mergedForIdx..takeMergeIdx];
		int buildCallIdx = mergedForBody.IndexOf("Build(source, body, hidden, skinned, always)", StringComparison.Ordinal);
		int firstLockIdx = mergedForBody.IndexOf("lock (perMask)", StringComparison.Ordinal);
		int secondLockIdx = mergedForBody.LastIndexOf("lock (perMask)", StringComparison.Ordinal);
		AssertThat(firstLockIdx).IsGreater(0);
		AssertThat(buildCallIdx).IsGreater(firstLockIdx);
		AssertThat(secondLockIdx).IsGreater(buildCallIdx);
	}

	/// <summary>
	/// Guards against the Intel Arc D3D12 title-screen GPU TDR regression (`0x887a0005`):
	/// UI text and world-marker shaders must declare explicit `hint_default_*` sampler fallbacks,
	/// gate `UV2` and `ui_text_outline_cover` behind uniform booleans for Fable 2 meshes without UV2,
	/// and keep `ui_text_outline.gdshaderinc` free of dual-sampler branches or `textureSize` inside loops.
	/// </summary>
	[TestCase]
	public void UiTextAndOutlineShadersMaintainD3D12SamplerAndUniformInvariants()
	{
		string godotRoot = FindRepoRoot();
		string uiDir = Path.Combine(godotRoot, "src", "Engine", "ui");

		string uiText = File.ReadAllText(Path.Combine(uiDir, "ui_text.gdshader"));
		AssertThat(uiText.Contains("uniform sampler2D atlas : source_color, filter_linear_mipmap, repeat_disable, hint_default_transparent;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("uniform sampler2D distance_field : filter_linear_mipmap, repeat_disable, hint_default_black;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("uniform bool has_uv2_modes = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("uniform bool outline = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("if (has_uv2_modes)", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("if (outline && outlined)", StringComparison.Ordinal)).IsTrue();

		string uiWorldMarker = File.ReadAllText(Path.Combine(uiDir, "ui_world_marker.gdshader"));
		AssertThat(uiWorldMarker.Contains("uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_disable, hint_default_transparent;", StringComparison.Ordinal)).IsTrue();

		string uiOutlineInc = File.ReadAllText(Path.Combine(uiDir, "ui_text_outline.gdshaderinc"));
		AssertThat(uiOutlineInc.Contains("textureSize(", StringComparison.Ordinal)).IsFalse();
		int fromFieldIdx = uiOutlineInc.IndexOf("if (from_field)", StringComparison.Ordinal);
		int firstForIdx = uiOutlineInc.IndexOf("for (int y = -1;", StringComparison.Ordinal);
		AssertThat(fromFieldIdx).IsGreater(0);
		AssertThat(firstForIdx).IsGreater(fromFieldIdx);

		// Verify project.godot's configured application icon exists so startup never logs a missing icon error.
		string projectGodot = File.ReadAllText(Path.Combine(godotRoot, "project.godot"));
		AssertThat(projectGodot.Contains("config/icon=\"res://assets/launcher/fable.png\"", StringComparison.Ordinal)).IsTrue();
		AssertThat(File.Exists(Path.Combine(godotRoot, "assets", "launcher", "fable.png"))).IsTrue();
	}
}

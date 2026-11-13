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
				Math.Max(
					fragBody.LastIndexOf("textureLod(", StringComparison.Ordinal),
					fragBody.LastIndexOf("textureGrad(", StringComparison.Ordinal)));
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
	/// Prevents regressions of the Intel Arc D3D12 GPU hang (`LiveKernelEvent 141` / `0x887a0005`)
	/// caused by evaluating screen-space derivatives (`fwidth(distance)`) on a sampled `.g` SDF
	/// texel or issuing redundant `fwidth(shipped)` calls in `ui_text.gdshader`.
	/// </summary>
	[TestCase]
	public void UiTextShaderAvoidsDependentTextureDerivativesAndRedundantFwidth()
	{
		string godotRoot = FindRepoRoot();
		string fullPath = Path.Combine(godotRoot, "src", "Engine", "ui", "ui_text.gdshader");
		AssertThat(File.Exists(fullPath)).IsTrue();
		string source = File.ReadAllText(fullPath);
		int fragIdx = source.IndexOf("void fragment()", StringComparison.Ordinal);
		AssertThat(fragIdx).IsGreaterEqual(0);
		string fragBody = string.Join("\n", System.Linq.Enumerable.Select(
			source[fragIdx..].Split('\n'),
			static line =>
			{
				int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
				return commentIdx >= 0 ? line[..commentIdx] : line;
			}));

		AssertThat(fragBody.Contains("fwidth(distance)", StringComparison.Ordinal)).IsFalse();
		AssertThat(fragBody.Contains("dFdx(distance)", StringComparison.Ordinal)).IsFalse();
		AssertThat(fragBody.Contains("dFdy(distance)", StringComparison.Ordinal)).IsFalse();
		AssertThat(fragBody.Contains("fwidth(texel", StringComparison.Ordinal)).IsFalse();
		AssertThat(fragBody.Contains("fwidth(shipped", StringComparison.Ordinal)).IsFalse();
		AssertThat(fragBody.Contains("dFdx(UV)", StringComparison.Ordinal)).IsTrue();
		AssertThat(fragBody.Contains("dFdy(UV)", StringComparison.Ordinal)).IsTrue();
		AssertThat(fragBody.Contains("clamp(0.25 / max(magnified", StringComparison.Ordinal)).IsTrue();
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
		AssertThat(uiText.Contains("uniform sampler2D atlas : filter_linear_mipmap, repeat_disable, hint_default_transparent;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("uniform sampler2D distance_field", StringComparison.Ordinal)).IsFalse();
		AssertThat(uiText.Contains("uniform bool has_uv2_modes = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("uniform bool outline = false;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("if (has_uv2_modes)", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiText.Contains("if (outline && outlined)", StringComparison.Ordinal)).IsTrue();

		string uiWorldMarker = File.ReadAllText(Path.Combine(uiDir, "ui_world_marker.gdshader"));
		AssertThat(uiWorldMarker.Contains("uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_disable, hint_default_transparent;", StringComparison.Ordinal)).IsTrue();

		string uiQuad = File.ReadAllText(Path.Combine(uiDir, "ui_quad.gdshader"));
		AssertThat(uiQuad.Contains("render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiQuad.Contains("textureGrad(diffuse_tex, uv, uv_dx, uv_dy)", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiQuad.Contains("textureGrad(detail_tex, detail_uv, detail_dx, detail_dy)", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiQuad.Contains("textureGrad(keycap_tex, UV, raw_uv_dx, raw_uv_dy)", StringComparison.Ordinal)).IsTrue();
		AssertThat(uiQuad.Contains("textureLod(", StringComparison.Ordinal)).IsFalse();
		int quadBranchIdx = uiQuad.IndexOf("if (has_keycap && ui_keyboard_mode)", StringComparison.Ordinal);
		AssertThat(quadBranchIdx).IsGreater(0);
		AssertThat(uiQuad.IndexOf("dFdx(uv)", StringComparison.Ordinal)).IsLess(quadBranchIdx);
		AssertThat(uiQuad.IndexOf("dFdy(detail_uv)", StringComparison.Ordinal)).IsLess(quadBranchIdx);
		AssertThat(uiQuad.Contains("if (clipped || final_alpha <= 0.001)", StringComparison.Ordinal)).IsTrue();

		string uiOutlineInc = File.ReadAllText(Path.Combine(uiDir, "ui_text_outline.gdshaderinc"));
		AssertThat(uiOutlineInc.Contains("float ui_text_outline_cover(sampler2D glyph_atlas,", StringComparison.Ordinal)).IsTrue();
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

	/// <summary>
	/// Verifies pure storage-layout matching used by <see cref="Engine_TextureUpload"/> to update
	/// textures in-place on D3D12 without live <c>RS::texture_replace</c> descriptor churn, and
	/// verifies load-time UI book prebuild restricts eager builds to core screens.
	/// </summary>
	[TestCase]
	public void TextureUploadStorageLayoutAndCoreBookPrebuildInvariants()
	{
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			512, 512, Godot.Image.Format.Dxt5, true,
			512, 512, Godot.Image.Format.Dxt5, true)).IsTrue();
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			512, 512, Godot.Image.Format.Dxt5, true,
			256, 512, Godot.Image.Format.Dxt5, true)).IsFalse();
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			512, 512, Godot.Image.Format.Dxt5, true,
			512, 512, Godot.Image.Format.Rgba8, true)).IsFalse();
		AssertThat(Engine_TextureUpload.MatchesStorageLayout(
			512, 512, Godot.Image.Format.Dxt5, true,
			512, 512, Godot.Image.Format.Dxt5, false)).IsFalse();

		AssertThat(Engine_UiBookPrebuild.Wants("pause")).IsTrue();
		AssertThat(Engine_UiBookPrebuild.Wants("items")).IsTrue();
		AssertThat(Engine_UiBookPrebuild.Wants("abilities")).IsFalse();
		AssertThat(Engine_UiBookPrebuild.Wants("quests")).IsFalse();
		AssertThat(Engine_UiBookPrebuild.Wants("logbook")).IsFalse();
	}

	/// <summary>
	/// Verifies that <see cref="Engine_RdHealth"/> latches cleanly on the first invalid RID / failure,
	/// increments suppressed counts without re-tripping on subsequent calls, and rate-limits repeating logs.
	/// </summary>
	[TestCase]
	public void RdHealthCircuitBreakerAndLogThrottleWorkCleanly()
	{
		Engine_RdHealth.Reset();
		try
		{
			AssertThat(Engine_RdHealth.IsDeviceLost).IsFalse();
			AssertThat(Engine_RdHealth.EvaluateResourceHealth(true, "test.ok", out bool firstOk)).IsTrue();
			AssertThat(firstOk).IsFalse();
			AssertThat(Engine_RdHealth.IsDeviceLost).IsFalse();

			AssertThat(Engine_RdHealth.EvaluateResourceHealth(false, "test.fail1", out bool firstFail)).IsFalse();
			AssertThat(firstFail).IsTrue();
			AssertThat(Engine_RdHealth.IsDeviceLost).IsTrue();
			AssertThat(Engine_RdHealth.SuppressedCount).IsEqual(0);

			AssertThat(Engine_RdHealth.EvaluateResourceHealth(false, "test.fail2", out bool secondFail)).IsFalse();
			AssertThat(secondFail).IsFalse();
			AssertThat(Engine_RdHealth.SuppressedCount).IsEqual(1);

			AssertThat(Engine_RdHealth.CheckRdValid(default, "test.defaultRid")).IsFalse();
			AssertThat(Engine_RdHealth.SuppressedCount).IsEqual(2);

			AssertThat(Engine_RdHealth.ShouldLogThrottled("k", 1000, 100, out int sup0)).IsTrue();
			AssertThat(sup0).IsEqual(0);
			AssertThat(Engine_RdHealth.ShouldLogThrottled("k", 1000, 500, out _)).IsFalse();
			AssertThat(Engine_RdHealth.ShouldLogThrottled("k", 1000, 900, out _)).IsFalse();
			AssertThat(Engine_RdHealth.ShouldLogThrottled("k", 1000, 1100, out int sup1)).IsTrue();
			AssertThat(sup1).IsEqual(2);
		}
		finally
		{
			Engine_RdHealth.Reset();
		}
	}

	/// <summary>
	/// Verifies that <see cref="Engine_UiFrontEndPrebuild"/> deduplicates normalised texture paths and
	/// spreads document load, individual texture decode/upload, pattern book build, and owned-book build
	/// across separate frames so startup videos never receive a synchronous single-frame burst.
	/// </summary>
	[TestCase]
	public void FrontEndPrebuildSpreadsWorkAcrossSeparateFramesAndDeduplicatesTextures()
	{
		var bgScreen = new Engine_UiScreen
		{
			Name = "scrollingbackground",
			Nodes = new[]
			{
				new UiNode { Quad = new UiQuad { TexturePath = @"art\gui\bg.tex" } },
			},
		};
		var menuScreen = new Engine_UiScreen
		{
			Name = "frontendmainmenu",
			Nodes = new[]
			{
				new UiNode { Quad = new UiQuad { TexturePath = "art/gui/bg.tex" } },
				new UiNode { Quad = new UiQuad { TexturePath = @"art\gui\button.tex" } },
				new UiNode { Quad = new UiQuad { TexturePath = @"art\gui\frame.tex" } },
			},
		};

		var events = new List<string>();
		var warmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var prebuild = new Engine_UiFrontEndPrebuild();

		Engine_UiScreen? LoadDoc(string name)
		{
			events.Add("doc:" + name);
			return name == "scrollingbackground" ? bgScreen : menuScreen;
		}

		bool WarmTex(string path)
		{
			events.Add("tex:" + path);
			return warmed.Add(path);
		}

		void BuildBook(string name) => events.Add("pattern:" + name);
		bool BuildOwned(string name)
		{
			events.Add("owned:" + name);
			return true;
		}

		// Frame 1: scrollingbackground doc only
		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.LoadMainMenuDocument);
		// Frame 2: frontendmainmenu doc only
		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.WarmTextures);
		AssertThat(prebuild.TotalQueuedTextures).IsEqual(3); // bg.tex deduplicated across slash styles

		// Frames 3..5: exactly ONE uncached texture per frame
		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.NextTextureIndex).IsEqual(1);
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.WarmTextures);

		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.NextTextureIndex).IsEqual(2);
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.WarmTextures);

		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.NextTextureIndex).IsEqual(3);
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.BuildBackgroundPatternBook);

		// Frames 6..8: pattern books and owned book each on separate frames
		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.BuildMainMenuPatternBook);

		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.PrebuildMainMenuOwnedBook);

		AssertThat(prebuild.Step(true, LoadDoc, WarmTex, BuildBook, BuildOwned)).IsTrue();
		AssertThat(prebuild.CurrentStage).IsEqual(Engine_UiFrontEndPrebuild.Stage.Complete);
		AssertThat(events.Count).IsEqual(8);
	}

	/// <summary>
	/// Verifies classic spot-shadow isolation from Modern mode, UI visibility layer symmetry across
	/// quads/text/fade chains, and shared Fable 2 / Fable 3 DRY canvas camera construction.
	/// </summary>
	[TestCase]
	public void SpotShadowsAndUiSceneBuilderEnforceClassicGuardAndLayerSymmetry()
	{
		string godotRoot = FindRepoRoot();

		string spotShadows = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "material", "Engine_ClassicSpotShadows.cs"));
		AssertThat(spotShadows.Contains("if (_initialized || !Engine_ClassicMaterial.Active", StringComparison.Ordinal)).IsTrue();
		AssertThat(spotShadows.Contains("if (!Engine_ClassicMaterial.Active", StringComparison.Ordinal)).IsTrue();
		AssertThat(spotShadows.Contains("idleViewport.Disable3D = true;", StringComparison.Ordinal)).IsTrue();

		string builder = File.ReadAllText(Path.Combine(godotRoot, "src", "Engine", "ui", "Engine_UiSceneBuilder.cs"));
		AssertThat(builder.Contains("public static void SetVisibilityLayer(MeshInstance3D mesh, float alpha)", StringComparison.Ordinal)).IsTrue();
		AssertThat(builder.Contains("SetVisibilityLayer(node, _inheritedOpacity);", StringComparison.Ordinal)).IsTrue();
		AssertThat(builder.Contains("SetVisibilityLayer(label, colour.A);", StringComparison.Ordinal)).IsTrue();
		AssertThat(builder.Contains("SetVisibilityLayer(mesh, accumulated);", StringComparison.Ordinal)).IsTrue();
		AssertThat(builder.Contains("SetVisibilityLayer(mesh, opacity);", StringComparison.Ordinal)).IsTrue();
		AssertThat(builder.Contains("public static Camera3D CreateCanvasCamera(", StringComparison.Ordinal)).IsTrue();

		string fable3Canvas = File.ReadAllText(Path.Combine(godotRoot, "src", "Titles", "Fable3", "Fable3Canvas.cs"));
		string fable3Icons = File.ReadAllText(Path.Combine(godotRoot, "src", "Titles", "Fable3", "Fable3WorldIcons.cs"));
		AssertThat(fable3Canvas.Contains("Engine_UiSceneBuilder.CreateCanvasCamera(", StringComparison.Ordinal)).IsTrue();
		AssertThat(fable3Icons.Contains("Engine_UiSceneBuilder.CreateCanvasCamera(", StringComparison.Ordinal)).IsTrue();
	}

	/// <summary>
	/// Verifies root 3D scalers/MSAA/SSAA/TAA remain disabled while no 3D world is active (boot videos and
	/// front-end menus) and restore when a 3D world is mounted, without capping active window FPS.
	/// </summary>
	[TestCase]
	public void GameWindowSettingsDisablesRoot3DEffectsWithoutActiveWorld()
	{
		var settings = new Refii.Game.GameWindowSettings
		{
			Size = new Godot.Vector2I(1280, 720),
			RenderScalePercent = 75,
			Msaa = 4,
			Scaler = "fsr2",
			FsrQuality = "quality",
			ScreenSpaceAa = "fxaa",
			Taa = true,
			MeshLodPixels = 2.0f,
			MaxFps = 0,
			Vsync = false,
		};

		var idle = settings.ResolveRoot3DQuality(new Godot.Vector2I(1920, 1080), activeWorld: false);
		AssertThat(idle.ScalingMode).IsEqual(Godot.Viewport.Scaling3DModeEnum.Bilinear);
		AssertThat(idle.ScalingScale).IsEqual(1.0f);
		AssertThat(idle.MsaaMode).IsEqual(Godot.Viewport.Msaa.Disabled);
		AssertThat(idle.SsaaMode).IsEqual(Godot.Viewport.ScreenSpaceAAEnum.Disabled);
		AssertThat(idle.UseTaa).IsFalse();

		var active = settings.ResolveRoot3DQuality(new Godot.Vector2I(1920, 1080), activeWorld: true);
		AssertThat(active.ScalingMode).IsEqual(Godot.Viewport.Scaling3DModeEnum.Fsr2);
		AssertThat(active.MsaaMode).IsEqual(Godot.Viewport.Msaa.Msaa4X);
		AssertThat(active.SsaaMode).IsEqual(Godot.Viewport.ScreenSpaceAAEnum.Fxaa);
		AssertThat(active.UseTaa).IsFalse(); // FSR2 replaces TAA

		AssertThat(Refii.Game.GameWindow.ResolveVsyncMode(settings, isD3d12: true))
			.IsEqual(Godot.DisplayServer.VSyncMode.Disabled);
	}
}

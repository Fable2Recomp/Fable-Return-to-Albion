using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GdUnit4;
using Refii.Animations;
using Refii.Core;
using Refii.Game;
using Refii.Lighting;
using Refii.Level;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public sealed class LoadStabilityAndVramTests
{
	private sealed class MockPoseRig : Engine_ClipAnimator.IPoseBatchItem
	{
		private readonly List<uint>? _writeLog;
		private readonly int _spinIterations;

		public MockPoseRig(uint address, bool foldOnMain = false, List<uint>? writeLog = null, int spinIterations = 0)
		{
			Address = address;
			FoldOnMain = foldOnMain;
			_writeLog = writeLog;
			_spinIterations = spinIterations;
		}

		public bool FoldOnMain { get; }
		public uint Address { get; }
		public bool InBatch { get; set; }
		public Exception? FoldError { get; set; }
		public int FoldCount;
		public int WriteCount;

		public void Fold()
		{
			if (_spinIterations > 0)
			{
				Thread.SpinWait(_spinIterations);
			}

			Interlocked.Increment(ref FoldCount);
		}

		public void Write()
		{
			Interlocked.Increment(ref WriteCount);
			_writeLog?.Add(Address);
		}
	}

	private static string GodotRoot([CallerFilePath] string source = "") =>
		Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));

	[TestCase]
	public void MainThreadGpuUpload_CountsAgainstFrameBudgetAndPacesWorkers()
	{
		const long mb = 1024L * 1024L;
		using var pacer = new Engine_ServerSyncs.GpuUploadPacer();
		pacer.MarkMainThread();
		pacer.LoadCoverActive = true;
		pacer.BeginMainFrameUploadBudget();

		// 70 MB main-thread upload counts directly against the 96 MB load-cover frame cap.
		pacer.RunGpuUpload(70L * mb, static () => { });
		AssertThat(pacer.FrameUploadBytes).IsEqual(70L * mb);

		int executedWorkers = 0;
		Task worker1 = Task.Run(() => pacer.RunGpuUpload(30L * mb, () => Interlocked.Increment(ref executedWorkers)));
		Task worker2 = Task.Run(() => pacer.RunGpuUpload(30L * mb, () => Interlocked.Increment(ref executedWorkers)));

		AssertThat(SpinWait.SpinUntil(() => pacer.PendingWorkerUploads == 2, 2000)).IsTrue();

		// First pump executes one 30 MB worker upload (70 + 30 = 100 MB >= 96 MB cap) and defers the second.
		pacer.PumpWorkerGpuUploads(50.0);
		AssertThat(Volatile.Read(ref executedWorkers)).IsEqual(1);
		AssertThat(pacer.FrameUploadBytes).IsEqual(100L * mb);
		AssertThat(pacer.PendingWorkerUploads).IsEqual(1);

		// Next frame resets the staging counter and drains the remaining worker upload.
		pacer.BeginMainFrameUploadBudget();
		AssertThat(pacer.FrameUploadBytes).IsEqual(0L);
		pacer.PumpWorkerGpuUploads(50.0);

		AssertThat(Task.WaitAll(new[] { worker1, worker2 }, TimeSpan.FromSeconds(2))).IsTrue();
		AssertThat(Volatile.Read(ref executedWorkers)).IsEqual(2);
	}

	[TestCase]
	public void PoseBatch_StarvedThreadPool_MainThreadStealsAllRigsWithoutDeadlock()
	{
		var writeOrder = new List<uint>();
		var batch = new Engine_ClipAnimator.PoseBatch
		{
			DispatchToThreadPool = false,
		};

		var rigs = new List<MockPoseRig>();
		for (uint i = 1; i <= 16; i++)
		{
			var rig = new MockPoseRig(i, foldOnMain: (i % 5) == 0, writeLog: writeOrder);
			rigs.Add(rig);
			batch.AddItem(rig);
		}

		batch.Finish();

		// 3 rigs (5, 10, 15) folded as FoldOnMain; the remaining 13 were stolen on the main thread.
		AssertThat(batch.LastStolenCount).IsEqual(13);
		AssertThat(writeOrder.Count).IsEqual(16);
		for (int i = 0; i < rigs.Count; i++)
		{
			AssertThat(rigs[i].FoldCount).IsEqual(1);
			AssertThat(rigs[i].WriteCount).IsEqual(1);
			AssertThat(rigs[i].InBatch).IsFalse();
			AssertThat(writeOrder[i]).IsEqual((uint)(i + 1));
		}
	}

	[TestCase]
	public void PoseBatch_ConcurrentThreadPoolAndStealing_EachRigFoldsOnceAcrossEpochs()
	{
		var batch = new Engine_ClipAnimator.PoseBatch();

		for (int epoch = 0; epoch < 5; epoch++)
		{
			var writeOrder = new List<uint>(24);
			var rigs = new MockPoseRig[24];
			for (int i = 0; i < rigs.Length; i++)
			{
				rigs[i] = new MockPoseRig((uint)(i + 100), foldOnMain: (i % 7) == 0, writeLog: writeOrder, spinIterations: 200);
				batch.AddItem(rigs[i]);
			}

			batch.Finish();

			AssertThat(writeOrder.Count).IsEqual(rigs.Length);
			for (int i = 0; i < rigs.Length; i++)
			{
				AssertThat(rigs[i].FoldCount).IsEqual(1);
				AssertThat(rigs[i].WriteCount).IsEqual(1);
				AssertThat(rigs[i].InBatch).IsFalse();
				AssertThat(writeOrder[i]).IsEqual((uint)(i + 100));
			}
		}
	}

	[TestCase]
	public void SpecularSemanticPolicy_CapsEnhancementAt512WhilePreservingAlbedo1024()
	{
		TexPolicy specular = Engine_TexSemantic.For(TexSemantic.Specular);
		TexPolicy albedo = Engine_TexSemantic.For(TexSemantic.Albedo);
		TexPolicy normal = Engine_TexSemantic.For(TexSemantic.Normal);

		AssertThat(specular.MaxEdge).IsEqual(512);
		AssertThat(albedo.MaxEdge).IsEqual(1024);
		AssertThat(normal.MaxEdge).IsEqual(1024);

		// A 512px specular map stays 512px (1x), whereas 512px albedo/normal maps double to 1024px (2x).
		AssertThat(Engine_TexSemantic.TargetScale(512, 512, specular)).IsEqual(1);
		AssertThat(Engine_TexSemantic.TargetScale(256, 256, specular)).IsEqual(2);
		AssertThat(Engine_TexSemantic.TargetScale(512, 512, albedo)).IsEqual(2);
		AssertThat(Engine_TexSemantic.TargetScale(512, 512, normal)).IsEqual(2);
	}

	[TestCase]
	public void VistaPageDirectBlockUpload_AcceptsAlignedBc1AndRejectsUnalignedOrTruncated()
	{
		var validBc1 = new EhfAtlasImage
		{
			Width = 256,
			Height = 256,
			Format = TexSurfaceFormat.Bc1,
			Blocks = new byte[(256 / 4) * (256 / 4) * 8],
		};

		AssertThat(Engine_EhfPages.CanUploadPageBlocksDirectly(validBc1, out int bytes)).IsTrue();
		AssertThat(bytes).IsEqual(32768);

		var unaligned = new EhfAtlasImage
		{
			Width = 250,
			Height = 256,
			Format = TexSurfaceFormat.Bc1,
			Blocks = new byte[32768],
		};
		AssertThat(Engine_EhfPages.CanUploadPageBlocksDirectly(unaligned, out _)).IsFalse();

		var truncated = new EhfAtlasImage
		{
			Width = 256,
			Height = 256,
			Format = TexSurfaceFormat.Bc1,
			Blocks = new byte[1024],
		};
		AssertThat(Engine_EhfPages.CanUploadPageBlocksDirectly(truncated, out _)).IsFalse();
	}

	[TestCase]
	public void NoDirectCallDeferredGpuTextureUploadsInShadowOrAo()
	{
		string shadowMaps = File.ReadAllText(Path.Combine(GodotRoot(), "src/Engine/Lighting/Engine_LevelShadowMaps.cs"));
		string ambientOcclusion = File.ReadAllText(Path.Combine(GodotRoot(), "src/Engine/Lighting/Engine_LevelAmbientOcclusion.cs"));

		AssertThat(shadowMaps.Contains("BindAtlasTextureOnMainThread(container)).CallDeferred()", StringComparison.Ordinal)).IsFalse();
		AssertThat(ambientOcclusion.Contains("BindAoTexturesOnMainThread(container)).CallDeferred()", StringComparison.Ordinal)).IsFalse();
	}

	[TestCase]
	public void WorldLoadLifecycle_GpuUploadPacing_WorkStealingPoses_AndGiDeferral_E2E()
	{
		const long mb = 1024L * 1024L;
		using var pacer = new Engine_ServerSyncs.GpuUploadPacer();
		try
		{
			pacer.MarkMainThread();
			pacer.LoadCoverActive = true;
			Engine_GiRefresh.HoldForWorldLoad = true;

			int uploadedChunks = 0;
			var workers = new Task[4];
			for (int w = 0; w < workers.Length; w++)
			{
				workers[w] = Task.Run(() =>
				{
					pacer.RunGpuUpload(50L * mb, () => Interlocked.Increment(ref uploadedChunks));
				});
			}

			AssertThat(SpinWait.SpinUntil(() => pacer.PendingWorkerUploads == 4, 2000)).IsTrue();

			var poseBatch = new Engine_ClipAnimator.PoseBatch();
			int framesSimulated = 0;
			while (!Task.WhenAll(workers).IsCompleted && framesSimulated < 16)
			{
				framesSimulated++;
				pacer.BeginMainFrameUploadBudget();
				pacer.PumpWorkerGpuUploads(50.0);

				// Even while background workers wait on GPU upload fences across frames,
				// main-thread pose batches complete every frame without stalling.
				var frameRigs = new MockPoseRig[8];
				for (int r = 0; r < frameRigs.Length; r++)
				{
					frameRigs[r] = new MockPoseRig((uint)(r + 1));
					poseBatch.AddItem(frameRigs[r]);
				}

				poseBatch.Finish();
				for (int r = 0; r < frameRigs.Length; r++)
				{
					AssertThat(frameRigs[r].WriteCount).IsEqual(1);
				}

				AssertThat(Engine_GiRefresh.HoldForWorldLoad).IsTrue();
			}

			AssertThat(Task.WaitAll(workers, TimeSpan.FromSeconds(2))).IsTrue();
			AssertThat(Volatile.Read(ref uploadedChunks)).IsEqual(4);
			AssertThat(framesSimulated).IsGreaterEqual(2);

			// Streaming complete: release GI hold, verify 2-frame post-hold settle before SDFGI enables.
			Engine_GiRefresh.HoldForWorldLoad = false;
			pacer.LoadCoverActive = false;
			AssertThat(Engine_GiRefresh.HoldForWorldLoad).IsFalse();
			AssertThat(Engine_GiRefresh.SettlingAfterWorldLoad).IsTrue();
			AssertThat(Engine_GiRefresh.AdvanceWorldLoadGate()).IsFalse();
			AssertThat(Engine_GiRefresh.AdvanceWorldLoadGate()).IsFalse();
			AssertThat(Engine_GiRefresh.SettlingAfterWorldLoad).IsFalse();
			AssertThat(Engine_GiRefresh.AdvanceWorldLoadGate()).IsTrue();
		}
		finally
		{
			Engine_GiRefresh.ResetWorldLoadGateForTest();
		}
	}

	[TestCase]
	public void RendererSettingsAndProjectGodot_Configure128MbStagingCeilingAndExpandedD3D12DescriptorLimits()
	{
		var emptyConfig = new Dictionary<string, string>();
		string modernCfg = GameChildProcess.BuildRendererSettingsContent(emptyConfig, "modern");
		AssertThat(modernCfg.Contains("rendering_device/staging_buffer/max_size_mb=64\n", StringComparison.Ordinal)).IsTrue();
		AssertThat(modernCfg.Contains("rendering_device/d3d12/max_resource_descriptors=131072\n", StringComparison.Ordinal)).IsTrue();
		AssertThat(modernCfg.Contains("rendering_device/d3d12/max_sampler_descriptors=2048\n", StringComparison.Ordinal)).IsTrue();
		AssertThat(modernCfg.Contains("rendering_device/d3d12/max_resource_descriptors_per_frame=65536\n", StringComparison.Ordinal)).IsTrue();
		AssertThat(modernCfg.Contains("rendering_device/d3d12/max_sampler_descriptors_per_frame=2048\n", StringComparison.Ordinal)).IsTrue();
		AssertThat(modernCfg.Contains("rendering_device/d3d12/max_misc_descriptors_per_frame=4096\n", StringComparison.Ordinal)).IsTrue();
		AssertThat(modernCfg.Contains("driver/depth_prepass/enable=false", StringComparison.Ordinal)).IsFalse();

		string classicCfg = GameChildProcess.BuildRendererSettingsContent(emptyConfig, "classic");
		AssertThat(classicCfg.Contains("rendering_device/staging_buffer/max_size_mb=64\n", StringComparison.Ordinal)).IsTrue();
		AssertThat(classicCfg.Contains("driver/depth_prepass/enable=false\n", StringComparison.Ordinal)).IsTrue();

		string projectGodot = File.ReadAllText(Path.Combine(GodotRoot(), "project.godot"));
		AssertThat(projectGodot.Contains("rendering_device/staging_buffer/max_size_mb=64", StringComparison.Ordinal)).IsTrue();
		AssertThat(projectGodot.Contains("rendering_device/d3d12/max_resource_descriptors_per_frame=65536", StringComparison.Ordinal)).IsTrue();
		AssertThat(projectGodot.Contains("rendering_device/d3d12/max_sampler_descriptors=2048", StringComparison.Ordinal)).IsTrue();
		AssertThat(projectGodot.Contains("rendering_device/d3d12/max_sampler_descriptors_per_frame=2048", StringComparison.Ordinal)).IsTrue();
		AssertThat(projectGodot.Contains("rendering_device/d3d12/max_misc_descriptors_per_frame=4096", StringComparison.Ordinal)).IsTrue();
	}

	[TestCase]
	public void SdfgiWorldLoadGate_StaggersActivationAcrossPostHoldSettleFramesAndUses4Cascades()
	{
		AssertThat(Engine_LevelLighting.DefaultSdfgiCascades).IsEqual(4);

		try
		{
			Engine_GiRefresh.ResetWorldLoadGateForTest();
			AssertThat(Engine_GiRefresh.AdvanceWorldLoadGate()).IsTrue();

			Engine_GiRefresh.HoldForWorldLoad = true;
			AssertThat(Engine_GiRefresh.AdvanceWorldLoadGate()).IsFalse();
			AssertThat(Engine_GiRefresh.SettlingAfterWorldLoad).IsTrue();

			Engine_GiRefresh.HoldForWorldLoad = false;
			for (int i = 0; i < Engine_GiRefresh.PostHoldSettleFrameCount; i++)
			{
				AssertThat(Engine_GiRefresh.SettlingAfterWorldLoad).IsTrue();
				AssertThat(Engine_GiRefresh.AdvanceWorldLoadGate()).IsFalse();
			}

			AssertThat(Engine_GiRefresh.SettlingAfterWorldLoad).IsFalse();
			AssertThat(Engine_GiRefresh.AdvanceWorldLoadGate()).IsTrue();
		}
		finally
		{
			Engine_GiRefresh.ResetWorldLoadGateForTest();
		}
	}

	[TestCase]
	public void PostFxAndClassicScreenShadow_FreeOwnedTargetsOnDisposeAndCreateWhiteMaskOnRenderThread()
	{
		string postFx = File.ReadAllText(Path.Combine(GodotRoot(), "src/Engine/Lighting/Engine_PostFxEffect.cs"));
		AssertThat(postFx.Contains("FreeTexture(ref _staging);", StringComparison.Ordinal)).IsTrue();
		AssertThat(postFx.Contains("FreeTexture(ref _shaftDepthHalf[i]);", StringComparison.Ordinal)).IsTrue();
		AssertThat(postFx.Contains("_staging = default;", StringComparison.Ordinal)).IsFalse();

		string classicShadow = File.ReadAllText(Path.Combine(GodotRoot(), "src/Engine/material/Engine_ClassicScreenShadow.cs"));
		AssertThat(classicShadow.Contains("BindWhite();", StringComparison.Ordinal)).IsTrue();
		AssertThat(classicShadow.Contains("RenderingServer.CallOnRenderThread(Callable.From(BindWhite));", StringComparison.Ordinal)).IsTrue();

		string shadowMaps = File.ReadAllText(Path.Combine(GodotRoot(), "src/Engine/Lighting/Engine_LevelShadowMaps.cs"));
		AssertThat(shadowMaps.Contains("ActiveAtlasRdRid = texRid.IsValid ? RenderingServer.TextureGetRdTexture(texRid) : default;", StringComparison.Ordinal)).IsTrue();
	}
}

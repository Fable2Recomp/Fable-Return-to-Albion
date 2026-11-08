using System.Linq;
using GdUnit4;
using Refii.Lighting;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_pipeline_warmup`: the light combinations its plan draws.</summary>
[TestSuite]
public sealed class PipelineWarmupPlanTests
{
	private static string List(bool omni, bool spot, bool pinned) =>
		string.Join(" ", Engine_PipelineWarmup.CombosFor(omni, spot, pinned).Select(c => $"{c.Omni}{c.Spot}"));

	/// <summary>The list the plan walked before there was a pinned case, in its order.</summary>
	[TestCase]
	public void WithoutFillersEveryCombinationTheLightsCanMakeIsDrawn()
	{
		AssertThat(List(true, true, false)).IsEqual("00 10 20 01 11 21 02 12 22");
		AssertThat(List(true, false, false)).IsEqual("00 10 20");
		AssertThat(List(false, true, false)).IsEqual("00 01 02");
		AssertThat(List(false, false, false)).IsEqual("00");
	}

	[TestCase]
	public void WithFillersOneCombinationWithNoTestLightsIsDrawn()
	{
		AssertThat(List(true, true, true)).IsEqual("00");
		AssertThat(List(false, false, true)).IsEqual("00");
	}

	/// <summary>
	/// The reason one is enough: two fillers per type is Godot's "several", and a third light
	/// is too (`shader_count_for`: 0 none, 1 single, otherwise multiple).
	/// </summary>
	[TestCase]
	public void TheFillersReachSeveral() => AssertThat(Engine_StableLightCounts.PerType).IsGreaterEqual(2);

	/// <summary>On the logs' 66 sources: 3 frames placing, then the last copies' 2, inside the 10 settle frames.</summary>
	[TestCase]
	public void PinnedFitsTheSettleFrames()
	{
		int draws = 66 * Engine_PipelineWarmup.CombosFor(true, true, true).Count();
		int frames = (draws + Engine_PipelineWarmup.CellsPerFrame - 1) / Engine_PipelineWarmup.CellsPerFrame;
		AssertThat(frames).IsEqual(3);
		AssertThat(frames + Engine_PipelineWarmup.KeepFrames).IsLessEqual(10);
	}
}

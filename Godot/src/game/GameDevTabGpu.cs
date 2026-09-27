using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>
/// The GPU side: Godot's per-pass timestamps (when the engine captures them — `gpu_profile`),
/// the visible/shadow split of draw calls, primitives and objects, and video memory.
/// </summary>
public partial class GameDevTabGpu : GameDevTab
{
	public override string Title => "GPU";

	private readonly Label _summary = Readout();

	private readonly GameDevTable _passes =
		new(new[] { "pass", "last ms", "avg ms", "peak ms" }, sortColumn: 2);

	public GameDevTabGpu()
	{
		AddChild(_summary);
		Section("Render passes");
		AddChild(_passes);
	}

	public override void Refresh()
	{
		// The render thread's published sample (Engine_RenderStats): no server getter here.
		Engine_RenderStats.Sample render = Engine_RenderStats.Of(GameViewportNode);

		double mb = 1048576.0;
		Dictionary<string, object?> report = Engine_GpuPasses.Report();
		_summary.Text =
			$"gpu {render.GpuMs:0.00} ms   " +
			$"passes total {Engine_GpuPasses.LastTotalMs:0.00} ms   adapter {RenderingServer.GetVideoAdapterName()}\n" +
			$"              draws      prims (M)   objects\n" +
			$"visible  {render.VisibleCalls,9}  {render.VisiblePrims / 1e6,10:0.00}  {render.VisibleObjects,8}\n" +
			$"shadow   {render.ShadowCalls,9}  {render.ShadowPrims / 1e6,10:0.00}  {render.ShadowObjects,8}\n" +
			$"vram  total {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / mb:0} MB   " +
			$"textures {Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) / mb:0} MB   " +
			$"buffers {Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed) / mb:0} MB" +
			(Engine_GpuPasses.Available
				? string.Empty
				: "\npasses: not captured — set gpu_profile = true in refii.toml and relaunch");

		var rows = new List<string[]>();
		if (report.TryGetValue("passes", out object? value) &&
			value is List<Dictionary<string, object?>> passes)
		{
			foreach (Dictionary<string, object?> pass in passes)
			{
				rows.Add(new[]
				{
					pass["pass"] as string ?? "?",
					$"{pass["lastMs"]:0.000}",
					$"{pass["avgMs"]:0.000}",
					$"{pass["peakMs"]:0.000}",
				});
			}
		}

		_passes.Show(rows);
	}
}

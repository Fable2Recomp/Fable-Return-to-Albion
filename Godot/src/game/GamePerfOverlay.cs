using System.Collections.Generic;
using System.Text;
using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>
/// The live performance readout, toggled with F3: frame rate, frame time, the main thread's own
/// work, the render thread's CPU and GPU time, draw calls, video memory, GC, and Godot's per-pass
/// GPU split when the engine is capturing it (`gpu_profile = true`).
/// </summary>
/// <remarks>
/// Read only: the phases come from <see cref="Engine_FramePhases"/>, the render times from the
/// viewport's own measurement published by <see cref="Engine_RenderStats"/>, the passes from
/// <see cref="Engine_GpuPasses"/>, the counts from Godot's `Performance` monitors. The text is
/// rebuilt four times a second.
///
/// For watching, not capture: RenderDoc's layer costs most of the render thread while attached.
/// </remarks>
public partial class GamePerfOverlay : CanvasLayer
{
	private const double RefreshSeconds = 0.25;

	private Label? _label;
	private double _sinceRefresh;
	private bool _measuring;
	private readonly StringBuilder _text = new(768);

	public override void _Ready()
	{
		Layer = 128;
		ProcessMode = ProcessModeEnum.Always;

		var panel = new PanelContainer
		{
			Position = new Vector2(12, 12),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		var style = new StyleBoxFlat
		{
			BgColor = new Color(0, 0, 0, 0.6f),
			ContentMarginLeft = 10, ContentMarginRight = 10,
			ContentMarginTop = 6, ContentMarginBottom = 6,
		};
		style.SetCornerRadiusAll(4);
		panel.AddThemeStyleboxOverride("panel", style);

		_label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
		_label.AddThemeFontSizeOverride("font_size", 15);
		_label.AddThemeColorOverride("font_color", new Color(0.92f, 0.95f, 0.92f));
		_label.AddThemeConstantOverride("outline_size", 3);
		_label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
		panel.AddChild(_label);
		AddChild(panel);

		IReadOnlyDictionary<string, string> config = Engine_RefiiConfig.Read(out _);
		Visible = Engine_RefiiConfig.Bool(config, "perf_overlay", false);
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F3 })
		{
			Visible = !Visible;
			_sinceRefresh = RefreshSeconds;
			GetViewport().SetInputAsHandled();
		}
	}

	public override void _Process(double delta)
	{
		if (!Visible || _label is null)
		{
			return;
		}

		Viewport viewport = GetViewport();
		if (!_measuring)
		{
			Engine_RenderStats.Track(viewport);
			_measuring = true;
		}

		_sinceRefresh += delta;
		if (_sinceRefresh < RefreshSeconds)
		{
			return;
		}

		_sinceRefresh = 0.0;
		_label.Text = Compose(viewport);
	}

	private string Compose(Viewport viewport)
	{
		(double frameMs, double framePeak) = Engine_FramePhases.Get("frame_total");
		(double processMs, _) = Engine_FramePhases.Get("process_total");
		(double waitMs, _) = Engine_FramePhases.Get("draw");
		(double entitiesMs, _) = Engine_FramePhases.Get("ent.loop");

		// Read off the render thread's published sample, never the server: a getter from here
		// would block on the whole render queue (Engine_RenderStats).
		Engine_RenderStats.Sample render = Engine_RenderStats.Of(viewport);
		double renderCpu = render.CpuMs + RenderingServer.GetFrameSetupTimeCpu();
		double renderGpu = render.GpuMs;

		double fps = Engine.GetFramesPerSecond();
		double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
		double primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
		double vram = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0;

		_text.Clear();
		if (GameWhereabouts.Current is { } place)
		{
			_text.Append(place.DisplayName).Append('\n');
		}

		_text.Append($"{fps:0} fps   {frameMs:0.0} ms  (peak {framePeak:0})\n");
		_text.Append($"main  {processMs:0.0} ms   entities {entitiesMs:0.0}   waiting on render {waitMs:0.0}\n");
		_text.Append($"render cpu {renderCpu:0.0} ms   gpu {renderGpu:0.0} ms   ");
		_text.Append(Bound(processMs, renderCpu, renderGpu)).Append('\n');
		_text.Append($"draws {drawCalls:0}   prims {primitives / 1e6:0.0} M   vram {vram / 1024.0:0.00} GB\n");
		_text.Append($"gc gen2 {System.GC.CollectionCount(2)}   pause {System.GC.GetTotalPauseDuration().TotalMilliseconds / 1000.0:0.0} s total");

		if (Engine_GpuPasses.Available &&
			Engine_GpuPasses.Report().TryGetValue("passes", out object? value) &&
			value is List<Dictionary<string, object?>> passes)
		{
			var top = new List<(string Name, double Ms)>();
			foreach (Dictionary<string, object?> pass in passes)
			{
				if (pass["pass"] is string name && name != "total" && pass["avgMs"] is double ms && ms >= 0.2)
				{
					top.Add((name, ms));
				}
			}

			top.Sort((a, b) => b.Ms.CompareTo(a.Ms));
			_text.Append($"\ngpu passes (total {Engine_GpuPasses.LastTotalMs:0.0} ms)");
			for (int i = 0; i < top.Count && i < 6; ++i)
			{
				_text.Append($"\n  {top[i].Ms,5:0.0}  {top[i].Name}");
			}
		}
		else
		{
			_text.Append("\ngpu passes: set gpu_profile = true");
		}

		return _text.ToString();
	}

	/// <summary>Which wall the frame is standing on right now, by the largest of the three.</summary>
	private static string Bound(double main, double renderCpu, double gpu)
	{
		if (gpu >= renderCpu && gpu >= main)
		{
			return "[GPU-bound]";
		}

		return renderCpu >= main ? "[render-thread-bound]" : "[main-thread-bound]";
	}
}

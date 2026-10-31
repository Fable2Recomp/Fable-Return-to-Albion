using System.Text;
using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>
/// The frame at a glance: the headline numbers, a per-frame graph of frame / main / render CPU /
/// GPU time against the platform performance target, and the worst frames caught with the phases in them.
/// </summary>
public partial class GameDevTabFrame : GameDevTab
{
	public override string Title => "Frame";

	private readonly GameDevGraph _graph = new();
	private readonly Label _numbers = Readout();
	private readonly Label _hitches = Readout();
	private readonly StringBuilder _text = new(1024);
	private bool _measuring;

	/// <summary>Builds the frame readout and graph using the platform goal independently of presentation caps.</summary>
	public GameDevTabFrame()
	{
		bool android = OS.HasFeature("android");
		_graph.Budgets = new[] { ((float)Engine_PresentationPolicy.BudgetMs(android), $"{Engine_PresentationPolicy.TargetFps(android)} fps target") };
		_graph.Lines.Add(new GameDevGraph.Series { Name = "frame", Colour = new Color(1f, 1f, 1f) });
		_graph.Lines.Add(new GameDevGraph.Series { Name = "main", Colour = new Color(0.45f, 0.8f, 1f) });
		_graph.Lines.Add(new GameDevGraph.Series { Name = "render cpu", Colour = new Color(1f, 0.75f, 0.3f) });
		_graph.Lines.Add(new GameDevGraph.Series { Name = "gpu", Colour = new Color(0.5f, 1f, 0.5f) });

		AddChild(_numbers);
		Section("Frame time — newest on the right");
		AddChild(_graph);
		Section("Worst frames (over 25 ms), with the phases that ran in them");
		var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		scroll.AddChild(_hitches);
		AddChild(scroll);
	}

	public override void Tick(double delta)
	{
		Viewport viewport = GameViewportNode;
		if (!_measuring)
		{
			Engine_RenderStats.Track(viewport);
			_measuring = true;
		}

		// The render thread's published sample: a server getter here, every frame, would
		// serialise the two threads (Engine_RenderStats).
		Engine_RenderStats.Sample render = Engine_RenderStats.Of(viewport);
		_graph.Push(
			(float)(delta * 1000.0),
			(float)Engine_FramePhases.Last("process_total"),
			(float)(render.CpuMs + RenderingServer.GetFrameSetupTimeCpu()),
			(float)render.GpuMs);
	}

	/// <summary>Displays measured frame costs against a diagnostic budget without assuming resolution or pacing.</summary>
	public override void Refresh()
	{
		Engine_RenderStats.Sample render = Engine_RenderStats.Of(GameViewportNode);
		(double frameMs, double framePeak) = Engine_FramePhases.Get("frame_total");
		(double mainMs, double mainPeak) = Engine_FramePhases.Get("process_total");
		(double waitMs, _) = Engine_FramePhases.Get("draw");
		(double entitiesMs, _) = Engine_FramePhases.Get("ent.loop");
		double renderCpu = render.CpuMs + RenderingServer.GetFrameSetupTimeCpu();
		double gpu = render.GpuMs;

		string bound = gpu >= renderCpu && gpu >= mainMs
			? "GPU-bound"
			: renderCpu >= mainMs ? "render-thread-bound" : "main-thread-bound";

		_text.Clear();
		if (GameWhereabouts.Current is { } place)
		{
			_text.Append(place.DisplayName).Append('\n');
		}

		_text.Append($"{Engine.GetFramesPerSecond(),4:0} fps    frame {frameMs,6:0.00} ms (peak {framePeak:0.0})    {bound}\n");
		_text.Append($"main   {mainMs,6:0.00} ms (peak {mainPeak:0.0})   entities {entitiesMs:0.00}   waiting on render {waitMs:0.00}\n");
		_text.Append($"render cpu {renderCpu,6:0.00} ms   gpu {gpu,6:0.00} ms   {Engine_PresentationPolicy.BudgetLabel(OS.HasFeature("android"))}\n");
		_text.Append($"draws  visible {render.VisibleCalls,6}   shadow {render.ShadowCalls,6}   " +
			$"objects {render.VisibleObjects}   prims {render.VisiblePrims / 1e6:0.0} M");
		_numbers.Text = _text.ToString();

		_text.Clear();
		lock (Engine_FramePhases.Hitches)
		{
			foreach (Engine_FramePhases.Hitch hitch in Engine_FramePhases.Hitches)
			{
				_text.Append($"{hitch.TotalMs,7:0.0} ms  {hitch.Phases}");
				if (hitch.Gap.Length != 0)
				{
					_text.Append($"   | gap {hitch.Gap}");
				}

				_text.Append('\n');
			}
		}

		_hitches.Text = _text.Length == 0 ? "none caught yet" : _text.ToString();
	}
}

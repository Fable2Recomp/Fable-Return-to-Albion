using System.Text;
using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>
/// The frame at a glance: the headline numbers, a per-frame graph of frame / main / render CPU /
/// GPU time against the 120 fps budget, and the worst frames caught with the phases in them.
/// </summary>
public partial class GameDevTabFrame : GameDevTab
{
	public override string Title => "Frame";

	private readonly GameDevGraph _graph = new();
	private readonly Label _numbers = Readout();
	private readonly Label _hitches = Readout();
	private readonly StringBuilder _text = new(1024);
	private bool _measuring;

	public GameDevTabFrame()
	{
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
		Rid viewport = GameViewport;
		if (!_measuring)
		{
			RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
			_measuring = true;
		}

		_graph.Push(
			(float)(delta * 1000.0),
			(float)Engine_FramePhases.Last("process_total"),
			(float)(RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport) +
				RenderingServer.GetFrameSetupTimeCpu()),
			(float)RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport));
	}

	public override void Refresh()
	{
		Rid viewport = GameViewport;
		(double frameMs, double framePeak) = Engine_FramePhases.Get("frame_total");
		(double mainMs, double mainPeak) = Engine_FramePhases.Get("process_total");
		(double waitMs, _) = Engine_FramePhases.Get("draw");
		(double entitiesMs, _) = Engine_FramePhases.Get("ent.loop");
		double renderCpu = RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport) +
			RenderingServer.GetFrameSetupTimeCpu();
		double gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
		double Info(RenderingServer.ViewportRenderInfoType type, RenderingServer.ViewportRenderInfo info) =>
			RenderingServer.ViewportGetRenderInfo(viewport, type, info);

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
		_text.Append($"render cpu {renderCpu,6:0.00} ms   gpu {gpu,6:0.00} ms   budget 8.33 ms (1440p @ 120)\n");
		_text.Append($"draws  visible {Info(RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame),6:0}   " +
			$"shadow {Info(RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame),6:0}   " +
			$"objects {Info(RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame):0}   " +
			$"prims {Info(RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.PrimitivesInFrame) / 1e6:0.0} M");
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

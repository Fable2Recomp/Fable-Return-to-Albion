using System.Collections.Generic;
using System.Text;
using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>
/// The live performance readout, toggled with F5: frame rate, frame time, the CPU and GPU clocks
/// and their thermal caps where the platform publishes them, the main thread's own work, the render thread's CPU and GPU time, draw calls, video memory, GC, and Godot's per-pass
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
	private PanelContainer? _panel;
	private double _sinceRefresh;

	/// <summary>The panel's distance from the usable screen's top-left corner, in canvas units.</summary>
	private static readonly Vector2 Inset = new(12, 12);
	private bool _measuring;
	private readonly StringBuilder _text = new(768);

	public override void _Ready()
	{
		Layer = 128;
		ProcessMode = ProcessModeEnum.Always;

		var panel = new PanelContainer
		{
			Position = Inset,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_panel = panel;
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
		if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F5 })
		{
			Toggle();
			GetViewport().SetInputAsHandled();
		}
	}

	/// <summary>Toggles the existing readout from a keyboard or Android diagnostic button.</summary>
	public void Toggle()
	{
		Visible = !Visible;
		_sinceRefresh = RefreshSeconds;
		PlaceInSafeArea(GetViewport());
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
		PlaceInSafeArea(viewport);
		_label.Text = Compose(viewport);
	}

	/// <summary>
	/// Keeps the panel inside the display's safe area: a phone's camera cutout sits in the top-left
	/// corner in one landscape orientation and moves to the other side when the phone is turned
	/// round, so the area is read again at every refresh. The safe area is in screen pixels; the
	/// panel lives on the stretched canvas, so it is scaled into canvas units first. A screen with no
	/// cutout reports the whole window and the panel stays where it always was.
	/// </summary>
	private void PlaceInSafeArea(Viewport viewport)
	{
		if (_panel is null)
		{
			return;
		}

		Rect2I safe = DisplayServer.GetDisplaySafeArea();
		Vector2I window = DisplayServer.WindowGetSize();
		Vector2 canvas = viewport.GetVisibleRect().Size;
		if (window.X <= 0 || window.Y <= 0)
		{
			_panel.Position = Inset;
			return;
		}

		Vector2I offset = SafeAreaOffset(safe, new Rect2I(DisplayServer.WindowGetPosition(), window));
		var toCanvas = new Vector2(canvas.X / window.X, canvas.Y / window.Y);
		_panel.Position = Inset + new Vector2(offset.X * toCanvas.X, offset.Y * toCanvas.Y);
	}

	/// <summary>
	/// How far into the window the safe area starts, in screen pixels. Both rectangles are in the
	/// desktop's coordinates, which on a phone start at the window's corner and on a desktop with
	/// several monitors do not: there the safe area is the primary screen's usable rectangle, at
	/// (2560, 1440) when that screen sits right of and below another, and taken as it is it put
	/// the panel outside the window. Only the part of the safe area the window shows counts, so a
	/// window on another screen, or one smaller than the area, keeps the panel at its corner.
	/// </summary>
	internal static Vector2I SafeAreaOffset(Rect2I safe, Rect2I window)
	{
		if (safe.Size.X <= 0 || safe.Size.Y <= 0 || window.Size.X <= 0 || window.Size.Y <= 0)
		{
			return Vector2I.Zero;
		}

		Rect2I shown = window.Intersection(safe);
		if (shown.Size.X <= 0 || shown.Size.Y <= 0)
		{
			return Vector2I.Zero;
		}

		return shown.Position - window.Position;
	}

	/// <summary>Formats measured performance and the platform goal independently of presentation caps.</summary>
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
		double assetVram = (Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) +
			Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed)) / 1048576.0;

		_text.Clear();
		if (GameWhereabouts.Current is { } place)
		{
			_text.Append(place.DisplayName).Append('\n');
		}

		_text.Append($"{fps:0} fps   {frameMs:0.0} ms  (peak {framePeak:0})\n");
		// The clocks the frame rate was made at: a warm phone runs at a fraction of them.
		foreach (string line in Engine_DeviceClocks.Format(Engine_DeviceClocks.Read()))
		{
			_text.Append(line).Append('\n');
		}
		_text.Append($"main  {processMs:0.0} ms   entities {entitiesMs:0.0}   waiting on render {waitMs:0.0}\n");
		_text.Append($"render cpu {renderCpu:0.0} ms   gpu {renderGpu:0.0} ms   ");
		_text.Append(Bound(processMs, renderCpu, renderGpu)).Append('\n');
		_text.Append($"draws {drawCalls:0}   prims {primitives / 1e6:0.0} M   vram {vram / 1024.0:0.00} GB (assets {assetVram / 1024.0:0.00})\n");
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

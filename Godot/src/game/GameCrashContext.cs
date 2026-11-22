using System;
using System.Globalization;
using Godot;

namespace Refii.Game;

/// <summary>
/// States Godot's condition to the bridge every two seconds, so a crash record says which
/// adapter and driver the game ran on and how much graphics memory it held when it stopped
/// (<see cref="Refii.Host.HostBridge.NoteEngineState"/>).
/// </summary>
public static class GameCrashContext
{
	private const double Interval = 2.0;
	private const double Megabyte = 1048576.0;

	private static double _elapsed = Interval;
	private static string _adapter = string.Empty;

	public static void Tick(Node node, double delta)
	{
		_elapsed += delta;
		if (_elapsed < Interval)
		{
			return;
		}

		_elapsed = 0.0;
		if (_adapter.Length == 0)
		{
			_adapter = $"{RenderingServer.GetVideoAdapterName()} " +
				$"({RenderingServer.GetCurrentRenderingDriverName()} {RenderingServer.GetVideoAdapterApiVersion()})";
		}

		Viewport viewport = node.GetViewport();
		Vector2I window = DisplayServer.WindowGetSize();
		Vector2 view = viewport.GetVisibleRect().Size;
		string line = string.Create(CultureInfo.InvariantCulture,
			$"adapter {_adapter}; video {Mb(Performance.Monitor.RenderVideoMemUsed)} MB " +
			$"(textures {Mb(Performance.Monitor.RenderTextureMemUsed)}, buffers {Mb(Performance.Monitor.RenderBufferMemUsed)}); " +
			$"managed {GC.GetTotalMemory(false) / Megabyte:0} MB; " +
			$"window {window.X}x{window.Y} mode {DisplayServer.WindowGetMode()}; view {view.X:0}x{view.Y:0} " +
			$"scale {viewport.Scaling3DScale:0.##} {viewport.Scaling3DMode}; msaa {viewport.Msaa3D}; " +
			$"objects {Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame):0}; " +
			$"up {Time.GetTicksMsec() / 1000.0:0} s");
		Refii.Host.HostBridge.NoteEngineState(line);
	}

	private static string Mb(Performance.Monitor monitor) =>
		(Performance.GetMonitor(monitor) / Megabyte).ToString("0", CultureInfo.InvariantCulture);
}

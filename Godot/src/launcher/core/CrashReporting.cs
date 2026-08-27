using System;
using System.Threading.Tasks;
using Godot;

namespace Refii.Launcher.Core;

/// <summary>
/// Makes a managed death say what it was. An exception on a thread Godot did not call —
/// a <c>Task</c>, a worker, a timer — is fatal to the process and .NET reports it on stderr
/// only, which an exported build has nowhere to put. Godot's error path flushes its log file,
/// so these land in <c>godot.log</c> before the process goes.
/// </summary>
public static class CrashReporting
{
	private static bool _installed;

	public static void Install()
	{
		if (_installed)
		{
			return;
		}

		_installed = true;
		AppDomain.CurrentDomain.UnhandledException += (_, args) =>
		{
			string text = args.ExceptionObject is Exception error ? error.ToString() : $"{args.ExceptionObject}";
			GD.PushError($"[crash] unhandled managed exception (terminating={args.IsTerminating}): {text}");
			GD.PrintErr($"[crash] unhandled managed exception (terminating={args.IsTerminating}): {text}");
		};
		TaskScheduler.UnobservedTaskException += (_, args) =>
		{
			GD.PushError($"[crash] unobserved task exception: {args.Exception}");
		};
	}
}

namespace Refii.Titles.Fable3;

using Godot;
using Refii.Core;
using Refii.Host;
using Refii.Ui;

/// <summary>
/// The platform's text entry for Fable 3: the guest asks the console for a string
/// (`XamShowKeyboardUI`, whose one caller is the interaction mode's rename of the dog or a weapon)
/// and the host is the console. Fable3/src/hooks/src/keyboard_ui.cpp leaves the request pending
/// and states it as `@Keyboard.Open(user, title, description, default, max units)`; the answer
/// goes back as the guest tick action `keyboard:&lt;text&gt;` or `keyboard-cancel`, which completes
/// the guest's XOVERLAPPED on its own thread. The guest renames when the text is non-empty and new
/// (sub_82962720) and posts "ClosedSystemUI" otherwise (sub_82962E28) — both its own. The entry
/// itself is the shared <see cref="Engine_UiTextEntry"/>; while it is open <see cref="Fable3Input"/>
/// keeps the pad from the guest, as the console's keyboard does.
/// </summary>
public partial class Fable3Keyboard : Control
{
	/// <summary>One open request, as the hook stated it.</summary>
	public readonly record struct Request(string Title, string Description, string Default, int MaxUnits);

	private static Request? _pending;
	private static ulong _generation;

	/// <summary>Whether the entry is up: the guest's pad is held neutral meanwhile.</summary>
	public static bool IsOpen { get; private set; }

	/// <summary>Routes the hook's statement; called with the title's other routes.</summary>
	public static void Register(Engine_UiGuiNatives router) =>
		router.Register("@Keyboard.Open", call =>
		{
			_pending = new Request(call.Argument(1).Text, call.Argument(2).Text, call.Argument(3).Text,
				System.Math.Max(1, (int)call.Argument(4).Number));
			++_generation;
		});

	private Engine_UiTextEntry _entry = null!;
	private ulong _shown;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		_entry = new Engine_UiTextEntry { Name = "Entry" };
		_entry.Accepted += text => Answer(text);
		_entry.Cancelled += () => Answer(null);
		AddChild(_entry);
	}

	public override void _Process(double delta)
	{
		if (_shown != _generation && _pending is { } request)
		{
			_shown = _generation;
			// The guest's buffer holds MaxUnits code units with the terminator.
			_entry.Open(request.Title, request.Description, request.Default, request.MaxUnits - 1);
			IsOpen = true;
			GD.Print($"[fable3-keyboard] open '{request.Title}' (max {request.MaxUnits - 1} character(s))");
		}
	}

	private void Answer(string? text)
	{
		IsOpen = false;
		_pending = null;
		Engine_TitleGame.Active.Input.HoldUntilReleased();
		uint depth = HostBridge.PostFrontEndAction(text is null ? "keyboard-cancel" : "keyboard:" + text);
		GD.Print($"[fable3-keyboard] answered {(text is null ? "cancel" : $"'{text}'")} (queue depth {depth})");
	}
}

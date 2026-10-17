namespace Refii.Ui;

/// <summary>
/// Input cadence for a screen's second input level (the options category page). Nothing is
/// drawn: the level stays guest-mirrored; this only supplies the auto-repeat the guest's stick
/// translator would (the masked D-pad never reaches it). 36 commits the whole page
/// (`sub_829BF698`); nothing is written while scrolling. Each axis has its own clock.
/// </summary>
public sealed class Engine_UiScreenLevel
{
	private Engine_UiNavigationClock? _vertical;

	private Engine_UiNavigationClock? _horizontal;

	/// <summary>A clock on the page gate's repeat numbers, not the menu's.</summary>
	private static Engine_UiNavigationClock PageClock() => new()
	{
		FirstInterval = Engine_UiScreenInput.PageFirstRepeat,
		SubsequentInterval = Engine_UiScreenInput.PageRepeat,
	};

	public void Drive(Engine_UiSurfaceInput input)
	{
		_vertical ??= PageClock();
		_horizontal ??= PageClock();

		for (int step = _vertical.Steps(input.Direction, input.Delta);
			step > 0; --step)
		{
			Send(input.Direction < 0 ? Engine_UiScreenInput.Up : Engine_UiScreenInput.Down);
		}

		for (int step = _horizontal.Steps(input.Horizontal, input.Delta);
			step > 0; --step)
		{
			Send(input.Horizontal < 0 ? Engine_UiScreenInput.Left : Engine_UiScreenInput.Right);
		}

		if (input.Accept)
		{
			Send(Engine_UiScreenInput.Select);
		}
		else if (input.Back)
		{
			Send(Engine_UiScreenInput.Back);
		}
	}

	/// <summary>To the guest, and to the host's own record of the page's pending choices.</summary>
	private static void Send(uint code)
	{
		if (!Engine_UiWeatherOptions.Open) Engine_UiSource.Current.ScreenAction(code);
		Engine_UiOptionsPending.Track(code);
	}

	// ---------------------------------------------------------------------------------------
	// THE MOUSE ON AN OPTIONS PAGE
	// ---------------------------------------------------------------------------------------
	//
	// The guest owns the row and every value; the pointer only posts the codes the keys would,
	// each followed by its release (32..35) so the page gate lets a burst land in one drain.
	// Hovering a row walks the cursor to it; a click or drag on a slider steps it to the point
	// under the pointer; a pickbox steps back on its left half and forward on its right; the
	// wheel over a value row steps it; the page's A and B prompts commit and cancel. Every other
	// click is taken, so a stray one cannot commit the page.

	/// <summary>Each page's rows, top to bottom, as the guest orders them.</summary>
	private static readonly System.Collections.Generic.Dictionary<string, string[]> Rows = new()
	{
		[Engine_UiWeatherOptions.PageName] = Engine_UiWeatherOptions.Rows,
		["Game"] = new[]
		{
			"Options.Subtitles_pickbox", "Options.BreadcrumbSize_pickbox",
			"Options.TutorialBoxes_pickbox", "Options.MultiplayerOrbs_pickbox",
			"Options.Auto_joinable_pickbox",
		},
		["Controls"] = new[] { "Page2.Contents.pickbox1" },
		["Video"] = new[] { "Page3.Contents.slider1" },
		["Audio"] = new[]
		{
			"Page4.Contents.slider1", "Page4.Contents.slider2", "Page4.Contents.slider3",
			"Page4.Contents.pickbox1",
		},
	};

	/// <summary>`sub_829907F0` ignores the page for its first half second; so does the pointer.</summary>
	private const double OpenQuiet = 0.55;

	private string _page = string.Empty;
	private double _openFor;
	private int _dragRow = -1;

	/// <summary>One frame of the pointer on the open page of <paramref name="book"/>.</summary>
	public void Pointer(Engine_UiOwnedBook book, double delta)
	{
		string page = Engine_UiOptionsPending.Page;
		if (page != _page)
		{
			_page = page;
			_openFor = 0.0;
			_dragRow = -1;
		}

		_openFor += delta;
		if (!Rows.TryGetValue(page, out string[]? rows) || Engine_UiPointer.Canvas is not { } at)
		{
			return;
		}

		bool left = Engine_UiPointer.TakeLeft();
		int wheel = Engine_UiPointer.TakeWheel();
		if (_openFor < OpenQuiet)
		{
			return;
		}

		// A slider drag holds its row while the button is down, wherever the pointer goes.
		if (_dragRow >= 0)
		{
			if (Godot.Input.IsMouseButtonPressed(Godot.MouseButton.Left) && _dragRow < rows.Length)
			{
				SetSlider(book, rows[_dragRow], _dragRow, at);
				return;
			}

			_dragRow = -1;
		}

		if (left && PromptAt(book, at) is { } prompt)
		{
			Send(prompt);
			return;
		}

		if (Engine_UiWeatherOptions.Open && wheel != 0)
		{
			WalkTo(System.Math.Clamp(Engine_UiOptionsPending.Row - wheel, 0, Engine_UiWeatherOptions.Keys.Length - 1));
			return;
		}
		int row = RowAt(book, rows, at);
		if (row < 0)
		{
			return;
		}

		if (Engine_UiPointer.Moved || left || wheel != 0)
		{
			WalkTo(row + (Engine_UiWeatherOptions.Open ? Engine_UiWeatherOptions.VisibleStart : 0));
		}

		bool slider = Engine_UiOptionsPending.SliderValue(row) is not null;
		if (left)
		{
			if (slider)
			{
				_dragRow = row;
				SetSlider(book, rows[row], row, at);
			}
			else if (book.NodeFor(rows[row]) is { } box &&
				Engine_UiPointer.CanvasBounds(box) is { } bounds)
			{
				Step(at.X < bounds.GetCenter().X ? -1 : 1);
			}
		}

		if (wheel != 0)
		{
			// Wheel up raises the value, as on a PC slider or spinner.
			for (int i = 0; i < System.Math.Abs(wheel); ++i)
			{
				Step(wheel < 0 ? 1 : -1);
			}
		}
	}

	/// <summary>Moves the guest's row cursor to <paramref name="row"/>, one pair per row.</summary>
	private static void WalkTo(int row)
	{
		while (Engine_UiOptionsPending.Row != row)
		{
			bool up = row < Engine_UiOptionsPending.Row;
			int before = Engine_UiOptionsPending.Row;
			Send(up ? Engine_UiScreenInput.Up : Engine_UiScreenInput.Down);
			if (!Engine_UiWeatherOptions.Open)
				Engine_UiSource.Current.ScreenAction(
					up ? Engine_UiScreenInput.UpReleased : Engine_UiScreenInput.DownReleased);
			if (Engine_UiOptionsPending.Row == before)
			{
				return;
			}
		}
	}

	/// <summary>One value step on the cursor's row, with its release.</summary>
	private static void Step(int direction)
	{
		Send(direction < 0 ? Engine_UiScreenInput.Left : Engine_UiScreenInput.Right);
		if (!Engine_UiWeatherOptions.Open)
			Engine_UiSource.Current.ScreenAction(
				direction < 0 ? Engine_UiScreenInput.LeftReleased : Engine_UiScreenInput.RightReleased);
	}

	/// <summary>
	/// Steps a slider to the value under the pointer: the pip travels `x = v·440 − 210` in the
	/// slider group's own units (`sub_82881E38`), in twentieths.
	/// </summary>
	private static void SetSlider(Engine_UiOwnedBook book, string path, int row, Godot.Vector2 at)
	{
		if (book.NodeFor(path + ".grp") is not { } group ||
			Engine_UiOptionsPending.SliderValue(row) is not { } value)
		{
			return;
		}

		WalkTo(row);
		Godot.Vector3 local = group.GlobalTransform.AffineInverse() *
			new Godot.Vector3(at.X, at.Y, group.GlobalPosition.Z);
		float wanted = Godot.Mathf.Clamp((local.X + 210f) / 440f, 0f, 1f);
		int steps = Godot.Mathf.RoundToInt((wanted - value) * 20f);
		for (int i = 0; i < System.Math.Abs(steps); ++i)
		{
			Step(steps < 0 ? -1 : 1);
		}
	}

	/// <summary>
	/// Which row a canvas point is on: within the row's own height, anywhere across the page —
	/// the Game page authors its labels apart from its boxes.
	/// </summary>
	private static int RowAt(Engine_UiOwnedBook book, string[] rows, Godot.Vector2 at)
	{
		for (int i = 0; i < rows.Length; ++i)
		{
			if (book.NodeFor(rows[i]) is { } node && node.IsVisibleInTree() &&
				Engine_UiPointer.CanvasBounds(node) is { } bounds &&
				at.Y >= bounds.Position.Y - 6f && at.Y <= bounds.End.Y + 6f &&
				at.X >= bounds.Position.X - 460f && at.X <= bounds.End.X + 40f)
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>The code a drawn A or B prompt on the page stands for, under the pointer.</summary>
	private static uint? PromptAt(Engine_UiOwnedBook book, Godot.Vector2 at)
	{
		if (Engine_UiWeatherOptions.Open)
		{
			foreach ((string path, uint code) in new[]
				{ ("Learn", Engine_UiScreenInput.Select), ("Unlearn", Engine_UiScreenInput.Back) })
			{
				if (book.NodeFor(Engine_UiWeatherOptions.Buttons + "." + path) is { } node &&
					node.IsVisibleInTree() && Engine_UiPointer.CanvasBounds(node) is { } bounds &&
					bounds.Grow(4f).HasPoint(at)) return code;
			}
			return null;
		}

		foreach ((string name, uint code) in new[]
			{ ("icon_button_a", Engine_UiScreenInput.Select), ("icon_button_b", Engine_UiScreenInput.Back) })
		{
			if (Find(book, name, at))
			{
				return code;
			}
		}

		return null;
	}

	private static bool Find(Godot.Node node, string name, Godot.Vector2 at)
	{
		if (node is Godot.Node3D { Visible: false })
		{
			return false;
		}

		if (node is Godot.Node3D found && found.Name == name &&
			Engine_UiPointer.CanvasBounds(found) is { } bounds && bounds.Grow(4f).HasPoint(at))
		{
			return true;
		}

		foreach (Godot.Node child in node.GetChildren())
		{
			if (Find(child, name, at))
			{
				return true;
			}
		}

		return false;
	}
}

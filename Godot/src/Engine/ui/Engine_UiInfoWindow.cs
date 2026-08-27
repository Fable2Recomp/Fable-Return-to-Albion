using Godot;

namespace Refii.Ui;

/// <summary>
/// The menu's info window — `scripts/gameface/infowindow.lua`, ported.
///
/// `AK.fetchObjects` pulls `Image`, `Name`, `Description` and `DescriptionComponent`; the first
/// three are written by name, the fourth is a slide machine gating the description half.
///
/// `IsMenuItemWithDetails` returns false unconditionally in the shipped script and nothing
/// overwrites it, so the "has details" arm of <see cref="UpdateDescriptionPageVisibility"/> is
/// unreachable and the description component parks on slide 2. The dead arm is kept, marked and
/// never taken. Something other than this widget draws the right-hand page's description text.
///
/// Tags, not text: `infowindow.dis main.3 pc7` and `main.4 pc7` load the attribute name
/// `'TextID'` and pass the argument straight through — no `GUI:GetText` — so these are Babel
/// tags the text layer resolves. <see cref="Engine_UiItemInfo"/> differs: its `iteminfo.dis
/// main.1/main.2 pc7..12` write `textstring` through `GUI:GetText`, already resolved.
/// </summary>
public sealed class Engine_UiInfoWindow
{
	/// <summary>The slide the description component parks on when there are no details.</summary>
	public const int ParkedSlide = 2;

	/// <summary>The slide the description component shows details on — never reached.</summary>
	public const int DetailSlide = 1;

	private Node3D? _image;
	private MeshInstance3D? _name;
	private MeshInstance3D? _description;
	private Node3D? _descriptionComponent;

	/// <summary>The theme the text is re-meshed through.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>The slide the description component is currently on.</summary>
	public int CurrentSlide { get; private set; } = DetailSlide;

	/// <summary>Whether the component's own timeline has advanced — `getTime() ~= 0`.</summary>
	public bool TimelineRunning { get; set; }

	/// <summary>
	/// The second result of `getTime(DescriptionComponent)` — `infowindow.dis main.1 pc12..15`
	/// takes three results, `pc32` tests this one. Unnamed by the script; gates the dead arm's
	/// second branch.
	/// </summary>
	public bool TimelineSecondResult { get; set; }

	/// <summary>
	/// Whether the last <see cref="UpdateDescriptionPageVisibility"/> asked for
	/// `play(DescriptionComponent)` — `infowindow.dis main.1 pc34..37`.
	///
	/// Never true as shipped: it lives inside the arm `IsMenuItemWithDetails` closes off.
	/// </summary>
	public bool PlayRequested { get; private set; }

	public bool IsBound => _descriptionComponent is not null;

	/// <summary>`AK.fetchObjects(g_InfoWindow, "Image", "Name", "Description", "DescriptionComponent")`.</summary>
	public bool Bind(Node3D window)
	{
		_image = Engine_UiTheme.FindByName(window, "Image") as Node3D;
		_name = Engine_UiTheme.FindByName(window, "Name") as MeshInstance3D;
		_description = Engine_UiTheme.FindByName(window, "Description") as MeshInstance3D;
		_descriptionComponent =
			Engine_UiTheme.FindByName(window, "DescriptionComponent") as Node3D;
		return IsBound;
	}

	/// <summary>
	/// `IsMenuItemWithDetails` — the shipped body verbatim: ignores its argument, returns false.
	/// </summary>
	public static bool IsMenuItemWithDetails(object? item) => false;

	/// <summary>
	/// `UpdateDescriptionPageVisibility` — run per update while an item is highlighted. The live
	/// arm (`main.1 pc39..47`): while the slide is 1, once the component's timeline moves off
	/// zero, park it on slide 2. True when the slide changed.
	/// </summary>
	public bool UpdateDescriptionPageVisibility(bool anyItemHighlighted)
	{
		PlayRequested = false;

		// `if L1_1 then` — nothing happens at all without a highlighted item.
		if (!IsBound || !anyItemHighlighted)
		{
			return false;
		}

		if (IsMenuItemWithDetails(null))
		{
			// Unreachable in the shipped game; two branches, not one.
			// `infowindow.dis main.1 pc22..38`:
			//
			//   pc22  EQ 2, R2          -- R2 is getCurrentSlide(DescriptionComponent)
			//   pc24  goToSlide(DescriptionComponent, 1)   ; then JMP to the RETURN
			//   pc30  EQ R4, 0          -- R4 is getTime()'s FIRST result
			//   pc32  TEST R5           -- R5 is its SECOND
			//   pc34  play(DescriptionComponent)
			//
			// The fall-through re-tests the clock rather than the slide: show the page when
			// parked, else play only while the timeline sits at zero and R5 is truthy.
			if (CurrentSlide == ParkedSlide)
			{
				CurrentSlide = DetailSlide;
				return true;
			}

			PlayRequested = !TimelineRunning && TimelineSecondResult;
			return false;
		}

		if (CurrentSlide == DetailSlide && TimelineRunning)
		{
			CurrentSlide = ParkedSlide;
			return true;
		}

		return false;
	}

	/// <summary>`SetName` — writes a Babel tag, not a string. `main.3 pc7..9`.</summary>
	public void SetName(string tag) => SetTag(_name, tag);

	/// <summary>`SetDescription` — a tag again, `main.4 pc7..9`.</summary>
	public void SetDescription(string tag) => SetTag(_description, tag);

	/// <summary>
	/// `UpdateDescriptionPage` — off the staged parameter element: `String1` name, `String2`
	/// description. Same staging object as every menu spawn.
	/// </summary>
	public void UpdateDescriptionPage(string nameTag, string descriptionTag)
	{
		SetName(nameTag);
		SetDescription(descriptionTag);
	}

	private void SetTag(MeshInstance3D? node, string tag)
	{
		// `if g_InfoWindow.Name then` — each setter guards its own element.
		if (node is null || !GodotObject.IsInstanceValid(node) || Theme is null)
		{
			return;
		}

		Theme.SetMeshText(node, Theme.Resolve(tag));
	}

	public string Describe() =>
		$"info window {(IsBound ? "bound" : "NOT bound")}, slide {CurrentSlide}" +
		(_image is null ? ", no image" : string.Empty) +
		(_name is null ? ", no name" : string.Empty) +
		(_description is null ? ", no description" : string.Empty);
}

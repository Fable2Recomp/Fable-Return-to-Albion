namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The slide fold's tie rule (Engine_BgfDocument.ComputeSlideState, ancestorsLast): a parent
/// component's slide switches a child component off, the child's own slide names the child
/// active. Document order lets the child's self-entry win; ancestors-last lets the parent's gate
/// hold — Fable 3's `ObjectiveList.ObjectiveList` `Slide1` over each `Bar`'s own `Slide1`.
/// </summary>
[TestSuite]
public class BgfSlideRestOrderTests
{
	private static BgfSlideEntry Entry(int ordinal, bool active) =>
		new() { ElementOrdinal = ordinal, Active = active, Commands = System.Array.Empty<BgfAttribute>(), AnimationEvents = System.Array.Empty<BgfAnimationEvent>() };

	private static Engine_BgfDocument Document()
	{
		var doc = new Engine_BgfDocument
		{
			Elements = new[]
			{
				new BgfElement { ParentOrdinal = -1, Flags = 1 },
				new BgfElement { ParentOrdinal = 0, Flags = 1 },
			},
			Slides = new[]
			{
				new BgfSlide { Name = "Master Slide", OwnerElement = 0, Entries = new[] { Entry(0, false), Entry(1, false) } },
				new BgfSlide { Name = "Slide1", OwnerElement = 0, Entries = new[] { Entry(0, true), Entry(1, false) } },
				new BgfSlide { Name = "Master Slide", OwnerElement = 1, Entries = new[] { Entry(1, false) } },
				new BgfSlide { Name = "Slide1", OwnerElement = 1, Entries = new[] { Entry(1, true) } },
			},
		};
		return doc;
	}

	private static BgfSlide? First(int owner, BgfSlide[] owned)
	{
		foreach (BgfSlide slide in owned)
		{
			if (!slide.IsMaster)
			{
				return slide;
			}
		}

		return null;
	}

	[TestCase]
	public void DocumentOrderLetsTheChildsSelfEntryWin()
	{
		BgfSlideState state = Document().ComputeSlideState(First, _ => 0);
		AssertThat(state.Active[1]).IsTrue();
	}

	[TestCase]
	public void AncestorsLastLetsTheParentsGateHoldAtRest()
	{
		BgfSlideState state = Document().ComputeSlideState(First, _ => 0, ancestorsLast: true);
		AssertThat(state.Active[0]).IsTrue();
		AssertThat(state.Active[1]).IsFalse();
	}

	[TestCase]
	public void ALaterChildChangeStillWinsUnderAncestorsLast()
	{
		BgfSlideState state = Document().ComputeSlideState(First, owner => owner == 1 ? 5 : 0, ancestorsLast: true);
		AssertThat(state.Active[1]).IsTrue();
	}
}

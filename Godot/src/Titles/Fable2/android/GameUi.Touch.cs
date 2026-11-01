using Godot;
using Refii.Core;
using Refii.Ui;
using Refii.Host;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// What the touch controls read off the HUD: the expression wheel's open flag and its picks, for
/// a latched Emotes to let go on (AndroidTouchWheelRelease), and where the HUD draws its item
/// slots, for the item buttons to stand over. The wheel pump in GameUi.cs feeds these through
/// <see cref="FollowTouchHud"/>, <see cref="CountWheelPick"/> and <see cref="_wheelInGroup"/>.
/// </summary>
public partial class GameUi
{
	/// <summary>
	/// Counts the expression wheel's picks that perform something — an expression or a dog
	/// command, not a group entered or left. The touch controls let a latched Emotes go on each.
	/// </summary>
	/// <remarks>
	/// THE WHEEL IS TWO LEVELS: the top holds the groups (Social, Fun, Dog…) beside Turn Safety
	/// and Close, which close the wheel themselves; each group holds its actions and Back, at the
	/// south (sector 7). So a select inside a group on anything but Back performs; a select at the
	/// top enters a group (or closes the wheel); Back and B return to the top.
	/// </remarks>
	public static int ExpressionWheelPicks { get; private set; }

	/// <summary>Whether the open wheel stands in a group rather than at its top level.</summary>
	private bool _wheelInGroup;

	/// <summary>The wheel's sector the stated Back stands in, inside a group: south.</summary>
	private const int WheelBackSector = 7;

	/// <summary>Follows the wheel's level through one select on <paramref name="sector"/>, counting a pick that performs.</summary>
	private void CountWheelPick(int sector)
	{
		(_wheelInGroup, bool picked) = WheelSelectStep(_wheelInGroup, sector);
		if (picked)
		{
			++ExpressionWheelPicks;
		}
	}

	/// <summary>One select on the two-level wheel: the level after it, and whether it performed.</summary>
	/// <param name="inGroup">Whether the wheel stood in a group before the select.</param>
	/// <param name="sector">The sector selected, −1 when none is pointed.</param>
	internal static (bool InGroup, bool Picked) WheelSelectStep(bool inGroup, int sector) =>
		sector < 0 ? (inGroup, false)
		: !inGroup ? (true, false)
		: sector == WheelBackSector ? (false, false)
		: (true, true);

	/// <summary>
	/// Whether the guest states its expression wheel open while the wheel's button is held. Close,
	/// Turn Safety and backing out of the top level close it; the touch controls let a latched
	/// Emotes go on that. False while the button is up.
	/// </summary>
	public static bool ExpressionWheelOpen { get; private set; }

	/// <summary>
	/// What the touch controls follow on the HUD each frame it is drawn: the guest's wheel flag
	/// (<see cref="ExpressionWheelOpen"/>) and, on the phone, the item slots (<see cref="HudItemSlots"/>).
	/// </summary>
	private void FollowTouchHud()
	{
		ExpressionWheelOpen = Fable2Input.QuickWheelHeld && GuestExpressionWheelOpen();
		if (Engine_Platform.IsAndroid)
		{
			FollowItemSlots();
		}
	}

	/// <summary>
	/// The guest's own open flag, as the pump's QuickWheel lever reads it: the quick menu
	/// `*(GUI+28)`, its byte +35 (+34 unpatched, `GuiOwnerField`). The HUD's drawn quads are no
	/// answer — the closed wheel's art stays drawn under the guest's element state.
	/// </summary>
	private static bool GuestExpressionWheelOpen()
	{
		System.Span<uint> word = stackalloc uint[1];
		uint at = HostBridge.GuestAddress(0x83496B08u);
		if (at == 0u || HostBridge.Peek(at, word) != 1 || word[0] < 0x10000u)
		{
			return false;
		}

		if (HostBridge.Peek(word[0] + 28u, word) != 1 || word[0] < 0x10000u)
		{
			return false;
		}

		uint field = HostBridge.PickByBuild(35u, 34u);
		if (HostBridge.Peek(word[0] + 32u, word) != 1)
		{
			return false;
		}

		// Guest words are big-endian: byte +32 is the word's top byte, +35 its bottom.
		return ((word[0] >> (int)((35u - field) * 8u)) & 0xFFu) != 0u;
	}

	/// <summary>
	/// Where the HUD draws its four quick-item slots (the D-pad's items), in UI canvas units (Y
	/// up, origin at the centre): the cross's centre and the distance from it to a slot. Null
	/// until the slots have been drawn once; kept while the HUD hides them, since they do not
	/// move. The touch controls put their item buttons over these, so a finger taps the item it sees.
	/// </summary>
	public static (Vector2 Centre, float Reach)? HudItemSlots { get; private set; }

	/// <summary>The four slot pieces of the HUD's item D-pad (Engine_HudLayout), as node names.</summary>
	private static readonly string[] ItemSlotNodes =
	{
		"slidetoggle_DPadUp_up", "slidetoggle_DPadDown_down", "slidetoggle_DPadLeft_left", "slidetoggle_DPadRight_right",
	};

	private readonly Node3D?[] _itemSlotNodes = new Node3D?[4];
	private int _itemSlotSearchIn;

	/// <summary>Re-reads <see cref="HudItemSlots"/> from the drawn slots; cheap once the nodes are found, and allocates nothing.</summary>
	private void FollowItemSlots()
	{
		Vector2 sum = Vector2.Zero;
		System.Span<Vector2> centres = stackalloc Vector2[4];
		for (int i = 0; i < 4; ++i)
		{
			if (_itemSlotNodes[i] is not { } node || !GodotObject.IsInstanceValid(node))
			{
				// A missing slot is looked for every 60 frames (one to two seconds), not every
				// frame: the walk covers the whole HUD tree.
				if (--_itemSlotSearchIn > 0)
				{
					return;
				}

				_itemSlotSearchIn = 60;
				node = Engine_UiTheme.FindByName(_hud!, ItemSlotNodes[i]) as Node3D;
				_itemSlotNodes[i] = node;
			}

			if (node is null || !node.IsVisibleInTree() || Engine_UiPointer.CanvasBounds(node) is not { } box)
			{
				return;
			}

			centres[i] = box.GetCenter();
			sum += centres[i];
		}

		Vector2 centre = sum / 4f;
		float reach = 0f;
		foreach (Vector2 slot in centres)
		{
			reach += slot.DistanceTo(centre) / 4f;
		}

		HudItemSlots = (centre, reach);
	}
}

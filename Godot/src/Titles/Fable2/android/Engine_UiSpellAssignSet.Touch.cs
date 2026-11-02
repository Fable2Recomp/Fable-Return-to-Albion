using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The hero's spells as the touch controls' Spells button and its picker read them, published by
/// <see cref="Follow"/> each frame the widgets are revealed.
/// </summary>
public sealed partial class Engine_UiSpellAssignSet
{
	/// <summary>
	/// Whether the guest would open its spells menu now: it refuses below two spell types awarded
	/// and purchased at power level 0 (`sub_826B4448`, see <see cref="Follow"/>). True while the
	/// state is unknown, so nothing is hidden on a guess. The touch controls show their Spells
	/// button only while this holds — a hero with one spell casts it with B and has nothing to choose.
	/// </summary>
	public static bool SpellsMenuCanOpen => HeroSpellTypes is not { } types || types >= 2;

	/// <summary>
	/// Set by the touch controls while a hold on their Spells button is the right trigger: the
	/// trigger draws in experience orbs and opens the game's spell wheel, and under a touch hold the
	/// wheel is not drawn (the touch picker is the way to choose). A controller's trigger is untouched.
	/// </summary>
	public static bool TouchHidesWheel { get; set; }

	/// <summary>Whether the guest has either player's spell wheel open, as last sampled.</summary>
	public static bool GuestWheelOpen { get; private set; }

	/// <summary>The spell types the hero can cast at power level 0, or null while unknown.</summary>
	public static int? HeroSpellTypes { get; private set; }

	/// <summary>
	/// This frame's spell slots, as the selectors read them: what each power level holds and may
	/// hold. Not <c>Known</c> until the hero's spell manager has been sampled.
	/// </summary>
	public static Engine_UiModel.SpellAssignState HeroSpells { get; internal set; }

	/// <summary>The host the selectors take their art from, for <see cref="WillIcon"/> and <see cref="WillName"/>.</summary>
	private static Engine_UiPresentationHost? _artHost;

	/// <summary>A will's icon, the selectors' own (`art\gui\wheelicons\abilities`), or null before the art loads.</summary>
	public static Texture2D? WillIcon(int will) =>
		will >= 0 && will < Engine_UiScreenConstants.Wills.Length && _artHost is { } host
			? host.LoadTexture(Engine_UiScreenConstants.WillIconFolder + Engine_UiScreenConstants.Wills[will].Icon + ".tex")
			: null;

	/// <summary>A will's name in the game's text, as the selectors label it; empty before the text loads.</summary>
	public static string WillName(int will) =>
		will >= 0 && will < Engine_UiScreenConstants.Wills.Length &&
		_artHost?.PatternBookFor(WheelPresentation) is { } book
			? book.Resolve(Engine_UiScreenConstants.Wills[will].Tag)
			: string.Empty;

	/// <summary>
	/// Puts one will in every power level that offers it, as A does on the selectors (the slot's
	/// will carried down into every later slot that offers it): B then casts it however far it is
	/// charged. Each slot goes through the guest's own assignment, so the game saves it as a choice.
	/// </summary>
	/// <returns>How many slots changed.</returns>
	public static int ChooseForEveryLevel(int will, int player = 1)
	{
		Engine_UiModel.SpellAssignState spells = HeroSpells;
		int changed = 0;
		for (int slot = 0; spells.Known && slot < Engine_UiModel.SpellAssignState.Slots; ++slot)
		{
			if (spells.Offers(slot, will) && spells.On(slot) != will)
			{
				Engine_UiSource.Current.AssignSpell(slot, will, player);
				++changed;
			}
		}

		return changed;
	}

	/// <summary>Publishes this frame's sample (<see cref="_spells"/>) and art host for the statics above.</summary>
	private void PublishForTouch()
	{
		HeroSpellTypes = _spells.Known ? _spells.Choices(0).Count : null;
		HeroSpells = _spells;
		GuestWheelOpen = _spells.WheelOpen || _spells.WheelOpenPlayerTwo;
		_artHost = Host;
	}
}

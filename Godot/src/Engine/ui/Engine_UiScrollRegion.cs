namespace Refii.Ui;

/// <summary>
/// THE SCROLL REGION'S STATE — one offset for every scrolled text region: the owned books'
/// (<see cref="Engine_UiOwnedBook"/>, the template's <c>ScrollContent</c>) and the host box's
/// paper (<see cref="Engine_UiPaperBox"/>). The stick slides at the guest's rate
/// (`sub_828C30A8` feeds ~3 canvas units per 30 Hz tick at full stick, ~100/s; a little faster
/// reads better at 60), the wheel and the d-pad in notches, the bar to a fraction; every input
/// is held until the owner's next <see cref="Step"/>, the one place the offset moves, and the
/// offset is clamped to the overflow the owner measured. No Godot type: the arithmetic is the
/// contract.
/// </summary>
public sealed class Engine_UiScrollRegion
{
	/// <summary>Canvas units a full stick scrolls per second.</summary>
	public const float UnitsPerSecond = 150f;

	/// <summary>Canvas units one wheel notch, or one d-pad step, scrolls.</summary>
	public const float UnitsPerNotch = 45f;

	/// <summary>Canvas units the content is slid up from its laid-out place; 0 at the top.</summary>
	public float Offset { get; private set; }

	/// <summary>Canvas units of overflow the last step measured; 0 = fits.</summary>
	public float Range { get; private set; }

	/// <summary>Whether there is anything to scroll (an overflow a pixel can show).</summary>
	public bool Scrollable => Range > 1f;

	/// <summary>Where the view stands, 0 at the top, 1 at the bottom; 0 when nothing scrolls.</summary>
	public float Fraction => Range > 0f ? Offset / Range : 0f;

	private float _pendingUnits;

	private float? _scrollTo;

	/// <summary>Wheel notches, positive down; held until the next step.</summary>
	public void AddWheel(int notches) => _pendingUnits += notches * UnitsPerNotch;

	/// <summary>Scrolls to a 0..1 position on the next step — the bar's click or drag.</summary>
	public void ScrollTo(float fraction) => _scrollTo = System.Math.Clamp(fraction, 0f, 1f);

	/// <summary>Back to the top, keeping any input held for the next step — a new region or key.</summary>
	public void Rewind() => Offset = 0f;

	/// <summary>Everything dropped — the region left the screen; the next showing starts at the top.</summary>
	public void Reset()
	{
		Offset = 0f;
		Range = 0f;
		_pendingUnits = 0f;
		_scrollTo = null;
	}

	/// <summary>
	/// One step: the stick (<paramref name="stick"/> up positive, -1..1) over
	/// <paramref name="delta"/> seconds, the held notches, and a held bar position, against the
	/// overflow <paramref name="range"/> the owner measured this frame. Returns the new offset.
	/// </summary>
	public float Step(float stick, double delta, float range)
	{
		Range = System.Math.Max(0f, range);
		if (_scrollTo is { } to)
		{
			Offset = to * Range;
			_scrollTo = null;
		}

		Offset = System.Math.Clamp(
			Offset - (stick * UnitsPerSecond * (float)delta) + _pendingUnits, 0f, Range);
		_pendingUnits = 0f;
		return Offset;
	}
}

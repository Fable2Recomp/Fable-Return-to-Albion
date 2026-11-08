using Godot;

namespace Refii.Ui;

/// <summary>
/// A GROUP of a mod's HUD pieces moved and sized as one by values: <c>ui/hud.json</c>'s
/// <c>"groups"</c> (group id → record) and a piece's <c>"group"</c> naming one.
/// </summary>
/// <remarks>
/// <code>
/// "groups": { "hotbar.bar": { "anchor": [0.5, 1], "pivot": [0, -12],
///   "scale": "setting:hotbar.size", "offset": [0, "-setting:hotbar.lift"] } }
/// </code>
/// <list type="bullet">
/// <item><c>anchor</c> + <c>pivot</c> — the point the group scales about, placed as a piece's
/// point is (<see cref="Engine_HudLayout.Place"/>): a screen fraction plus reference units,
/// +Y down.</item>
/// <item><c>scale</c> — a number or a value source (<see cref="Engine_UiValueSources"/>,
/// <c>setting:</c> among them): every member's point is moved that factor away from the pivot
/// and drawn that factor larger. Not stated, or not above 0, it is 1.</item>
/// <item><c>offset</c> — <c>[x, y]</c> in reference units (+Y down) added after the scale, each
/// a number or a value source; a leading <c>-</c> on a source negates it (<c>"-setting:m.lift"</c>
/// raises by the setting). Not stated, it is 0.</item>
/// <item><c>touchScale</c> — a number the scale is multiplied by on a touchscreen device (a phone
/// or tablet), whose screen is small and is pressed with a thumb: a bar the right size on a monitor
/// is too small there to see or tap. Not stated, or not above 0, it is 1.</item>
/// </list>
/// <para>Read per frame with the readers of the member's own player, so a group of
/// <c>each</c> pieces in player two's half reads player two's <c>player:</c> values and scales
/// about the pivot placed in that half. Pure; held by tests.</para>
/// </remarks>
public static class Engine_HudModGroups
{
	public const string Section = "groups";

	/// <summary>One group as stated: its pivot and the scale and offset sources.</summary>
	public sealed record Group(
		string Id,
		string Mod,
		Engine_HudLayout.Anchor Anchor,
		Vector2 Pivot,
		string Scale,
		string OffsetX,
		string OffsetY,
		float TouchScale = 1f);

	/// <summary>A group's values now: the factor about the pivot and the offset in reference units.</summary>
	public readonly record struct Transform(float Scale, Vector2 Offset)
	{
		public static readonly Transform Identity = new(1f, Vector2.Zero);
	}

	/// <summary>A group's scale and offset for one reader; an unstated source is the identity's.</summary>
	public static Transform Resolve(Group group, Engine_UiValueSources.Context context)
	{
		float scale = Number(group.Scale, context) is { } stated && stated > 0f && float.IsFinite(stated) ? stated : 1f;
		if (Handheld && group.TouchScale > 0f && float.IsFinite(group.TouchScale))
		{
			scale *= group.TouchScale;
		}

		float x = Number(group.OffsetX, context) is { } ox && float.IsFinite(ox) ? ox : 0f;
		float y = Number(group.OffsetY, context) is { } oy && float.IsFinite(oy) ? oy : 0f;
		return new Transform(scale, new Vector2(x, y));
	}

	/// <summary>
	/// A point in the canvas (centre-origin, +Y up) moved by the group: scaled away from the
	/// pivot placed in <paramref name="extent"/>, then offset by reference units at the layout's scale.
	/// </summary>
	public static Vector2 Apply(Vector2 point, Group group, Transform transform, Vector2 extent)
	{
		Vector2 pivot = Engine_HudLayout.Place(new Engine_HudLayout.Piece(group.Id, string.Empty, group.Anchor, group.Pivot), extent);
		float unit = Engine_HudLayout.Scale(extent);
		return pivot + (point - pivot) * transform.Scale + new Vector2(transform.Offset.X * unit, -transform.Offset.Y * unit);
	}

	/// <summary>Whether <see cref="Group.TouchScale"/> applies: the game runs on a touchscreen device. Set by the platform at start.</summary>
	public static bool Handheld { get; set; }

	/// <summary>A number or a value source, negated by a leading <c>-</c> before a source; null when it states nothing.</summary>
	public static float? Number(string? text, Engine_UiValueSources.Context context)
	{
		string written = (text ?? string.Empty).Trim();
		if (Negated(written) is { } source)
		{
			return Engine_UiValueSources.Number(source, context) is { } value ? -value : null;
		}

		return Engine_UiValueSources.Number(written, context);
	}

	/// <summary>Why a group value cannot be read, or null when it can (the validator's rule).</summary>
	public static string? Problem(string? text) =>
		Engine_UiValueSources.NumberProblem(Negated((text ?? string.Empty).Trim()) ?? text);

	/// <summary>The source after a leading <c>-</c>, or null when the text is not a negated source.</summary>
	private static string? Negated(string written) =>
		written.Length > 1 && written[0] == '-' && Engine_UiValueSources.LooksLikeSource(written[1..]) ? written[1..] : null;
}

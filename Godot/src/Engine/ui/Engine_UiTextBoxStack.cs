namespace Refii.Ui;

/// <summary>
/// A LAID-OUT TEXT BOX'S LINES, each line element's local position from the lines the guest
/// states and the sizes Godot measures them at. Pure — no Godot type — so the tests hold it
/// against the shipped files.
/// </summary>
/// <remarks>
/// <para>The contract is Fable 3's fit (`sub_82920278` → `sub_82920610`, 360 disc). The box's
/// lines are the vector its caller passes `sub_82920BC8` (`@Hud.Lines`), in order. Each line is
/// measured (`sub_82920830` / `sub_82920958` → `sub_82D40300`): its height, an int, is kept at
/// line+12 (0x82920484) and summed (0x829204B8), its width maxed (0x829204C4). The box takes
/// the widest line clamped to the box's [min.x, max.x] and the summed height clamped to
/// [min.y, max.y] (the `fsel` pairs 0x82920540..0x829205A0) plus the caller's pad (0x829205B8):
/// box+12 = w, box+16 = h (0x829205C0). Line 0 stands at
/// <c>y = h · (0.5 − pivot.y) + pos.y</c> (0x82920720, <see cref="Engine_UiTextBoxFit.FirstLineY"/>)
/// and <c>x = pos.x + 37</c> (0x8292072C, flt_8206E448), or <c>x = pos.x − w / 2</c> for the
/// box the fit centres (byte +9, set for `Subtitles` alone: 0x82920C3C against
/// `dword_8355BF00` = FNV-1 "Subtitles"). Every line element filed under the line's TextName
/// is written there at z −1 (f29 = flt_82070544, GuiElement_SetPosition at 0x829207DC), and
/// the next line drops by this line's measured height (0x829207F0..0x82920814) whether or not
/// any element is filed under it. An empty line measures 0 and drops nothing.</para>
///
/// <para>Coordinates are the .bgf's: +Y up, a line element's position its top-left.</para>
/// </remarks>
public static class Engine_UiTextBoxStack
{
	/// <summary>A non-centred box's lines stand this far right of its summed position (flt_8206E448).</summary>
	public const float LeftInset = 37f;

	/// <summary>The z every line is written at (flt_82070544, f3 at 0x829207CC).</summary>
	public const float LineZ = -1f;

	/// <summary>One axis's range, the box element's size with its meter at 0 and at 100.</summary>
	public readonly record struct Range(float Min, float Max)
	{
		/// <summary>The fit's clamp: below the minimum takes the minimum, then above the maximum the maximum.</summary>
		public float Clamp(float value)
		{
			float atLeast = value - Min >= 0f ? value : Min;
			return atLeast - Max >= 0f ? Max : atLeast;
		}
	}

	/// <summary>
	/// A box the fit lays: the name the BoxName registry files it under, whether its lines are
	/// centred on its position, its size ranges and the pad its caller adds to the height.
	/// </summary>
	public readonly record struct Box(string Name, bool Centred, Range Width, Range Height, float Pad);

	/// <summary>A line as measured: its widest row and its block height.</summary>
	public readonly record struct Measured(float Width, float Height);

	/// <summary>The box's fitted width and height (box+12, box+16).</summary>
	public static (float Width, float Height) Fitted(Box box, System.Collections.Generic.IReadOnlyList<Measured> lines)
	{
		float widest = 0f;
		float tall = 0f;
		foreach (Measured line in lines)
		{
			widest = System.Math.Max(widest, Whole(line.Width));
			tall += Whole(line.Height);
		}

		return (box.Width.Clamp(widest), box.Height.Clamp(tall) + box.Pad);
	}

	/// <summary>
	/// Each line's local (x, y), in the lines' order; null when the document files no element
	/// under the box's name.
	/// </summary>
	/// <param name="stated">A position a script states on a chain element in place of the file's.</param>
	public static (float X, float Y)[]? Place(
		Engine_BgfDocument document, Box box, System.Collections.Generic.IReadOnlyList<Measured> lines,
		System.Func<int, (float? X, float? Y)>? stated = null)
	{
		int element = Engine_UiTextBoxFit.BoxElement(document, box.Name);
		if (element < 0)
		{
			return null;
		}

		(float width, float height) = Fitted(box, lines);
		(float x, float _) = Engine_UiTextBoxFit.ChainPosition(document, element, stated);
		float y = Engine_UiTextBoxFit.FirstLineY(document, box.Name, height, stated)!.Value;
		x = box.Centred ? x - (width * 0.5f) : x + LeftInset;

		var placed = new (float X, float Y)[lines.Count];
		for (int i = 0; i < lines.Count; ++i)
		{
			placed[i] = (x, y);
			y -= Whole(lines[i].Height);
		}

		return placed;
	}

	/// <summary>The measure's int (sub_82D40300 stores the extents as words; read back with `extsw`/`fcfid`, 0x829207FC).</summary>
	private static float Whole(float value) => (int)value;
}

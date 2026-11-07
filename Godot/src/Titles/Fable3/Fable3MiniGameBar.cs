using System.Collections.Generic;

namespace Refii.Titles.Fable3;

/// <summary>
/// THE MINI-GAME BAR'S LAYOUT, as `art/gui/gameface/minigamebarcontrol.lua` (format 2, the
/// `MiniGameBarControl` behaviour on `interactionmode.bgf` `Layer.Bar`) performs it — the one
/// shipped presentation script whose layout Godot has to reproduce. The bar is the relationship
/// and podium mini-game (`RelationshipHero.Start` -> sub_825C5A60, `g_MiniGameBar:Show()` at
/// 0x825C5AF8). Its inputs are the guest's calls: the Parameters stagers (`@Lua.Param`) and the
/// chunk the C++ runs (`@Lua.Chunk`), headless_presentation_hooks.cpp:
/// <list type="bullet">
/// <item>SpawnButton (sub_826EC6A0): Float1 = start + (length - 4) / 2 / 620 (0x826EC710, stager
/// 0x826EC724), Float2 = (length - 4) × 0.01 (0x826EC754), Float3 = the segment index (0x826EC790),
/// String1 = the colour (sub_826E99F8, 0x826EC7CC), chunk 0x826EC7FC.</item>
/// <item>RemoveButton (sub_826EAA78): Float1 = index (0x826EAAD0), chunk 0x826EAAF8.</item>
/// <item>SetButtonHitLevel / SetButtonHitFinal (sub_826EB3E0): Float1 index, Float2 level
/// (0x826EB58C / 0x826EB5C8), chunks 0x826EB6BC / 0x826EB5FC.</item>
/// <item>SetBackground (sub_826EAB20): String1 = a HitHighlights slide name (0x826EAB90), chunk
/// 0x826EABB8. SetPipTravel(t) (sub_826EABE8): the pip fraction formatted into the chunk, 0x826EB228.</item>
/// <item>Show (sub_825C5A60, 0x825C5AF8) / Hide (sub_826ECAA0, 0x826ECAE8).</item>
/// </list>
/// The rule, from the script: segment x = LeftMostButtonX + Float1 × Length, scale.x = Float2,
/// opacity 100; a removed or cleared segment goes to opacity 0 at x −1900; hit level opacity
/// 100 − level × 10, hit final 20; the pip x = PipStartX + t × PipMovePerSecond (PipMovesViaTime is
/// false). Each colour's table starts with the authored template (`StoreButtons`) and CLONES the
/// template when every entry is in use. Black's table gains the template again on every Show (the
/// script has no empty check there), so its entries can share one element — kept as written.
/// No Godot types: the view side is <see cref="Fable3MiniGameBarClones"/>.
/// </summary>
public sealed class Fable3MiniGameBar
{
	public const float LeftMostButtonX = -310f;
	public const float RightMostButtonX = 310f;
	public const float Length = RightMostButtonX - LeftMostButtonX;
	public const float PipStartX = 0f;
	public const float PipMovePerSecond = 67.8f;
	public const float ParkedX = -1900f;
	public const int ShowSlide = 2;
	public const int HideSlide = 1;

	public const string BarPath = "Layer.Bar";
	public const string ButtonsPath = "Layer.Bar.Elements.Buttons";
	public const string PipPath = "Layer.Bar.Elements.pip";
	public const string HighlightsPath = "Layer.Bar.Elements.HitHighlights";

	/// <summary>The colour tables in the script's own order of declaration.</summary>
	public enum Colour { A, B, X, Y, Black }

	/// <summary>Each colour's template element under <see cref="ButtonsPath"/>.</summary>
	public static string TemplateName(Colour colour) => colour switch
	{
		Colour.A => "ButtonA",
		Colour.B => "ButtonB",
		Colour.X => "ButtonX",
		Colour.Y => "ButtonY",
		_ => "ButtonBlack",
	};

	/// <summary>`SpawnButton`'s String1 to table: G A, R B, B X, Y Y, Bl Black; anything else is none.</summary>
	public static Colour? ColourOf(string code) => code switch
	{
		"G" => Colour.A,
		"R" => Colour.B,
		"B" => Colour.X,
		"Y" => Colour.Y,
		"Bl" => Colour.Black,
		_ => null,
	};

	/// <summary>
	/// One element the script writes: a colour's template (Copy 0) or a clone of it. Null channels
	/// are unwritten — the element keeps its authored value.
	/// </summary>
	public sealed class Element
	{
		public required Colour Colour { get; init; }
		public required int Copy { get; init; }
		public float? X { get; set; }
		public float? ScaleX { get; set; }
		public float? Opacity { get; set; }
	}

	private sealed class Entry
	{
		public required Element Element { get; init; }
		public bool Used { get; set; }
		public float Index { get; set; } = -1f;
	}

	private readonly List<Entry>[] _tables =
	{
		new(), new(), new(), new(), new(),
	};

	private readonly List<Element> _elements = new();
	private readonly Element?[] _templates = new Element?[5];

	/// <summary>Every element the script has written, templates first in colour order, then clones as made.</summary>
	public IReadOnlyList<Element> Elements => _elements;

	/// <summary>`self.Element` is set (Show ran): Hide and the pip write only after it.</summary>
	public bool Found { get; private set; }

	/// <summary>The bar's slide as the script last set it, null before any.</summary>
	public int? BarSlide { get; private set; }

	/// <summary>The HitHighlights slide NAME the script last went to, null before any.</summary>
	public string? Background { get; private set; }

	/// <summary>The pip's x as the script last wrote it, null before any.</summary>
	public float? PipX { get; private set; }

	/// <summary>Bumped on every write, for the document's slide stamps.</summary>
	public ulong Version { get; private set; }

	private Element Template(Colour colour)
	{
		if (_templates[(int)colour] is { } template)
		{
			return template;
		}

		var made = new Element { Colour = colour, Copy = 0 };
		_templates[(int)colour] = made;
		_elements.Add(made);
		return made;
	}

	/// <summary>
	/// One chunk the guest ran, `g_MiniGameBar:Name(arg)`, with the presentation's staged
	/// parameters; false when it is not this script's.
	/// </summary>
	public bool Run(string chunk, IReadOnlyDictionary<string, Fable3LuaParameter> parameters)
	{
		const string receiver = "g_MiniGameBar:";
		string text = chunk.Trim();
		if (!text.StartsWith(receiver, System.StringComparison.Ordinal))
		{
			return false;
		}

		int open = text.IndexOf('(');
		int close = text.LastIndexOf(')');
		if (open < 0 || close < open)
		{
			return false;
		}

		string method = text[receiver.Length..open];
		string argument = text[(open + 1)..close].Trim();
		float? number = float.TryParse(argument, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? parsed : null;

		float? Float(string name) =>
			parameters.TryGetValue(name, out Fable3LuaParameter value) ? value.Number : null;
		string? Text(string name) =>
			parameters.TryGetValue(name, out Fable3LuaParameter value) ? value.Text : null;

		switch (method)
		{
			case "Show":
				Show();
				break;
			case "Hide":
				if (Found)
				{
					BarSlide = HideSlide;
				}

				break;
			case "SpawnButton":
				if (Text("String1") is { } code && ColourOf(code) is { } colour)
				{
					Spawn(colour, Float("Float1"), Float("Float2"), Float("Float3"));
				}

				break;
			case "RemoveButton":
				if ((number ?? Float("Float1")) is { } removed)
				{
					// A, Y, X, B, then Black — the script's order of tries.
					_ = Remove(Colour.A, removed) || Remove(Colour.Y, removed) || Remove(Colour.X, removed) ||
						Remove(Colour.B, removed) || Remove(Colour.Black, removed);
				}

				break;
			case "SetButtonHitLevel":
				if (Float("Float1") is { } levelIndex && Float("Float2") is { } level)
				{
					SetOpacityOfUsed(levelIndex, 100f - (level * 10f));
				}

				break;
			case "SetButtonHitFinal":
				if (Float("Float1") is { } finalIndex)
				{
					SetOpacityOfUsed(finalIndex, 20f);
				}

				break;
			case "SetBackground":
				// The highlights are found by path, not through Show; nil String1 is a Lua error.
				if (Text("String1") is { } slide)
				{
					Background = slide;
				}

				break;
			case "SetPipTravel":
				if (Found && number is { } travel)
				{
					PipX = PipStartX + (travel * PipMovePerSecond);
				}

				break;
			default:
				return false;
		}

		++Version;
		return true;
	}

	/// <summary>`Show`: StoreButtons, Clear, slide 2 on the bar, the pip found.</summary>
	private void Show()
	{
		Found = true;

		// StoreButtons: A, B, X, Y once each into an empty table (B is gated on A's element, which
		// the file always has); Black's template on every Show.
		foreach (Colour colour in new[] { Colour.A, Colour.B, Colour.X, Colour.Y })
		{
			List<Entry> table = _tables[(int)colour];
			if (table.Count == 0)
			{
				Element template = Template(colour);
				table.Add(new Entry { Element = template });
				template.Opacity = 0f;
			}
		}

		Element black = Template(Colour.Black);
		_tables[(int)Colour.Black].Add(new Entry { Element = black });
		black.Opacity = 0f;

		// Clear: A, X, B, Y, Black.
		foreach (Colour colour in new[] { Colour.A, Colour.X, Colour.B, Colour.Y, Colour.Black })
		{
			foreach (Entry entry in _tables[(int)colour])
			{
				entry.Used = false;
				entry.Index = -1f;
				entry.Element.Opacity = 0f;
				entry.Element.X = ParkedX;
			}
		}

		BarSlide = ShowSlide;
	}

	/// <summary>`SpawnButtonOfType`: the first unused entry, else a clone of the template.</summary>
	private void Spawn(Colour colour, float? position, float? scale, float? index)
	{
		List<Entry> table = _tables[(int)colour];
		if (table.Count == 0)
		{
			return;
		}

		Entry? entry = table.Find(e => !e.Used);
		if (entry is null)
		{
			Element source = table[0].Element;
			var clone = new Element
			{
				Colour = colour,
				Copy = table.Count,
				X = source.X,
				ScaleX = source.ScaleX,
				Opacity = source.Opacity,
			};
			_elements.Add(clone);
			entry = new Entry { Element = clone };
			table.Add(entry);
		}

		entry.Used = true;
		entry.Index = index ?? -1f;

		// `LeftMostButtonX + arg2 * Length` raises on a nil Float1 before any write.
		if (position is not { } at || scale is not { } width)
		{
			return;
		}

		entry.Element.ScaleX = width;
		entry.Element.X = LeftMostButtonX + (at * Length);
		entry.Element.Opacity = 100f;
	}

	private bool Remove(Colour colour, float index)
	{
		foreach (Entry entry in _tables[(int)colour])
		{
			if (entry.Used && entry.Index == index)
			{
				entry.Used = false;
				entry.Index = -1f;
				entry.Element.Opacity = 0f;
				entry.Element.X = ParkedX;
				return true;
			}
		}

		return false;
	}

	/// <summary>The hit writes reach A, B, X and Y — never Black.</summary>
	private void SetOpacityOfUsed(float index, float opacity)
	{
		foreach (Colour colour in new[] { Colour.A, Colour.B, Colour.X, Colour.Y })
		{
			foreach (Entry entry in _tables[(int)colour])
			{
				if (entry.Used && entry.Index == index)
				{
					entry.Element.Opacity = opacity;
				}
			}
		}
	}
}

/// <summary>One staged Parameters value: a string (SetParameterString) or a number (SetParameterFloat).</summary>
public readonly record struct Fable3LuaParameter(string? Text, float? Number);

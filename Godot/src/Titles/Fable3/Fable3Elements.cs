namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// The Gameface elements Fable 3's C++ writes, by presentation and dotted path, as its element
/// hooks state them (Fable3/src/hooks/src/gui_element_hooks.cpp: `@Element.State`,
/// `@Element.Opacity`, `@Element.Text`). The screens' Godot views read their rows from here; the
/// guest's own presentation never renders.
/// </summary>
/// <remarks>
/// Every Fable 3 menu is written this way — there is no `g_Menu:` chunk mechanism — so this model
/// is the whole data path for the options, hero status, stats, quest summary, promises, schedule
/// and interaction screens. A statement arrives only when a value changes; a path is empty when
/// the element was written before the resolver named it (a text box the guest cached).
///
/// The census (`[fable3-elements]`) prints each (presentation, path) the first time it is
/// written, with the value: the vocabulary a screen's view is built against.
/// </remarks>
public static class Fable3Elements
{
	/// <summary>One element's last stated values.</summary>
	public sealed class Element
	{
		public int? State { get; set; }
		public float? Opacity { get; set; }
		public string? Text { get; set; }
		public uint Address { get; set; }
	}

	private static readonly Dictionary<(uint Presentation, string Path), Element> ByPath = new();
	private static readonly Dictionary<uint, Element> ByAddress = new();
	private static readonly HashSet<(uint, string)> Reported = new();
	private const int MaxReported = 600;

	/// <summary>Bumped on every statement, so a view redraws only when something changed.</summary>
	public static ulong Generation { get; private set; }

	/// <summary>The element at a path in a presentation, or null when nothing was stated for it.</summary>
	public static Element? Get(uint presentation, string path) =>
		ByPath.TryGetValue((presentation, path), out Element? element) ? element : null;

	/// <summary>Every stated element of a presentation.</summary>
	public static IEnumerable<KeyValuePair<(uint Presentation, string Path), Element>> Of(uint presentation)
	{
		foreach (KeyValuePair<(uint Presentation, string Path), Element> entry in ByPath)
		{
			if (entry.Key.Presentation == presentation)
			{
				yield return entry;
			}
		}
	}

	/// <summary>Routes the three pseudo-calls into the model.</summary>
	public static void Register(Engine_UiGuiNatives router)
	{
		router.Register("@Element.State", call => Apply(call, (element, value) => element.State = (int)value.Number));
		router.Register("@Element.Opacity", call => Apply(call, (element, value) => element.Opacity = value.Number));
		router.Register("@Element.Text", call => Apply(call, (element, value) => element.Text = value.Text));
	}

	private static void Apply(Engine_UiModel.GuiNativeCall call, System.Action<Element, Engine_UiModel.GuiNativeArgument> set)
	{
		uint presentation = Parse(call.Argument(0).Text);
		uint address = Parse(call.Argument(1).Text);
		string path = call.Argument(2).Text;
		Engine_UiModel.GuiNativeArgument value = call.Argument(3);

		Element? element = null;
		if (address != 0u && ByAddress.TryGetValue(address, out Element? known))
		{
			element = known;
		}

		if (element is null && path.Length > 0 && ByPath.TryGetValue((presentation, path), out Element? named))
		{
			element = named;
		}

		if (element is null)
		{
			element = new Element { Address = address };
			if (address != 0u)
			{
				ByAddress[address] = element;
			}
		}

		if (path.Length > 0)
		{
			ByPath[(presentation, path)] = element;
		}

		set(element, value);
		++Generation;

		if (Reported.Count < MaxReported && Reported.Add((presentation, path.Length > 0 ? path : address.ToString("X8"))))
		{
			string shown = value.Kind == Engine_UiModel.GuiNativeArgumentKind.String ? $"'{value.Text}'" : value.Text;
			GD.Print($"[fable3-elements] {presentation:X8} {(path.Length > 0 ? path : "<unnamed " + address.ToString("X8") + ">")} {call.Name[9..]} = {shown}");
		}
	}

	private static uint Parse(string hex) =>
		uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value) ? value : 0u;
}

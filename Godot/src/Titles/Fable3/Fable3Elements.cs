namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// The Gameface elements Fable 3's C++ writes, by presentation and dotted path, as its element
/// hooks state them (Fable3/src/hooks/src/gui_element_hooks.cpp: `@Element.State`,
/// `@Element.Opacity`, `@Element.Text`, `@Element.Time`). The screens' Godot views read their rows from here; the
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

		/// <summary>The element's timeline position as the guest passed it (a meter's fraction × 1000).</summary>
		public float? Time { get; set; }

		/// <summary>
		/// The element's local `position.x/.y/.z` as the guest last wrote them (sub_82818580): the
		/// .bgf's own units, null until written.
		/// </summary>
		public (float X, float Y, float Z)? Position { get; set; }

		/// <summary>The texture path the guest last set on the element (sub_82818EF0), null until set.</summary>
		public string? Texture { get; set; }

		/// <summary>
		/// The element's `active` flag as Lua `setAttribute(elem, 'active', b)` set it (flag 0x2000
		/// through the store's vt[20], sub_82C24A70 at 0x82c24c3c), null until set.
		/// </summary>
		public bool? Active { get; set; }

		/// <summary>
		/// Every other attribute a Gameface write set by name — Lua `setAttribute` (`position.y`,
		/// `textcolor.r`, `boxwidth`, …) or a .bgf action's COMMAND_SETPROPERTY — as last stated.
		/// </summary>
		public Dictionary<string, Engine_UiModel.GuiNativeArgument> Attributes { get; } = new(System.StringComparer.Ordinal);

		public uint Address { get; set; }

		/// <summary>The <see cref="Generation"/> of the element's latest statement: which of two statements is the later.</summary>
		public ulong Stamp { get; set; }

		/// <summary>
		/// Hidden by the visibility toggle sub_82819AE0, which scales the opacity across
		/// 0.09765625 (flt_832A18DC): below it the element is off.
		/// </summary>
		public bool Hidden => Opacity is { } opacity && opacity <= HiddenOpacity;
	}

	/// <summary>The visibility toggle's threshold, flt_832A18DC (sub_82819AE0 at 0x82819B38).</summary>
	public const float HiddenOpacity = 0.09765625f;

	private static readonly Dictionary<(uint Presentation, string Path), Element> ByPath = new();
	private static readonly Dictionary<uint, Element> ByAddress = new();
	private static readonly HashSet<(uint, string)> Reported = new();
	private const int MaxReported = 600;

	/// <summary>Bumped on every statement, so a view redraws only when something changed.</summary>
	public static ulong Generation { get; private set; }

	/// <summary>
	/// The HUD's presentation: the one whose elements are resolved under the `Layer` root
	/// (HUD.bgf; the resolver's root for its paths), 0 until one is stated.
	/// </summary>
	public static uint HudPresentation { get; private set; }

	/// <summary>
	/// THE HUD IS HIDDEN BY ITS OWN ROOT'S STATE. The HUD screen's tick sub_825C3A78 writes the
	/// presentation root (`Scene`, sub_82819DB8) to state 2 while a box-manager screen shows
	/// (sub_82277308 at 0x825c3d20 -> sub_8227DA00(root, pres, 2) at 0x825c3d5c), while the save
	/// menu is open or the front box is of type 11/12/13/20 (0x825c3c70..0x825c3ec8), and back to
	/// 1 when that clears (0x825c3d8c, 0x825c3d10); the Options box's Show does the same from the
	/// script route (sub_825C1CB0 at 0x825c9904). Every HUD-level surface follows this.
	/// </summary>
	public static bool HudHidden => HudPresentation != 0u && Get(HudPresentation, "Scene") is { State: 2 };

	/// <summary>
	/// The presentation that most recently stated an element under each root segment. A screen
	/// box builds a NEW presentation every time it opens (r7: `Options` under 451F65D0, then
	/// 44A82F80), and the old one's elements stay in this model, so a view reads the root's
	/// latest presentation rather than any presentation that ever wrote the path.
	/// </summary>
	private static readonly Dictionary<string, uint> LatestByRoot = new();

	/// <summary>The presentation that last wrote under <paramref name="root"/>, or 0.</summary>
	public static uint LatestPresentationOf(string root) =>
		LatestByRoot.TryGetValue(root, out uint presentation) ? presentation : 0u;

	/// <summary>The element at a path in the root's latest presentation, or null.</summary>
	public static Element? Latest(string path)
	{
		int dot = path.IndexOf('.');
		uint presentation = LatestPresentationOf(dot < 0 ? path : path[..dot]);
		return presentation == 0u ? null : Get(presentation, path);
	}

	/// <summary>The element at a path in a presentation, or null when nothing was stated for it.</summary>
	public static Element? Get(uint presentation, string path) =>
		ByPath.TryGetValue((presentation, path), out Element? element) ? element : null;

	/// <summary>Every stated element with a path, across presentations.</summary>
	public static IEnumerable<KeyValuePair<(uint Presentation, string Path), Element>> All => ByPath;

	/// <summary>The first stated element whose path satisfies <paramref name="match"/>, or null.</summary>
	public static Element? Find(System.Func<string, bool> match)
	{
		foreach (KeyValuePair<(uint Presentation, string Path), Element> entry in ByPath)
		{
			if (match(entry.Key.Path))
			{
				return entry.Value;
			}
		}

		return null;
	}

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

	/// <summary>Routes the element pseudo-calls into the model.</summary>
	public static void Register(Engine_UiGuiNatives router)
	{
		router.Register("@Element.State", call => Apply(call, (element, value) => element.State = (int)value.Number));
		router.Register("@Element.Opacity", call => Apply(call, (element, value) => element.Opacity = value.Number));
		router.Register("@Element.Text", call => Apply(call, (element, value) => element.Text = value.Text));
		router.Register("@Element.Time", call => Apply(call, (element, value) => element.Time = value.Number));
		router.Register("@Element.Texture", call => Apply(call, (element, value) => element.Texture = value.Text));
		router.Register("@Element.Position", call => Apply(call, (element, value) =>
			element.Position = (value.Number, call.Argument(4).Number, call.Argument(5).Number)));
		router.Register("@Element.Active", call => Apply(call, (element, value) => element.Active = value.Boolean));
		// (pres, elem, path, name, value): the name in argument 3, the value after it.
		router.Register("@Element.Attribute", call => Apply(call, (element, name) =>
			element.Attributes[name.Text] = call.Argument(4)));
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
			int dot = path.IndexOf('.');
			LatestByRoot[dot < 0 ? path : path[..dot]] = presentation;
		}

		set(element, value);
		element.Stamp = ++Generation;

		if (HudPresentation == 0u && path.StartsWith("Layer.", System.StringComparison.Ordinal))
		{
			HudPresentation = presentation;
		}

		// The HUD root's state is the HUD's visibility (HudHidden): said on EVERY change, not first-seen.
		if (path == "Scene" && presentation == HudPresentation && call.Name == "@Element.State")
		{
			GD.Print($"[fable3-elements] HUD root state = {value.Text} ({(element.State == 2 ? "hidden" : "shown")})");
		}

		if (Reported.Count < MaxReported && Reported.Add((presentation, path.Length > 0 ? path : address.ToString("X8"))))
		{
			string shown = value.Kind == Engine_UiModel.GuiNativeArgumentKind.String ? $"'{value.Text}'" : value.Text;
			GD.Print($"[fable3-elements] {presentation:X8} {(path.Length > 0 ? path : "<unnamed " + address.ToString("X8") + ">")} {call.Name[9..]} = {shown}");
		}
	}

	private static uint Parse(string hex) =>
		uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value) ? value : 0u;
}

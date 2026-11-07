namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// The Gameface elements Fable 3's C++ writes, by DOCUMENT and dotted path, as its hooks state
/// them. The screens' Godot views read their rows from here; the guest's own presentation never
/// renders.
/// </summary>
/// <remarks>
/// <para>KEYED BY DOCUMENT, NOT BY PRESENTATION. Every .bgf is loaded through one function,
/// sub_825C7A90(slot, name), and ui_stated_hooks.cpp states each load as
/// `@Presentation.Load(pres, file)`: a statement's presentation is turned into its document
/// (`hud`, `interactionmode`, `options`, …) the moment it lands, and a view asks for its
/// document — never for an address it would have to wait for. A reload of a document forgets
/// what its previous presentation was told. The path is the file's own (the resolver's spelling,
/// joined to the document's ordinals by <see cref="Fable3HudStatements"/>).</para>
///
/// <para>STATED, THEN SAMPLED. A writer's value as its caller passed it, stated at the writer's
/// entry (`@Ui.Text`, `@Ui.TextTag`, `@Ui.TextName`, `@Ui.Visible`; ui_stated_hooks.cpp and
/// gui_element_hooks.cpp), is what an element shows. The reads the element hooks make after the
/// guest committed — the text box's string (`@Element.Text`) and the opacity the visibility toggle
/// derived from a read-back (`@Element.ToggleOpacity`) — exist only while Gameface builds the
/// element; they are kept as the CROSS-CHECK (<c>[fable3-statecheck]</c>: a difference is logged
/// once per element and value) and, until every text has its stated writer, as the fallback a
/// path with no stated text draws (<c>sample-only</c>, logged once per path).</para>
/// </remarks>
public static class Fable3Elements
{
	/// <summary>One element's last stated values.</summary>
	public sealed class Element
	{
		public int? State { get; set; }

		/// <summary>The opacity a writer set outright (SetOpacity, Lua, a .bgf action) — not the visibility toggle's.</summary>
		public float? Opacity { get; set; }

		/// <summary>The visibility toggle's request (sub_82819AE0's bool), null until asked or after an outright opacity.</summary>
		public bool? Visible { get; set; }

		/// <summary>The text a writer passed — a literal, a tag hash or a tag name — or null when none stated one.</summary>
		public StatedText? Stated { get; set; }

		/// <summary>
		/// The slide a writer passed at its entry (`@Ui.State`: the path setters, the interaction
		/// mode tick's root, the interaction bars' show), null until one did.
		/// </summary>
		public int? StatedState { get; set; }

		/// <summary>
		/// The stamp of the latest `@Ui.State`: each one is an entry of the slide (the guest's funnel
		/// restarts it even when it is the same), so a playback re-enters on a newer stamp.
		/// </summary>
		public ulong StatedStateStamp { get; set; }

		/// <summary>The active flag as its setter was asked (`@Ui.Active`, sub_828196E0), null until it was.</summary>
		public bool? StatedActive { get; set; }

		/// <summary>The texture path the setter was called with (`@Ui.Texture`), null until one was.</summary>
		public string? StatedTexture { get; set; }

		/// <summary>An outright opacity a surface's own writer passed (the interaction button's 100 / 50), null until one did.</summary>
		public float? StatedOpacity { get; set; }

		/// <summary>
		/// The opacity the stated channels alone draw the element at: 0 asked inactive (flag 0x2000
		/// off skips the whole subtree) or hidden by the visibility toggle (sub_82819AE0), else the
		/// stated outright opacity, else null for the authored — a show restores what a hide divided
		/// away (0x82819B44..0x82819B64).
		/// </summary>
		public float? StatedDrawnOpacity => StatedActive == false || Visible == false ? 0f : StatedOpacity;

		/// <summary>The stated text resolved (a literal, or the tag's text in the title's book), or null.</summary>
		public string? StatedTextValue => Stated is { } stated ? Resolve(stated) : null;

		/// <summary>The text box's string read after the commit (`@Element.Text`): the cross-check.</summary>
		public string? SampledText { get; set; }

		/// <summary>The opacity the toggle wrote, read back after it (`@Element.ToggleOpacity`): the cross-check.</summary>
		public float? ToggleOpacity { get; set; }

		/// <summary>What the element shows: its stated text, else (no stated writer yet) the sample.</summary>
		public string? Text => Stated is { } stated ? Resolve(stated) : SampledText;

		/// <summary>The element's timeline position as the guest passed it (a meter's fraction × 1000).</summary>
		public float? Time { get; set; }

		/// <summary>
		/// The element's local `position.x/.y/.z` as the guest last wrote them (sub_82818580): the
		/// .bgf's own units, null until written. A TopBox line's guest fit is never stated: Godot
		/// stacks those rows from the file's first line (Engine_UiTextBoxFit).
		/// </summary>
		public (float X, float Y, float Z)? Position { get; set; }

		/// <summary>The texture path the guest last set on the element (sub_82818EF0), null until set.</summary>
		public string? Texture { get; set; }

		/// <summary>
		/// The renderable's diffuse as the colour setter was called with it (`@Ui.Colour`,
		/// sub_828190E8): r, g, b, a in 0..1; null until one was — the file's colour stands.
		/// </summary>
		public (float R, float G, float B, float A)? Colour { get; set; }

		/// <summary>The element's `active` flag (flag 0x2000: Lua setAttribute, sub_828196E0), null until set.</summary>
		public bool? Active { get; set; }

		/// <summary>Every other attribute a Gameface write set by name, as last stated.</summary>
		public Dictionary<string, Engine_UiModel.GuiNativeArgument> Attributes { get; } = new(System.StringComparer.Ordinal);

		public uint Address { get; set; }

		/// <summary>The <see cref="Generation"/> of the element's latest statement: which of two statements is the later.</summary>
		public ulong Stamp { get; set; }

		/// <summary>
		/// Hidden, as the visibility toggle leaves it: a hide divides the opacity by 1024 and a show
		/// multiplies one below 0.09765625 (flt_832A18DC) by 1024 (sub_82819AE0, 0x82819B38..0x82819B64),
		/// so asked-hidden is hidden, asked-shown is hidden only over an outright 0, and an
		/// element never toggled is hidden at or under the threshold.
		/// </summary>
		public bool Hidden => Visible switch
		{
			false => true,
			true => Opacity is { } o && o * 1024f <= HiddenOpacity,
			null => Opacity is { } o && o <= HiddenOpacity,
		};

		/// <summary>The opacity the element is drawn at: 0 hidden, the toggle's scale applied, null for the authored.</summary>
		public float? DrawnOpacity => Visible switch
		{
			false => 0f,
			true => Opacity is { } o ? (o <= HiddenOpacity ? o * 1024f : o) : null,
			null => Opacity,
		};

		/// <summary>The document and path this element is held under.</summary>
		internal (string Document, string Path)? Key { get; set; }
	}

	/// <summary>A text as a writer stated it: exactly one of the three is set.</summary>
	public readonly record struct StatedText(string? Literal, uint? Tag, string? TagName, string Via);

	/// <summary>The visibility toggle's threshold, flt_832A18DC (sub_82819AE0 at 0x82819B38).</summary>
	public const float HiddenOpacity = 0.09765625f;

	/// <summary>hud.bgf's document (sub_825C18C0 loads "HUD.bgf").</summary>
	public const string HudDocument = "hud";

	/// <summary>InteractionMode.bgf's document (sub_825C42C0 loads "InteractionMode.bgf").</summary>
	public const string InteractionDocument = "interactionmode";

	private static readonly Dictionary<(string Document, string Path), Element> ByPath = new();
	private static readonly Dictionary<uint, Element> ByAddress = new();
	private static readonly Dictionary<uint, string> Documents = new();
	private static readonly HashSet<string> Reported = new();
	private static readonly HashSet<string> Checked = new();
	private const int MaxReported = 600;
	private const int MaxChecked = 400;

	/// <summary>Where the census and the cross-check write (GD.Print in the game; a test's own sink).</summary>
	public static System.Action<string> Log { get; set; } = message => GD.Print(message);

	/// <summary>A tag hash's text in the title's book (Fable3Strings), or null.</summary>
	public static System.Func<uint, string?> TagText { get; set; } = DefaultTagText;

	/// <summary>A tag name's text in the title's book, or null.</summary>
	public static System.Func<string, string?> TagNameText { get; set; } = DefaultTagNameText;

	/// <summary>Bumped on every statement, so a view redraws only when something changed.</summary>
	public static ulong Generation { get; private set; }

	/// <summary>Cross-check differences logged so far (stated vs sampled), for the run's census.</summary>
	public static int Differences { get; private set; }

	/// <summary>
	/// The next stamp on the one clock every Fable 3 UI statement is ordered by — the element
	/// writes here and the registry writers' (<see cref="Fable3HudRegistry"/>) — so a document
	/// folding both applies them in the order the guest made them.
	/// </summary>
	internal static ulong NextStamp() => ++Generation;

	/// <summary>
	/// THE HUD ROOT'S STATE as its writers state it (`@Hud.Root`, screen_stated_hooks.cpp): HIDE
	/// sub_825C1CB0 2 (every box's Show), SHOW sub_825C1D28 1 (the boxes' hides, the HUD tick's
	/// enable, the cutscene rules leaving bit 27), the cutscene rules sub_8249A030 3 (bit 27 kept).
	/// Null until one spoke. Not the interaction mode's tick sub_825C3A78 — that writes
	/// InteractionMode.bgf's root (IM+0), not hud.bgf's.
	/// </summary>
	public static int? HudRootState { get; private set; }

	/// <summary>The HUD is hidden: its root on state 2 (hud.bgf's `GameToPauseMenu` slide fades every widget).</summary>
	public static bool HudHidden => (HudRootState ?? Get(HudDocument, Fable3HudStatements.RootPath)?.State) == 2;

	/// <summary>The document under each root segment that most recently stated an element there.</summary>
	private static readonly Dictionary<string, string> LatestByRoot = new(System.StringComparer.Ordinal);

	/// <summary>A presentation's document: its load's file stem, lower-cased; `#address` before a load named it; empty for 0.</summary>
	public static string DocumentOf(uint presentation) =>
		presentation == 0u ? string.Empty
		: Documents.TryGetValue(presentation, out string? document) ? document
		: "#" + presentation.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// The document a statement's presentation argument names: `=hud` (the native names the
	/// document once it saw the load — ui_stated_hooks.cpp's PresentationKey), else the address in
	/// hex through the loads this model heard.
	/// </summary>
	public static string DocumentOfKey(string key) =>
		key.StartsWith('=') ? key[1..] : DocumentOf(Parse(key));

	/// <summary>The document a load's file names: `HUD.bgf` and `…\gameface\Options.bgf` are `hud` and `options`.</summary>
	public static string DocumentName(string file)
	{
		string name = file.Replace('\\', '/');
		int slash = name.LastIndexOf('/');
		name = slash >= 0 ? name[(slash + 1)..] : name;
		int dot = name.LastIndexOf('.');
		return (dot > 0 ? name[..dot] : name).ToLowerInvariant();
	}

	/// <summary>The document that last wrote under <paramref name="root"/>, or null.</summary>
	public static string? LatestDocumentOf(string root) =>
		LatestByRoot.TryGetValue(root, out string? document) ? document : null;

	/// <summary>The element at a path in the root's latest document, or null.</summary>
	public static Element? Latest(string path)
	{
		int dot = path.IndexOf('.');
		return LatestDocumentOf(dot < 0 ? path : path[..dot]) is { } document ? Get(document, path) : null;
	}

	/// <summary>The element at a path in a document, or null when nothing was stated for it.</summary>
	public static Element? Get(string document, string path) =>
		ByPath.TryGetValue((document, path), out Element? element) ? element : null;

	/// <summary>Every stated element with a path, across documents.</summary>
	public static IEnumerable<KeyValuePair<(string Document, string Path), Element>> All => ByPath;

	/// <summary>Every stated element of a document.</summary>
	public static IEnumerable<KeyValuePair<(string Document, string Path), Element>> Of(string document)
	{
		foreach (KeyValuePair<(string Document, string Path), Element> entry in ByPath)
		{
			if (entry.Key.Document == document)
			{
				yield return entry;
			}
		}
	}

	/// <summary>Forgets everything (a new session; the tests).</summary>
	public static void Clear()
	{
		ByPath.Clear();
		ByAddress.Clear();
		Documents.Clear();
		LatestByRoot.Clear();
		Reported.Clear();
		Checked.Clear();
		HudRootState = null;
		Differences = 0;
		++Generation;
	}

	/// <summary>Routes the element statements into the model.</summary>
	private static readonly Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>> Routes = BuildRoutes();

	/// <summary>Routes this model's statements from the natives router.</summary>
	public static void Register(Engine_UiGuiNatives router)
	{
		foreach (KeyValuePair<string, System.Action<Engine_UiModel.GuiNativeCall>> route in Routes)
		{
			System.Action<Engine_UiModel.GuiNativeCall> handler = route.Value;
			router.Register(route.Key, call => handler(call));
		}
	}

	/// <summary>Takes one statement as the router would (the tests' entry); false when it is not this model's.</summary>
	public static bool Take(Engine_UiModel.GuiNativeCall call)
	{
		if (!Routes.TryGetValue(call.Name, out System.Action<Engine_UiModel.GuiNativeCall>? handler))
		{
			return false;
		}

		handler(call);
		return true;
	}

	private static Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>> BuildRoutes()
	{
		var routes = new Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>>(System.StringComparer.Ordinal);
		void Add(string name, System.Action<Engine_UiModel.GuiNativeCall> handler) => routes[name] = handler;

		Add("@Presentation.Load", call => Load(Parse(call.Argument(0).Text), call.Argument(1).Text));

		// The writers' values at entry: what is drawn.
		Add("@Ui.Text", call => Apply(call, (element, value) => SetStated(element, new StatedText(value.Text, null, null, Via(call)))));
		Add("@Ui.TextTag", call => Apply(call, (element, value) => SetStated(element, new StatedText(null, Parse(value.Text), null, Via(call)))));
		Add("@Ui.TextName", call => Apply(call, (element, value) => SetStated(element, new StatedText(null, null, value.Text, Via(call)))));
		// A position the guest's own call states (the credits roll's SetPosition sites,
		// headless_commit_hooks.cpp): (pres, elem, path, x, y, z, via).
		Add("@Ui.Position", call => Apply(call, (element, value) =>
			element.Position = (value.Number, call.Argument(4).Number, call.Argument(5).Number)));
		Add("@Ui.Visible", call => Apply(call, (element, value) =>
		{
			element.Visible = value.Boolean;
			CheckVisible(element);
		}));
		Add("@Ui.State", call => Apply(call, (element, value) =>
		{
			element.StatedState = (int)value.Number;
			element.State = (int)value.Number;
			element.StatedStateStamp = Generation + 1;
		}));
		Add("@Ui.Active", call => Apply(call, (element, value) => element.StatedActive = value.Boolean));
		// (pres, elem, path, r, g, b, a, via), 0..255 (ui_stated_hooks.cpp, sub_828190E8).
		Add("@Ui.Colour", call => Apply(call, (element, red) => element.Colour = (
			red.Number / 255f, call.Argument(4).Number / 255f, call.Argument(5).Number / 255f, call.Argument(6).Number / 255f)));
		// A meter by path, its fraction × 1000 at the call (sub_8281AD48, ui_stated_hooks.cpp).
		Add("@Ui.Time", call => Apply(call, (element, value) => element.Time = value.Number));
		Add("@Ui.Texture", call => Apply(call, (element, value) =>
		{
			element.StatedTexture = value.Text;
			element.Texture = value.Text;
		}));

		// The setters' arguments, after the call (gui_element_hooks.cpp): stated too.
		Add("@Element.State", call => Apply(call, (element, value) => element.State = (int)value.Number));
		Add("@Element.Opacity", call => Apply(call, (element, value) =>
		{
			element.Opacity = value.Number;
			element.Visible = null;  // an outright opacity overrides the toggle (it sets what the toggle reads)
		}));
		// THE OPACITY THE ELEMENT HOLDS after its writers and the track pass (gui_element_hooks.cpp
		// StateStoredOpacity): what the scene draws. The toggle hides by storing opacity / 1024 and its
		// show reads the stored value back (sub_82819560 at 0x82819b2c), so mid-fade its own write is
		// not the picture; the store is. Above 100 (SetOpacity scales its argument by 1024 over a
		// stored value under 0.09765625, 0x828194ac..0x828194bc) draws as 100.
		Add("@Element.StoredOpacity", call => Apply(call, (element, value) =>
		{
			element.Opacity = System.Math.Clamp(value.Number, 0f, 100f);
			element.Visible = null;
		}));
		Add("@Element.Time", call => Apply(call, (element, value) => element.Time = value.Number));
		Add("@Element.Texture", call => Apply(call, (element, value) => element.Texture = value.Text));
		Add("@Element.Position", call => Apply(call, (element, value) =>
			element.Position = (value.Number, call.Argument(4).Number, call.Argument(5).Number)));
		Add("@Element.Active", call => Apply(call, (element, value) => element.Active = value.Boolean));
		// (pres, elem, path, name, value): the name in argument 3, the value after it.
		Add("@Element.Attribute", call => Apply(call, (element, name) =>
			element.Attributes[name.Text] = call.Argument(4)));

		// Read back after the guest committed: the cross-check.
		Add("@Element.Text", call => Apply(call, (element, value) =>
		{
			element.SampledText = value.Text;
			CheckText(element);
		}));
		Add("@Element.ToggleOpacity", call => Apply(call, (element, value) =>
		{
			element.ToggleOpacity = value.Number;
			CheckVisible(element);
		}));

		return routes;
	}

	/// <summary>`@Presentation.Load(pres, file)`: the address is that document's now, and the document starts clean.</summary>
	public static void Load(uint presentation, string file)
	{
		if (presentation == 0u || file.Length == 0)
		{
			return;
		}

		string document = DocumentName(file);
		Documents[presentation] = document;
		Fable3LuaContracts.Forget(document);
		var drop = new List<(string, string)>();
		foreach (KeyValuePair<(string Document, string Path), Element> entry in ByPath)
		{
			if (entry.Key.Document == document)
			{
				drop.Add(entry.Key);
				if (entry.Value.Address != 0u)
				{
					ByAddress.Remove(entry.Value.Address);
				}
			}
		}

		foreach ((string, string) key in drop)
		{
			ByPath.Remove(key);
		}

		NextStamp();
		Log($"[fable3-elements] {presentation:X8} loads '{file}' as document '{document}'" +
			(drop.Count == 0 ? string.Empty : $" ({drop.Count} element(s) of its previous load forgotten)"));
	}

	/// <summary>`@Hud.Root(state, via)`: the root's state, landed on hud.bgf's root element too.</summary>
	public static void StateHudRoot(int state, string via)
	{
		HudRootState = state;
		State(HudDocument, Fable3HudStatements.RootPath, element => element.State = state);
		Log($"[fable3-elements] HUD root state = {state} ({(state == 2 ? "hidden" : state == 3 ? "ambient cutscene" : "shown")}) by sub_{via}");
	}

	/// <summary>
	/// A surface statement's write onto a document's path (<see cref="Fable3UiStatements"/>): the
	/// element made when the path was never stated, stamped like every other statement.
	/// </summary>
	public static Element State(string document, string path, System.Action<Element> set)
	{
		Element element = ElementAt(document, path, 0u);
		set(element);
		element.Stamp = NextStamp();
		return element;
	}

	private static Element ElementAt(string document, string path, uint address)
	{
		if (!ByPath.TryGetValue((document, path), out Element? element))
		{
			element = new Element { Address = address, Key = (document, path) };
			ByPath[(document, path)] = element;
		}

		int dot = path.IndexOf('.');
		LatestByRoot[dot < 0 ? path : path[..dot]] = document;
		return element;
	}

	private static string Via(Engine_UiModel.GuiNativeCall call) => call.Argument(4).Text;

	private static void SetStated(Element element, StatedText stated)
	{
		element.Stated = stated;
		CheckText(element);
	}

	private static string? Resolve(StatedText stated) =>
		stated.Literal ?? (stated.Tag is { } tag ? TagText(tag) : stated.TagName is { } name ? TagNameText(name) : null);

	private static string? DefaultTagText(uint tag) =>
		Fable3Strings.Current is { } strings && strings.TryGetString(tag, out string text) ? text : null;

	private static string? DefaultTagNameText(string name) =>
		name.Length == 0 ? string.Empty : Engine_UiGuiText.Resolve(name, Fable3Strings.Current);

	/// <summary>Logs a stated text the box read back differently (once per element and pair), or a sample with no stated writer (once per path).</summary>
	private static void CheckText(Element element)
	{
		string where = element.Key is { } key ? $"{key.Document}:{key.Path}" : $"<unnamed {element.Address:X8}>";
		if (element.Stated is not { } stated)
		{
			if (element.SampledText is { Length: > 0 } && Checked.Count < MaxChecked && Checked.Add("sample-only " + where))
			{
				Log($"[fable3-statecheck] sample-only {where} = '{element.SampledText}' (no stated writer reached it)");
			}

			return;
		}

		if (element.SampledText is not { } sampled)
		{
			return;
		}

		string? said = Resolve(stated);
		if (said is null || Normalise(said) == Normalise(sampled))
		{
			return;
		}

		++Differences;
		if (Checked.Count < MaxChecked && Checked.Add($"text {where} {said} {sampled}"))
		{
			Log($"[fable3-statecheck] DIFF text {where}: stated '{said}' by sub_{stated.Via}, box shows '{sampled}'");
		}
	}

	/// <summary>Logs a toggle request the read-back opacity disagrees with (once per element and pair).</summary>
	private static void CheckVisible(Element element)
	{
		if (element.Visible is not { } visible || element.ToggleOpacity is not { } read)
		{
			return;
		}

		bool shown = read > HiddenOpacity;
		if (shown == visible)
		{
			return;
		}

		++Differences;
		string where = element.Key is { } key ? $"{key.Document}:{key.Path}" : $"<unnamed {element.Address:X8}>";
		if (Checked.Count < MaxChecked && Checked.Add($"visible {where} {visible} {read}"))
		{
			Log($"[fable3-statecheck] DIFF visible {where}: asked {(visible ? "shown" : "hidden")}, toggle wrote opacity {read}");
		}
	}

	/// <summary>The guest wraps lines with U+000A and the book's strings carry their own breaks: compared without whitespace.</summary>
	internal static string Normalise(string text)
	{
		var builder = new System.Text.StringBuilder(text.Length);
		foreach (char c in text)
		{
			if (!char.IsWhiteSpace(c))
			{
				builder.Append(c);
			}
		}

		return builder.ToString();
	}

	/// <summary>
	/// THE MIRROR CUT. The paths whose look a stated call drives (<see cref="Fable3UiStatements"/>):
	/// a setter statement (`@Element.State`, `.Opacity`, `.StoredOpacity`, `.Time`, `.Position`)
	/// on one of them is dropped, so the stated value is the only one there is.
	/// </summary>
	private static readonly HashSet<(string Document, string Path)> OwnedPaths = new()
	{
		// hud.bgf's root: `@Hud.Root` (sub_825C1CB0 / sub_825C1D28 / sub_8249A030).
		(HudDocument, Fable3HudStatements.RootPath),
		// The crime timer's root: `@Hud.Crime.Shown` (sub_828393F0's CT+0 stores).
		(HudDocument, "CrimeTimer"),
		(HudDocument, "Layer.CrimeTimer"),
		// The job multiplier's root: `@Hud.Job` and TurnJobMultiplierOff (Fable3UiStatements.JobLevel / JobOff).
		(HudDocument, "JobMultiplier"),
		(HudDocument, "Layer.JobMultiplier"),
		// The look-at alert: `@Hud.LookAt` (sub_82B14CB8's slide, the HUD tick's hide).
		(HudDocument, "LookAt.LookAt.LookAt"),
		(HudDocument, "Layer.LookAt.LookAt.LookAt"),
		// The money alert's gold icons: shown and hidden only by the visibility toggle sub_8281E468 ->
		// sub_82819B90 asks for (`@Ui.Visible`), never an outright opacity.
		(HudDocument, "MoneyAlert.MainPlayer.Group.KingdomGold"),
		(HudDocument, "MoneyAlert.MainPlayer.Group.PlayerGold"),
		(HudDocument, "MoneyAlertSecondPlayer.SecondPlayer.Group.KingdomGold"),
		(HudDocument, "MoneyAlertSecondPlayer.SecondPlayer.Group.PlayerGold"),
		// The objective list and its rows' ticks: `@Hud.Objective.Redraw` (sub_82953E68) and the
		// hide (Fable3UiStatements.ObjectivesDrawn / ObjectivesHidden).
		(HudDocument, "ObjectiveList.ObjectiveList"),
		(HudDocument, "Layer.ObjectiveList.ObjectiveList"),
		(HudDocument, "ObjectiveList.ObjectiveList.Bar1.tickbox.tick"),
		(HudDocument, "Layer.ObjectiveList.ObjectiveList.Bar1.tickbox.tick"),
		(HudDocument, "ObjectiveList.ObjectiveList.Bar2.tickbox.tick"),
		(HudDocument, "Layer.ObjectiveList.ObjectiveList.Bar2.tickbox.tick"),
		(HudDocument, "ObjectiveList.ObjectiveList.Bar3.tickbox.tick"),
		(HudDocument, "Layer.ObjectiveList.ObjectiveList.Bar3.tickbox.tick"),
		(HudDocument, "ObjectiveList.ObjectiveList.Bar4.tickbox.tick"),
		(HudDocument, "Layer.ObjectiveList.ObjectiveList.Bar4.tickbox.tick"),
		(HudDocument, "ObjectiveList.ObjectiveList.Bar5.tickbox.tick"),
		(HudDocument, "Layer.ObjectiveList.ObjectiveList.Bar5.tickbox.tick"),
		// The co-op partners' markers: placed and faded by Fable3WorldIcons from their stated icons
		// (types 52/53), never from the clamp's SetPosition (sub_82B51B58 at 0x82B51FC8) or the
		// icon's slide and visibility writes (0x82B51F28, sub_82B52818).
		(HudDocument, "HeroOffScreenWorldIcon"),
		(HudDocument, "Layer.HeroOffScreenWorldIcon"),
		(HudDocument, "HeroOffScreenWorldIcon.Contents"),
		(HudDocument, "HeroOffScreenWorldIcon.Contents.Icon"),
		(HudDocument, "HeroOffScreenWorldIcon.Contents.Opinion"),
		(HudDocument, "HenchmanOffScreenWorldIcon"),
		(HudDocument, "Layer.HenchmanOffScreenWorldIcon"),
		(HudDocument, "HenchmanOffScreenWorldIcon.Contents"),
		(HudDocument, "HenchmanOffScreenWorldIcon.Contents.Icon"),
		(HudDocument, "HenchmanOffScreenWorldIcon.Contents.Opinion"),
	};

	/// <summary>
	/// The setter channels a whole document takes from stated calls instead: the credits' positions
	/// are the roll's own SetPosition calls (`@Ui.Position`), so `@Element.Position` there is dropped.
	/// </summary>
	private static readonly Dictionary<string, HashSet<string>> OwnedChannels = new(System.StringComparer.Ordinal)
	{
		["creditsscreen"] = new HashSet<string>(System.StringComparer.Ordinal) { "@Element.Position" },
		// The box-manager screens and the save menu (Fable3StatedScreen): their texts, slides by path,
		// meters by path and visibility toggles are stated at the call (`@Ui.*`), their cursor rows,
		// ghosting and rows by their own calls (Fable3ScreenStatements); the setters' own state,
		// opacity and timeline writes never reach them.
		["herostatus"] = ScreenChannels(),
		["statsscreen"] = ScreenChannels(),
		["promises"] = ScreenChannels(),
		["questsummary"] = ScreenChannels(),
		["kingsschedule"] = ScreenChannels(),
		["joinoptions"] = ScreenChannels(),
		["visability"] = ScreenChannels(),
		["friendschannel"] = ScreenChannels(),
		["chatchannel"] = ScreenChannels(),
		["treasureaccounts"] = ScreenChannels(),
		["bedmenubox"] = ScreenChannels(),
		["foodquerybox"] = ScreenChannels(),
		["compweddingpicker"] = ScreenChannels(),
		["cooppartnersscreen"] = ScreenChannels(),
		["proposalquerybox"] = ScreenChannels(),
		["divorcemessagebox"] = ScreenChannels(),
		["savemenu"] = ScreenChannels(),
		// The interaction mode is stated-only (Fable3Hud: slides `@Ui.State`, visibility `@Ui.Visible`,
		// opacity `@Interaction.Button`, times from the registry). The setters' echoes must not even
		// touch the record: the toggle's own SetOpacity echo (0.09765625 after a hide, 0x82819B68)
		// cleared `Visible` and drew a hidden button (Button1, an empty glyph under Wear).
		["interactionmode"] = new HashSet<string>(System.StringComparer.Ordinal)
		{
			"@Element.State", "@Element.Opacity", "@Element.StoredOpacity", "@Element.Time", "@Element.Position",
		},
	};

	/// <summary>The setter channels a stated screen document drops (sub_82B3EC40's files, by stem).</summary>
	private static HashSet<string> ScreenChannels() => new(System.StringComparer.Ordinal)
	{
		"@Element.State", "@Element.Opacity", "@Element.StoredOpacity", "@Element.Time",
	};

	/// <summary>Setter statements dropped on an owned path, for the census.</summary>
	public static int MirrorDropped { get; private set; }

	/// <summary>Whether a stated call owns the look of this document's path (<see cref="OwnedPaths"/>).</summary>
	public static bool IsOwned(string document, string path) => OwnedPaths.Contains((document, path));

	private static void Apply(Engine_UiModel.GuiNativeCall call, System.Action<Element, Engine_UiModel.GuiNativeArgument> set)
	{
		uint address = Parse(call.Argument(1).Text);
		string path = call.Argument(2).Text;
		Engine_UiModel.GuiNativeArgument value = call.Argument(3);
		string document = DocumentOfKey(call.Argument(0).Text);
		if (call.Name.StartsWith("@Element.", System.StringComparison.Ordinal) && call.Name != "@Element.Text" &&
			call.Name != "@Element.ToggleOpacity" && call.Name != "@Element.Texture" && call.Name != "@Element.Active" &&
			call.Name != "@Element.Attribute" &&
			(IsOwned(document, path) || (OwnedChannels.TryGetValue(document, out HashSet<string>? channels) && channels.Contains(call.Name))))
		{
			if (++MirrorDropped is 1 or 16 or 256 or 4096)
			{
				Log($"[fable3-mirror] dropped {call.Name} on stated {document}:{path} ({MirrorDropped} so far)");
			}

			return;
		}

		// One element under every name it was stated by: the address first (a registry key and the
		// resolver's later path are the same element), else the path.
		Element? element = null;
		if (address != 0u && ByAddress.TryGetValue(address, out Element? known))
		{
			element = known;
		}

		if (element is null && path.Length > 0 && ByPath.TryGetValue((document, path), out Element? named))
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
			ByPath[(document, path)] = element;
			element.Key ??= (document, path);
			int dot = path.IndexOf('.');
			LatestByRoot[dot < 0 ? path : path[..dot]] = document;
		}

		set(element, value);
		element.Stamp = NextStamp();

		if (Reported.Count < MaxReported && Reported.Add(document + ":" + (path.Length > 0 ? path : address.ToString("X8"))))
		{
			string shown = value.Kind == Engine_UiModel.GuiNativeArgumentKind.String ? $"'{value.Text}'" : value.Text;
			Log($"[fable3-elements] {document} {(path.Length > 0 ? path : "<unnamed " + address.ToString("X8") + ">")} {call.Name[(call.Name.IndexOf('.') + 1)..]} = {shown}");
		}
	}

	internal static uint Parse(string hex) =>
		uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value) ? value : 0u;
}

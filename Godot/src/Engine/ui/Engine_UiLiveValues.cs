using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// Drives the in-game HUD's live values onto a built <c>hud.bgf</c>, in Godot.
///
/// GODOT OWNS THE HUD; THE GUEST STATES THE NUMBERS. That is the same division the front end
/// settled on, reached from the opposite direction. There, the guest had no item list to
/// mirror and Godot had to own the model outright. Here the guest owns a great deal — it is
/// the only thing that knows the hero's health — but it states every bit of it as a named
/// percentage, and states which element draws which name in the file itself. So neither side
/// has to guess: <see cref="Engine_UiBindings"/> reads the binding out of `hud.bgf`,
/// <c>Engine_UiSource.Current.GetHudMeters</c> reads the values out of the guest, and this joins them on a
/// hash both sides computed from the same string.
///
/// WHAT A METER ACTUALLY DOES, and the one place this class knowingly approximates it.
///
/// The guest does NOT set a scale. Following the write all the way down —
/// `sub_823FDFF8(meter, pct)` computes `trunc(pct * 10)`, an integer 0..1000, and hands it to
/// `sub_82426E20(element, presentation, value)`, which resolves `presentation + 568` and calls
/// slot +44 of its vtable (`off_820030A8`) — lands on `sub_82BB5B60`:
///
/// <code>
/// result = (*(vt + 4))(a1);            // the element's animation state
/// *(_DWORD *)(result + 56) = a3;       // 0..1000
/// *(_DWORD *)(result + 48) = a3;
/// *(_BYTE  *)(result + 64) |= 0x10;    // dirty
/// </code>
///
/// Both the current and target positions of the element's OWN TIMELINE, plus a dirty bit. So a
/// meter is a PLAYHEAD: the artist animated the bar, and the percentage scrubs that animation
/// rather than driving any single property.
///
/// The file agrees, and says what the animation does. Under an element carrying
/// <c>MeterName</c> the artist authored:
///
/// <code>
/// BarMiddle   pivot.x -50   scale.x 0   scale.y 0.24    (a 1px slice, scaleu 0.008)
/// BarEnd      pivot.x -50   scale.x 0   scale.y 0.24    (the cap,     scaleu 0.043)
/// </code>
///
/// An EMPTY bar pivoted on its left edge. The authored zero is the proof that something else
/// fills it — an element nothing drove would have been authored at the size the artist wanted
/// to see — and the left pivot is why a fill grows rightward instead of from the middle. On
/// the builder's 100-unit base quad, <c>pivot.x -50</c> IS the left edge.
///
/// SO SCALING X FROM ZERO IS THE RIGHT SHAPE, AND IT IS STILL AN APPROXIMATION. It reproduces
/// the endpoints the file states and the direction the pivot states, and for a bar animated
/// linearly it is exact. What it cannot reproduce is a curve: if the artist eased the fill, or
/// animated colour or opacity alongside it, this draws the motion straight and drops the rest.
/// Making it exact needs the `.bgf` ANIMATION chunk, which
/// <see cref="Engine_BgfDocument"/> currently skips to EOF — that is the honest next step, and
/// it is a format section rather than a change of design here. Until then the numbers are the
/// guest's and only the interpolation between them is ours.
///
/// WHAT THIS DOES NOT DO. It does not decide what a name means. "PlayerHealth" being
/// `current / absoluteMax * 100` while "PlayerHealthMax" is `currentMax / absoluteMax * 100`
/// is the guest's arithmetic (`sub_825E8DE0` at 0x825E90D0), and the reason the health widget
/// has two bars — a bright fill and a dim `BarMax` at opacity 25 behind it — is that the second
/// shows how much of the hero's absolute capacity is currently available. Encoding that here
/// would be re-deriving a fact the guest already sends.
///
/// It also does not drive TEXT or PULSERS yet. Those live in different registries of the same
/// vector, and their index is not something I have decompiled — see
/// <c>RefiiHostGetHudMeters</c>, which reads only the index it can prove. The bindings are
/// already read and reported, so wiring them is a native accessor rather than a redesign.
/// </summary>
public sealed class Engine_UiLiveValues
{
	/// <summary>
	/// One bound bar: which name feeds it, the nodes that draw it, and how wide FULL is.
	///
	/// Resolved ONCE when the HUD is built rather than looked up per frame. The lookup is a
	/// tree walk by name over a 3431-element presentation, and this runs every frame — the
	/// front-end row menu makes the same trade for the same reason, keeping its glyph work off
	/// the per-frame path.
	/// </summary>
	/// <summary>
	/// <paramref name="FullScaleX"/> is the x-scale the fill wears at 100%, and it is READ
	/// rather than assumed to be 1.
	///
	/// The spell-charge pips prove it has to be: their fill is a <c>charging</c> element whose
	/// own slides sweep <c>scale.x</c> between 0 and <b>0.6</b>. Driving those to 1.0 would
	/// overshoot the artist's full extent by two thirds. Where no slide states a width — the
	/// health bar is the case, its fill being animated in the `.bgf` ANIMATION chunk rather
	/// than by a slide — it falls back to 1 and says so.
	/// </summary>
	private readonly record struct Bar(
		uint NameHash, string Name, Node3D? Fill, Node3D? Cap, float FullScaleX);

	private readonly List<Bar> _bars = new();

	/// <summary>
	/// One bound text field: which name feeds it, and the mesh that draws it.
	///
	/// <see cref="Shown"/> is the last string written, so a field is re-laid only when its text
	/// actually changes. That matters more here than on the row menu: the subtitle line changes
	/// a few times a minute and the experience counters change every frame they animate, and
	/// rebuilding a glyph mesh for an unchanged string would put text layout on the per-frame
	/// path for the whole HUD.
	/// </summary>
	private sealed class Field
	{
		public required uint NameHash { get; init; }
		public required string Name { get; init; }
		public required MeshInstance3D Node { get; init; }
		public string Shown { get; set; } = string.Empty;
	}

	private readonly List<Field> _fields = new();

	/// <summary>The look text is drawn with. Without it a bound field stays as the file left it.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>Text fields written on the last <see cref="Apply"/>.</summary>
	public int TextsApplied { get; private set; }

	/// <summary>
	/// Bound text fields whose name the guest actually publishes, whatever it publishes for it.
	///
	/// The denominator that tells a broken hash from an empty message: equal to
	/// the bound field count means the join is sound and the guest simply has nothing to
	/// say, while zero against a non-empty registry means we are looking up the wrong key.
	/// </summary>
	public int FieldsMatched { get; private set; }

	/// <summary>
	/// What each bar actually resolved to, for a check tool.
	///
	/// Exposed so a diagnostic reports THIS object's decisions rather than re-deriving them.
	/// The probe used to look the fill up by name itself and consequently could not see the ten
	/// spell-charge meters, whose fill is called `charging` — it showed a clean report of the
	/// bars it already knew about and stayed silent about the ones at risk.
	/// </summary>
	public IEnumerable<(string Name, uint NameHash, Node3D? Fill, float FullScaleX)> Bars
	{
		get
		{
			foreach (Bar bar in _bars)
			{
				yield return (bar.Name, bar.NameHash, bar.Fill, bar.FullScaleX);
			}
		}
	}

	/// <summary>Bindings whose element was not in the built tree, which is worth reporting.</summary>
	public int BarsUnresolved { get; private set; }

	/// <summary>The last values applied, for a check tool's log.</summary>
	public int MetersApplied { get; private set; }

	/// <summary>
	/// Binds every meter in the document to the nodes the builder made for it.
	///
	/// <paramref name="nodes"/> is indexed by ELEMENT ORDINAL, which is the only key that
	/// works here. A `.bgf` has far more elements than distinct names — `hud.bgf` has 3431
	/// elements and reuses "Bar", "BarMiddle" and "Component" dozens of times — so a lookup by
	/// name hash would collapse every bar onto one widget.
	/// </summary>
	public void Bind(
		Engine_BgfDocument document, Engine_UiBindings bindings, IReadOnlyList<Node3D?> nodes)
	{
		_bars.Clear();
		_fields.Clear();
		BarsUnresolved = 0;

		// EVERY METER-BOUND ELEMENT, gathered before anything is resolved, because a fill
		// belongs to the NEAREST binding above it rather than to any binding above it.
		//
		// `TopBoxY` is bound to `HealthAndMana`, which is the PARENT of the health bar — so a
		// naive search under it finds the health bar's own `BarMiddle` and two meters end up
		// writing one node, last one wins. The artist already resolved this by binding `Bar`
		// with `PlayerHealth`: anything under `Bar` is the health bar's business.
		var bound = new HashSet<int>();
		foreach (Engine_UiBindings.Binding binding in bindings.All)
		{
			if (binding.Kind == Engine_UiBindings.Kind.Meter)
			{
				bound.Add(binding.ElementOrdinal);
			}
		}

		foreach (Engine_UiBindings.Binding binding in bindings.All)
		{
			// A TEXT BINDING RESOLVES TO THE MESH ITSELF, not to a container. The scene builder
			// makes one MeshInstance3D per text element and stamps the artist's point size,
			// alignment, wrap mode and box on it, so the bound ordinal IS the thing to re-text —
			// and re-texting through that metadata is what keeps the layout the artist's.
			if (binding.Kind == Engine_UiBindings.Kind.Text)
			{
				if (binding.ElementOrdinal >= 0 && binding.ElementOrdinal < nodes.Count &&
					nodes[binding.ElementOrdinal] is MeshInstance3D mesh)
				{
					_fields.Add(new Field
					{
						NameHash = binding.NameHash,
						Name = binding.Name,
						Node = mesh,
					});
				}
				else
				{
					++BarsUnresolved;
				}

				continue;
			}

			if (binding.Kind != Engine_UiBindings.Kind.Meter)
			{
				continue;
			}

			if (binding.ElementOrdinal < 0 || binding.ElementOrdinal >= nodes.Count ||
				nodes[binding.ElementOrdinal] is not { } element)
			{
				// The element is bound in the file but absent from the built tree — a
				// non-spatial element, or one the builder skipped. Counted rather than
				// dropped silently, because "the bar never moves" and "the bar was never
				// found" want different fixes and look identical on screen.
				++BarsUnresolved;
				continue;
			}

			// The two pieces by the artist's own names, searched under the bound element only.
			// A meter element may legitimately have neither — the layout meters ("TopBoxY",
			// "CornerY", "SubtitlesY") drive whole components rather than bars, and those are
			// bound here with null pieces so they still report rather than vanish.
			// THE FILL IS FOUND FROM THE SLIDE TABLE FIRST, by name only as a fallback.
			//
			// A fill is not always called `BarMiddle`. The spell-charge pips call theirs
			// `charging`, and a name list would simply miss them — ten of the fifty-two meters
			// silently undriven, which looks exactly like ten meters the guest never published.
			//
			// What every animated fill DOES have is slides that state its `scale.x`, because
			// that is the artist saying how wide it gets. So the element a meter fills is the
			// descendant whose slides move its width, and the largest width they state is what
			// FULL means. That is one rule covering both questions, read out of the data.
			(Node3D? fill, float full) = FindFill(document, binding.ElementOrdinal, nodes, bound);

			// A meter with no fill of its own is kept rather than dropped, with null nodes. The
			// LAYOUT meters — `TopBoxY`, `CornerY`, `SubtitlesX` — are real bindings the guest
			// really publishes; they simply are not bars, and driving whatever quad happens to
			// sit under them is how a meter at 50% drew the heart icon at half size. Keeping
			// them means the bar count still counts what the file binds.
			_bars.Add(new Bar(
				binding.NameHash, binding.Name, fill,
				// The cap only means anything beside a fill this binding actually owns.
				fill is null
					? null
					: Engine_UiTheme.FindByName(element, Engine_UiBindings.FillCapName) as Node3D,
				full));
		}
	}

	/// <summary>
	/// The descendant a meter fills, and the widest <c>scale.x</c> any slide gives it.
	///
	/// Searched over the DOCUMENT rather than the node tree, because the slide table is keyed by
	/// element ordinal and the parent chain is in the document too. Returns the deepest-stated
	/// match under <paramref name="root"/>; null when no slide moves a width down there.
	/// </summary>
	private static (Node3D? Fill, float Full) FindFill(
		Engine_BgfDocument document, int root, IReadOnlyList<Node3D?> nodes,
		HashSet<int> bound)
	{
		uint scaleX = Refii.Core.Engine_GameHash.AttributeId("scale.x");

		// TWO THINGS PER CANDIDATE: does any slide state it EMPTY, and what is the widest one
		// states. Both are needed — see the return below for why either alone lets the wrong
		// element through.
		var widest = new Dictionary<int, float>();
		var empty = new HashSet<int>();

		foreach (BgfSlide slide in document.Slides)
		{
			foreach (BgfSlideEntry entry in slide.Entries)
			{
				if (entry.CommandCount == 0 || !IsUnder(document, entry.ElementOrdinal, root))
				{
					continue;
				}

				foreach (BgfAttribute command in entry.Commands)
				{
					if (command.Id != scaleX || command.Type != BgfValueType.Float)
					{
						continue;
					}

					float value = command.AsFloat();
					if (value <= 0f)
					{
						empty.Add(entry.ElementOrdinal);
					}
					else if (!widest.TryGetValue(entry.ElementOrdinal, out float had) || value > had)
					{
						widest[entry.ElementOrdinal] = value;
					}
				}
			}
		}

		// THE NAMED FALLBACK IS SEARCHED BY ORDINAL TOO, not by walking nodes.
		//
		// `BarMiddle` appears nowhere in the slide table — the health bar is animated in the
		// ANIMATION chunk instead — so the loop below never finds it and this is the path that
		// actually resolves the health bars. Doing it here rather than on the node tree is what
		// lets it be OWNERSHIP-CHECKED like the slide path: `TopBoxY` is bound to
		// `HealthAndMana`, the grandparent of the health bar, and a `FindByName` from there
		// cheerfully returns the health bar's own fill. Two meters then wrote one node and the
		// last one won.
		uint barMiddle = Refii.Core.Engine_GameHash.Sdbm(Engine_UiBindings.FillName);
		int named = -1;

		for (int ordinal = 0; ordinal < document.Elements.Length && ordinal < nodes.Count; ++ordinal)
		{
			if (document.Elements[ordinal].NameHash != barMiddle ||
				nodes[ordinal] is not MeshInstance3D ||
				NearestBound(document, ordinal, bound) != root)
			{
				continue;
			}

			named = ordinal;
			break;
		}

		int best = -1;
		float full = 0f;

		foreach ((int ordinal, float width) in widest)
		{
			// A FILL IS A DRAWABLE QUAD THE ARTIST ANIMATES FROM EMPTY. Both halves of that
			// earn their place, and a run without them showed exactly what each one costs:
			//
			// WITHOUT "states empty": `Base2` (the heart's backing plate, slides state only
			// 0.7) and `Component` (17.09) get taken as fills, and a meter at 50% draws the
			// heart at half size or the subtitle box at a third of its width.
			//
			// WITHOUT "is a quad": `group` — whose slides really do run 0 to 1 — gets taken for
			// the `SubtitlesY` meter, and scaling a CONTAINER rescales everything under it.
			//
			// Together they admit `BarMiddle` and `charging` and nothing else in `hud.bgf`,
			// which is the set that is actually a bar.
			if (!empty.Contains(ordinal) || ordinal >= nodes.Count ||
				nodes[ordinal] is not MeshInstance3D)
			{
				continue;
			}

			// AND IT MUST BE THIS BINDING'S, not a nested one's. Walking up from the candidate,
			// the first meter-bound element found has to be `root`; if it is some other binding,
			// the fill belongs to that one and taking it here would make two meters write one
			// node. See the set built in Bind.
			if (NearestBound(document, ordinal, bound) != root)
			{
				continue;
			}

			if (width > full)
			{
				full = width;
				best = ordinal;
			}
		}

		// A slide-stated fill wins, because it brings the artist's own full extent with it. The
		// named one is the health bar's shape and stands at 1.0 until the ANIMATION chunk is
		// decoded — see the class remarks.
		if (best >= 0)
		{
			return (nodes[best], full);
		}

		return named >= 0 ? (nodes[named], 1f) : (null, 1f);
	}

	/// <summary>
	/// The closest meter-bound element at or above <paramref name="ordinal"/>, or -1.
	///
	/// "Closest" is what makes nesting work: the health bar sits under `Bar` (bound
	/// `PlayerHealth`) which sits under `HealthAndMana` (bound `TopBoxY`), and only the nearer
	/// of the two owns it.
	/// </summary>
	private static int NearestBound(
		Engine_BgfDocument document, int ordinal, HashSet<int> bound)
	{
		for (int at = ordinal, steps = 0;
			at >= 0 && at < document.Elements.Length && steps <= document.Elements.Length;
			at = document.Elements[at].ParentOrdinal, ++steps)
		{
			if (bound.Contains(at))
			{
				return at;
			}
		}

		return -1;
	}

	/// <summary>True when <paramref name="ordinal"/> is <paramref name="root"/> or below it.</summary>
	private static bool IsUnder(Engine_BgfDocument document, int ordinal, int root)
	{
		// Bounded by the element count rather than trusted to terminate: a parent chain is data
		// from a file, and a cycle in it would hang the build rather than draw something wrong.
		for (int at = ordinal, steps = 0;
			at >= 0 && at < document.Elements.Length && steps <= document.Elements.Length;
			at = document.Elements[at].ParentOrdinal, ++steps)
		{
			if (at == root)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Applies the guest's current percentages. Call once per frame with a fresh read.
	///
	/// A MISSING NAME IS LEFT ALONE, not zeroed. The guest's map holds an entry only for a
	/// meter something has actually published, so a name absent from the read is one the game
	/// has not driven yet — and writing zero would empty a bar the guest never touched, which
	/// is a different picture from the authored one.
	/// </summary>
	public void Apply(
		IReadOnlyDictionary<uint, float> meters,
		IReadOnlyDictionary<uint, string>? texts = null)
	{
		MetersApplied = 0;
		TextsApplied = 0;

		if (texts is not null)
		{
			FieldsMatched = 0;
			foreach (Field field in _fields)
			{
				if (!texts.TryGetValue(field.NameHash, out string? display))
				{
					// The guest has not published this name at all.
					continue;
				}

				// COUNTED BEFORE THE CONTENT TEST, and that separation is the whole reason this
				// counter exists. `texts=0` has two causes that need opposite investigations:
				// our hash does not match what the guest publishes, or it matches perfectly and
				// the guest is publishing EMPTY strings because nothing has called SetGUIText
				// yet. A registry entry is created at BIND time carrying the element's LHString,
				// which starts empty — so "published but blank" is the ordinary state of a HUD
				// with no message up, and it is indistinguishable from a broken join unless the
				// match is counted separately from the apply.
				++FieldsMatched;

				if (display == field.Shown)
				{
					// Unchanged — nothing to re-lay, and no reason to touch the mesh.
					continue;
				}

				field.Shown = display;
				SetText(field.Node, display);
				++TextsApplied;
			}
		}

		foreach (Bar bar in _bars)
		{
			if (!meters.TryGetValue(bar.NameHash, out float percent))
			{
				continue;
			}

			// The guest clamps to 0..100 on the way in (`sub_823FE2D0`'s fsel chain against
			// flt_8209BC68 and flt_82099484), so this is belt and braces against a torn read
			// rather than a rule of our own.
			// THE ARTIST'S FULL EXTENT, not a nominal 1.0 — see Bar.FullScaleX. The spell-charge
			// pips fill to 0.6 and driving them to 1.0 would run them two thirds past the frame
			// the artist drew round them.
			float fill = Mathf.Clamp(percent, 0f, 100f) / 100f * bar.FullScaleX;

			if (bar.Fill is { } middle)
			{
				SetFillWidth(middle, fill);
			}

			// THE CAP RIDES THE FILL'S RIGHT EDGE. Both pieces are authored at scale.x 0 on the
			// same left pivot, so the cap has to be given the same scale to sit where the fill
			// ends; it is a separate element because it carries a different slice of the atlas
			// (scaleu 0.043 against the fill's 0.008) — a rounded end rather than a stretched
			// middle.
			if (bar.Cap is { } cap)
			{
				SetFillWidth(cap, fill);
			}

			++MetersApplied;
		}
	}

	/// <summary>
	/// Sets a bar piece's width to <paramref name="fill"/>, leaving its thickness alone.
	///
	/// NEVER THROUGH <c>Node3D.Scale</c> — a bar authored <c>scale.x = 0</c> has a singular
	/// basis, and Godot's scale getter answers (0,0,0) for every axis when it does. See
	/// <see cref="Engine_UiBasisScale"/>, which is the one implementation of the cure.
	/// </summary>
	private static void SetFillWidth(Node3D node, float fill)
	{
		// `Mathf.Clamp` IS NOT A GUARD. Godot's is `v < min ? min : v > max ? max : v`, and
		// every comparison against NaN is false, so a NaN percent falls through both branches
		// unchanged and arrives here looking clamped. The value comes from the guest's meter
		// snapshot, and it ends up in a basis column — a NaN basis gives the bar a NaN AABB,
		// which gives it a NaN render depth, which hangs Godot's render-list sort outright.
		if (!float.IsFinite(fill))
		{
			return;
		}

		Engine_UiBasisScale.SetX(node, fill);
	}

	/// <summary>
	/// Re-texts one bound field in place, keeping every piece of layout the file authored.
	///
	/// THE METADATA IS THE POINT, and it is the same mechanism
	/// <see cref="Engine_FrontEndWarning"/> and the save cards use: the scene builder stamps the
	/// artist's point size, alignment, wrap mode, box and colour onto the node as it builds it,
	/// so regenerating the glyphs from those values reproduces the layout exactly. Choosing them
	/// here would be inventing a layout beside one that already exists — and the HUD has
	/// eighteen of them, no two alike.
	/// </summary>
	private void SetText(MeshInstance3D node, string display)
	{
		if (Theme is null)
		{
			return;
		}

		int size = node.HasMeta("text_size") ? (int)node.GetMeta("text_size") : 18;
		int horizontal = node.HasMeta("text_halign") ? (int)node.GetMeta("text_halign") : 0;
		int vertical = node.HasMeta("text_valign") ? (int)node.GetMeta("text_valign") : 1;
		int wrap = node.HasMeta("text_wrap") ? (int)node.GetMeta("text_wrap") : 0;
		Vector2 box = node.HasMeta("text_box") ? (Vector2)node.GetMeta("text_box") : Vector2.Zero;
		Color colour = node.HasMeta("text_colour") ? (Color)node.GetMeta("text_colour") : Colors.White;

		// Opaque, as Engine_UiSceneBuilder builds text: the engine overwrites a .bgf's own text
		// alpha with 1.0, and the element's accumulated opacity is applied separately.
		var opaque = new Color(colour.R, colour.G, colour.B, 1f);

		Theme.Paint(
			node, display, size, opaque,
			horizontalAlign: horizontal, verticalAlign: vertical,
			boxWidth: box.X, boxHeight: box.Y, wrapMode: wrap);
	}

	/// <summary>
	/// A one-line description for a check tool's log.
	///
	/// It names the health bar's actual value rather than only the counts, because "the join
	/// works" and "the join works and the number is sensible" are different claims and a run
	/// that reports only totals cannot tell them apart.
	/// </summary>
	/// <param name="texts">
	/// What the guest published in the TEXT registry this frame, so the line can carry a
	/// DENOMINATOR for the text half the way it always has for meters.
	///
	/// Without it `texts=0` has two unrelated causes wearing one number: the guest published
	/// nothing, or it published names none of this screen's bindings match. Those need
	/// opposite investigations — the first is a question about the guest, the second about our
	/// hashing — and the meter half of this same line has always been able to tell them apart.
	/// </param>
	public string Describe(IReadOnlyDictionary<uint, float> meters,
		IReadOnlyDictionary<uint, string>? texts = null)
	{
		string health = meters.TryGetValue(PlayerHealthHash, out float value)
			? value.ToString("0.#") + "%"
			: "not published";

		return $"[ui-values] bars={_bars.Count} fields={_fields.Count} " +
			$"unresolved={BarsUnresolved} meters={MetersApplied}/{meters.Count} " +
			$"texts={TextsApplied} matched={FieldsMatched}/{_fields.Count} " +
			$"published={(texts is null ? "?" : texts.Count.ToString())} " +
			$"PlayerHealth={health}";
	}

	/// <summary>
	/// The health bar's name hash, for the report above.
	///
	/// Computed from the string rather than written as a literal, so it stays correct if the
	/// hash implementation is ever revised — and so this file states which STRING it means.
	/// </summary>
	public static readonly uint PlayerHealthHash = Refii.Core.Engine_GameHash.Fnv1("PlayerHealth");
}

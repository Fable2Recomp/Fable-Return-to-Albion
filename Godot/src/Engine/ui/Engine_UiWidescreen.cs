using System.Collections.Generic;
using Godot;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// Fable 2's own widescreen layout, applied in Godot — the reason HUD widgets sit in the wrong
/// place without it.
///
/// THE GAME HAS TWO LAYOUTS AND WE WERE DRAWING THE OTHER ONE. Every `.bgf` element is authored
/// at its 4:3 position, and the 16:9 position is carried BESIDE it as attributes the runtime
/// applies when the display is wide. Skip that step and the whole HUD renders in its standard-
/// definition arrangement on a widescreen canvas — which is why the ABXY prompt cluster sat in
/// the middle of the screen instead of its corner.
///
/// THE RULE IS THE SHIPPED LUA'S, read out of the bytecode rather than described. From
/// <c>hud/widescreenadapter.lua</c>, proto <c>main.1</c> — the handler registered on the
/// <c>setWidescreenMode</c> event from <c>Scene.UIContract</c> (<c>main.0</c>):
///
/// <code>
///  0  GETGLOBAL 'getElement'      -- r1 = getElement('parent', self.element)
///  4  GETGLOBAL 'getAttribute'    -- r2 = getAttribute(self.element, 'widescreenXPos')
///  8  EQ        A=1 B=2 C=261     -- skip the write when it EQUALS the sentinel
/// 10  GETGLOBAL 'setAttribute'    -- setAttribute(r1, 'position.x', r2)
/// 15..25                          -- the same for 'widescreenYPos'   -> 'position.y'
/// 26..36                          -- the same for 'WidescreenXWidth' -> 'scale.x'
/// </code>
///
/// Three things in that are easy to get wrong and are worth stating outright:
///
/// <list type="bullet">
/// <item><b>It writes the PARENT, not itself.</b> The adapter is a marker child — an element
/// called <c>WidescreenAdapter</c> carrying the numbers — and what moves is the element it
/// hangs under. Applying the offsets to the carrier would move a zero-sized marker and leave
/// the widget exactly where it was.</item>
/// <item><b>Zero means "no override", per attribute.</b> The `EQ` guards each write
/// separately, so an element may state a new x and keep its authored y. The HUD's
/// `PlayerHealth` is exactly that: <c>widescreenXPos -512, widescreenYPos 0,
/// WidescreenXWidth 0</c> — move it in x, leave y and width alone.</item>
/// <item><b>The value is ABSOLUTE, not a delta.</b> `setAttribute(parent, 'position.x', v)`
/// assigns. Adding it to the authored position would double the offset.</item>
/// </list>
///
/// IT MUST BE APPLIED AFTER THE GUEST'S LIVE TRANSFORMS, every frame, and that is not a
/// preference. The guest still believes it is in 4:3 — `GUI.State.IsWidescreen` defaults false
/// and `GUI:SetToDisplayInWidescreen()` has no Lua caller, so on a console the platform layer
/// called it and here nothing does — so it keeps restating the standard-definition position on
/// elements it animates. Applied once at build time, the next frame would undo it. Applied
/// last, this is the same "final word" the event handler has in the original.
///
/// WHY GODOT DECIDES THIS AT ALL: because Godot owns the display. Making the GUEST widescreen
/// would mean calling into it, which this bridge does not do — it reads and writes memory and
/// nothing else. Reading the layout the guest ships for a wide display and applying it here is
/// the same answer reached the way this port reaches things.
/// </summary>
public sealed class Engine_UiWidescreen
{
	/// <summary>
	/// One element's widescreen override, already resolved to the element it MOVES.
	///
	/// Nulls are the "no override" cases rather than zeroes, so a genuine authored zero — an
	/// element whose widescreen position really is the origin — stays distinguishable from an
	/// attribute the artist left blank. The file uses zero for both; this does not have to.
	/// </summary>
	public readonly record struct Override(int ParentOrdinal, float? X, float? Y, float? ScaleX);

	/// <summary>The attribute names, spelled as the shipped Lua spells them. Case is exact.</summary>
	public const string XAttribute = "widescreenXPos";
	public const string YAttribute = "widescreenYPos";
	public const string WidthAttribute = "WidescreenXWidth";

	/// <summary>
	/// The aspect above which the game counts as widescreen.
	///
	/// The two layouts are 4:3 (1.333) and 16:9 (1.778), and the file offers nothing between
	/// them — so anything comfortably wider than 4:3 takes the wide layout. Sitting the
	/// threshold midway rather than at 4:3 exactly keeps a window a pixel off square from
	/// flipping layouts.
	/// </summary>
	public const float WidescreenAspect = 1.55f;

	private readonly List<Override> _overrides = new();

	/// <summary>How many elements carry an override. Zero means this screen has no wide layout.</summary>
	public int Count => _overrides.Count;

	/// <summary>Every override, for a check tool that wants to report what moved.</summary>
	public IReadOnlyList<Override> Overrides => _overrides;

	/// <summary>
	/// Reads every widescreen override out of a parsed document.
	///
	/// Done once per build: the attributes are static file data, and only the APPLY has to
	/// happen per frame.
	/// </summary>
	public static Engine_UiWidescreen Read(Engine_BgfDocument document)
	{
		var table = new Engine_UiWidescreen();

		uint x = Engine_GameHash.AttributeId(XAttribute);
		uint y = Engine_GameHash.AttributeId(YAttribute);
		uint width = Engine_GameHash.AttributeId(WidthAttribute);

		for (int ordinal = 0; ordinal < document.Elements.Length; ordinal++)
		{
			BgfElement element = document.Elements[ordinal];

			float? overrideX = Stated(element, x);
			float? overrideY = Stated(element, y);
			float? overrideWidth = Stated(element, width);

			if (overrideX is null && overrideY is null && overrideWidth is null)
			{
				continue;
			}

			// THE PARENT IS WHAT MOVES. An adapter with no parent is a marker on the root, which
			// has nothing to move and is dropped rather than silently applied to itself.
			int parent = element.ParentOrdinal;
			if (parent < 0 || parent >= document.Elements.Length)
			{
				continue;
			}

			table._overrides.Add(new Override(parent, overrideX, overrideY, overrideWidth));
		}

		return table;
	}

	/// <summary>
	/// One attribute's value, or null when it is absent or is the zero sentinel.
	///
	/// The Lua's `EQ` guard is per attribute, so this is asked per attribute rather than the
	/// whole record being taken or dropped together.
	/// </summary>
	private static float? Stated(BgfElement element, uint id)
	{
		if (!element.TryGetAttribute(id, out BgfAttribute attribute))
		{
			return null;
		}

		float value = attribute.Type switch
		{
			BgfValueType.Float => attribute.AsFloat(),
			BgfValueType.Int1 or BgfValueType.Int2 => attribute.AsInt(),
			_ => 0f,
		};

		return value == 0f ? null : value;
	}

	/// <summary>
	/// Writes the wide layout onto the built nodes. Returns how many elements were moved.
	///
	/// Call AFTER the guest's live transforms each frame — see the class remarks for why once
	/// at build time is not enough.
	/// </summary>
	public int Apply(IReadOnlyList<Node3D?> nodes)
	{
		int moved = 0;

		foreach (Override entry in _overrides)
		{
			if (entry.ParentOrdinal >= nodes.Count || nodes[entry.ParentOrdinal] is not { } node)
			{
				continue;
			}

			Transform3D transform = node.Transform;
			Vector3 origin = transform.Origin;

			if (entry.X is { } x)
			{
				origin.X = x;
			}

			if (entry.Y is { } y)
			{
				origin.Y = y;
			}

			transform.Origin = origin;

			if (entry.ScaleX is { } scale)
			{
				// The x COLUMN, not `Scale` — a basis with a zero column reads back (0,0,0) on
				// every axis and a read-modify-write through the scale property would zero the
				// other two. See Engine_UiLiveValues.SetFillWidth, which is the same trap.
				Basis basis = transform.Basis;
				Vector3 axis = basis.Y.Cross(basis.Z);
				axis = axis.LengthSquared() > 0.000001f ? axis.Normalized() : new Vector3(1f, 0f, 0f);
				basis.X = axis * scale;
				transform.Basis = basis;
			}

			node.Transform = transform;
			++moved;
		}

		return moved;
	}

	/// <summary>
	/// The HUD's three NAMED widescreen fixups, from <c>hudpositioningandscaling.lua</c>.
	///
	/// SEPARATE FROM <see cref="Apply"/> BECAUSE THEY ARE NOT IDEMPOTENT, and that is the whole
	/// reason this is a different method rather than three more entries in the table. The
	/// per-element adapter ASSIGNS an absolute position, so running it every frame is harmless
	/// and necessary. This one DIVIDES what is already there:
	///
	/// <code>
	/// main.3  ScaleVignetteBorderToWidescreen
	///   r1 = g_HUD:GetScaleRatio()                       -- main.1 returns 0.75
	///   r2 = getElement('Layer.VignetteBorder.OpacityGroup.Component
	///                    .OpacityModifier.TopLeftCorner')
	///   r3 = getAttribute(r2, 'scale.x')
	///   DIV r3 = r3 / r1                                 -- x 1.3333
	///   setAttribute(r2, 'scale.x', r3)                  -- and the same value to the other
	///   ...TopRightCorner, ...BottomRightCorner, ...BottomLeftCorner
	/// </code>
	///
	/// Run that once per frame and the vignette grows by a third every frame until it swallows
	/// the screen. The original calls it once, from `g_HUD.onInitialize`; this is called once
	/// per BUILD, which is the same thing — a rebuild re-reads the authored scale from the file,
	/// so the division always starts from the artist's value.
	///
	/// The other two are plain assignments and would survive repetition, but they belong to the
	/// same moment and are kept with it:
	///
	/// <code>
	/// main.4  setAttribute('Layer.VignetteBorder.OpacityGroup.Component', 'opacity', 100)
	/// main.5  setAttribute('Layer.Screenshot', 'scale.x', 12.8)
	/// </code>
	///
	/// Returns how many landed. A path that does not resolve is reported rather than skipped
	/// silently: these are the only hardcoded element PATHS in the whole widescreen story, so a
	/// rename in the file would otherwise fail invisibly.
	///
	/// EXPECT `Layer.Screenshot` TO BE ABSENT, and do not go looking for it. `hud.bgf` contains
	/// no element of that name — checked across the whole file — so
	/// `ScaleScreenshotForWidescreen` is a shipped call against something the shipped HUD does
	/// not have. It is in good company in that chunk: `GetWidescreenOffset` returns 145.0 and
	/// has no callers anywhere in either script tree, and `RepositionPivot` has none either. So
	/// five applied out of six is the COMPLETE result on the HUD, not a near miss.
	/// </summary>
	public static int ApplyNamedFixups(Node3D root, out string report)
	{
		const string vignette = "Layer.VignetteBorder.OpacityGroup.Component";
		const string corners = vignette + ".OpacityModifier.";

		int applied = 0;
		var missing = new List<string>();

		// THE CORNERS FIRST, and all four take the value computed from the TOP LEFT one — the
		// script reads one corner and writes the result to all four rather than dividing each in
		// turn. Dividing each would give the same answer only while they start equal.
		if (Find(root, corners + "TopLeftCorner") is { } topLeft)
		{
			float widened = topLeft.Transform.Basis.X.Length() / ScaleRatio;

			foreach (string corner in
				new[] { "TopLeftCorner", "TopRightCorner", "BottomRightCorner", "BottomLeftCorner" })
			{
				if (Find(root, corners + corner) is { } node)
				{
					SetScaleX(node, widened);
					++applied;
				}
				else
				{
					missing.Add(corner);
				}
			}
		}
		else
		{
			missing.Add("TopLeftCorner");
		}

		if (Find(root, vignette) is { } opacityGroup)
		{
			// Authored 0..100 like every other opacity in a `.bgf`; the builder folds it into the
			// quad alpha, so this is written as the meta the fade path reads rather than onto a
			// material that the next frame's opacity pass would overwrite.
			opacityGroup.SetMeta("opacity", 1f);
			++applied;
		}
		else
		{
			missing.Add(vignette);
		}

		if (Find(root, "Layer.Screenshot") is { } screenshot)
		{
			SetScaleX(screenshot, ScreenshotScaleX);
			++applied;
		}
		else
		{
			missing.Add("Layer.Screenshot");
		}

		// NOTHING TO SAY ABOUT A SCREEN THAT DOES NOT USE THIS SCRIPT.
		//
		// These three fixups are `hudpositioningandscaling.lua`, which only `hud.bgf` loads —
		// so on any other presentation all of them are absent, and that is the correct and
		// expected result rather than a fault. Reporting it listed three missing element names
		// every time the pause menu was rebuilt, which is a log line that says the same thing
		// forever and reads like a defect the next time somebody greps for NOT FOUND.
		//
		// Told apart by whether ANY of them resolved rather than by a list of which screens use
		// the script, so a presentation that adopts it later is covered without an edit here.
		report = applied == 0
			? string.Empty
			: missing.Count == 0
				? $"[ui-widescreen] applied {applied} named fixup(s)"
				: $"[ui-widescreen] applied {applied} named fixup(s); NOT FOUND: " +
				  string.Join(", ", missing);

		return applied;
	}

	/// <summary><c>g_HUD:GetScaleRatio()</c> — `main.1` returns this and nothing else.</summary>
	public const float ScaleRatio = 0.75f;

	/// <summary><c>ScaleScreenshotForWidescreen</c>'s literal.</summary>
	public const float ScreenshotScaleX = 12.8f;

	/// <summary>
	/// Resolves one of the scripts' dotted element paths.
	///
	/// The FIRST segment is searched for anywhere under the root and the rest walked as direct
	/// children. Gameface resolves these from the SCENE, and our built tree puts the scene one
	/// level down under `Component_0` — so anchoring on the first segment rather than on the
	/// root is what makes the script's own path work against our tree without restating it.
	/// </summary>
	private static Node3D? Find(Node root, string path)
	{
		string[] segments = path.Split('.');
		if (segments.Length == 0)
		{
			return null;
		}

		if (Engine_UiTheme.FindByName(root, segments[0]) is not Node3D at)
		{
			return null;
		}

		for (int i = 1; i < segments.Length; i++)
		{
			Node? next = null;
			foreach (Node child in at.GetChildren())
			{
				if (child.Name == segments[i])
				{
					next = child;
					break;
				}
			}

			if (next is not Node3D found)
			{
				return null;
			}

			at = found;
		}

		return at;
	}

	/// <summary>
	/// Sets an element's x scale without going through <c>Node3D.Scale</c>.
	///
	/// Same trap as <see cref="Engine_UiLiveValues"/>: a `.bgf` element may carry a singular
	/// basis, and the scale getter reports (0,0,0) for every axis when it does, so a
	/// read-modify-write through it zeroes the other two.
	/// </summary>
	private static void SetScaleX(Node3D node, float scaleX)
	{
		Transform3D transform = node.Transform;
		Basis basis = transform.Basis;

		Vector3 axis = basis.Y.Cross(basis.Z);
		axis = axis.LengthSquared() > 0.000001f ? axis.Normalized() : new Vector3(1f, 0f, 0f);

		basis.X = axis * scaleX;
		transform.Basis = basis;
		node.Transform = transform;
	}

	/// <summary>Whether a viewport of this size takes the wide layout.</summary>
	public static bool IsWidescreen(Vector2 viewport) =>
		viewport.Y > 0f && (viewport.X / viewport.Y) >= WidescreenAspect;

	/// <summary>A one-line description for a check tool's log.</summary>
	public string Describe() => $"[ui-widescreen] {_overrides.Count} element(s) carry a wide layout";
}

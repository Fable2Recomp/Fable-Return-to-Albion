using System.Collections.Generic;
using Godot;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// Fable 2's own widescreen layout, applied in Godot. Every `.bgf` element is authored at its
/// 4:3 position with the 16:9 position carried beside it as attributes.
///
/// The rule is <c>hud/widescreenadapter.lua</c> proto <c>main.1</c>, the handler on
/// <c>Scene.UIContract</c>'s <c>setWidescreenMode</c>:
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
/// <list type="bullet">
/// <item>It writes the PARENT: the adapter is a zero-sized marker child carrying the numbers.</item>
/// <item>Zero means "no override" PER ATTRIBUTE — the `EQ` guards each write separately, so an
/// element may state a new x and keep its authored y.</item>
/// <item>The value is ABSOLUTE, not a delta; adding it would double the offset.</item>
/// </list>
///
/// This is a modernization, not a restoration: `g_WidescreenMode` at `0x8336032C` has 31 xrefs,
/// all reads, initial dword 0, and it gates both `GUI:SetToDisplayInWidescreen()`
/// (0x823DCD44) and the only `setWidescreenMode` raise (0x823CA668) — so on TU 0.0.1.26 the
/// adapter never runs and all 17 `WidescreenAdapter` markers in `hud.bgf` are dead data.
/// Expect a difference from a console capture.
///
/// Apply AFTER the guest's live transforms, every frame: the guest believes it is in 4:3 and
/// keeps restating the standard-definition position on elements it animates.
/// </summary>
public sealed class Engine_UiWidescreen
{
	/// <summary>
	/// One element's widescreen override, resolved to the element it MOVES. Null is "no
	/// override"; the file spells that as zero, which also collides with a real origin.
	/// </summary>
	public readonly record struct Override(int ParentOrdinal, float? X, float? Y, float? ScaleX);

	/// <summary>The attribute names, spelled as the shipped Lua spells them. Case is exact.</summary>
	public const string XAttribute = "widescreenXPos";
	public const string YAttribute = "widescreenYPos";
	public const string WidthAttribute = "WidescreenXWidth";

	/// <summary>
	/// The aspect above which the game counts as widescreen. The two layouts are 4:3 (1.333) and
	/// 16:9 (1.778) with nothing between; midway keeps a near-4:3 window from flipping.
	/// </summary>
	public const float WidescreenAspect = 1.55f;

	private readonly List<Override> _overrides = new();

	/// <summary>How many elements carry an override. Zero means this screen has no wide layout.</summary>
	public int Count => _overrides.Count;

	/// <summary>Every override, for a check tool that wants to report what moved.</summary>
	public IReadOnlyList<Override> Overrides => _overrides;

	/// <summary>
	/// Reads every widescreen override out of a parsed document. Once per build — the attributes
	/// are static file data; only <see cref="Apply"/> runs per frame.
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

			// The parent is what moves; an adapter on the root has nothing to move, so drop it.
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
	/// One attribute's value, or null when absent or the zero sentinel. Asked per attribute
	/// because the Lua's `EQ` guard is per attribute.
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
	/// Writes the wide layout onto the built nodes; returns how many moved. Call after the
	/// guest's live transforms each frame.
	/// </summary>
	public int Apply(Engine_UiSceneBuilder builder)
	{
		IReadOnlyList<Node3D?> nodes = builder.ByOrdinal;
		int moved = 0;

		foreach (Override entry in _overrides)
		{
			if (entry.ParentOrdinal >= nodes.Count || nodes[entry.ParentOrdinal] is not { } node)
			{
				continue;
			}

			// A position, not an origin: `widescreenXPos` is the same kind of value the guest
			// states live, so it goes through the writer that subtracts the pivot term the
			// builder folded into the origin.
			builder.SetLivePosition(entry.ParentOrdinal, entry.X, entry.Y);

			Transform3D transform = node.Transform;

			if (entry.ScaleX is { } scale)
			{
				// The x COLUMN, not `Scale`: a basis with a zero column reads back (0,0,0) on every
				// axis, so a read-modify-write through the property zeroes the other two.
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
	/// The HUD's three named widescreen fixups, from <c>hudpositioningandscaling.lua</c>.
	/// Separate from <see cref="Apply"/> because they are NOT idempotent — `main.3` DIVIDES the
	/// scale already there:
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
	/// main.4  setAttribute('Layer.VignetteBorder.OpacityGroup.Component', 'opacity', 100)
	/// main.5  setAttribute('Layer.Screenshot', 'scale.x', 12.8)
	/// </code>
	///
	/// Call once per BUILD, as the original calls it once from `g_HUD.onInitialize`; a rebuild
	/// re-reads the authored scale, so the division always starts from the artist's value.
	/// Returns how many landed. All six resolve on the HUD — four corners, the opacity group and
	/// `Layer.Screenshot` (`hud.bgf` ord=576, a direct child of `Layer`) — so an unresolved path
	/// is reported rather than skipped. The chunk's other two functions, `GetWidescreenOffset`
	/// (`main.0`) and `RepositionPivot` (`main.2`), have no callers in either script tree.
	/// </summary>
	public static int ApplyNamedFixups(Node3D root, out string report)
	{
		const string vignette = "Layer.VignetteBorder.OpacityGroup.Component";
		const string corners = vignette + ".OpacityModifier.";

		int applied = 0;
		var missing = new List<string>();

		// All four corners take the value computed from the TOP LEFT one, as the script does;
		// dividing each in turn agrees only while they start equal.
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
			// Authored 0..100 like every `.bgf` opacity; written as the meta the fade path reads,
			// not onto a material the next frame's opacity pass would overwrite.
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

		// Only `hud.bgf` loads `hudpositioningandscaling.lua`, so on any other presentation all
		// three are absent and that is correct, not a fault. Told apart by whether ANY resolved,
		// so a presentation that adopts the script later is covered without an edit here.
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
	/// Resolves one of the scripts' dotted element paths — see
	/// <see cref="Engine_UiTheme.FindByPath"/>, which is the one implementation of it.
	/// </summary>
	private static Node3D? Find(Node root, string path) =>
		Engine_UiTheme.FindByPath(root, path);

	/// <summary>
	/// Sets an element's x scale without going through <c>Node3D.Scale</c> — a `.bgf` element may
	/// carry a singular basis, whose scale getter reports (0,0,0) on every axis.
	/// </summary>
	private static void SetScaleX(Node3D node, float scaleX) =>
		Engine_UiBasisScale.SetX(node, scaleX);

	/// <summary>Whether a viewport of this size takes the wide layout.</summary>
	public static bool IsWidescreen(Vector2 viewport) =>
		viewport.Y > 0f && (viewport.X / viewport.Y) >= WidescreenAspect;

	/// <summary>A one-line description for a check tool's log.</summary>
	public string Describe() => $"[ui-widescreen] {_overrides.Count} element(s) carry a wide layout";
}

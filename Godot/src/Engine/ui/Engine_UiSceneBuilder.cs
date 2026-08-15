using System;
using System.Collections.Generic;
using Godot;

using Refii.Core;

using Refii.Text;

using Refii.Textures;

namespace Refii.Ui;

/// <summary>
/// Turns a <see cref="Engine_UiScreen"/> into a Godot node tree.
///
/// Fable 2's UI is a lit 3D scene — every screen carries a camera and lights — so the
/// output is <see cref="Node3D"/>-based rather than a Control tree. Building it through
/// Godot's own API (instead of writing <c>.tscn</c> text) means the engine does the
/// serialising: call <see cref="SaveAsScene"/> to get a real scene file out.
///
/// The presentation's <c>node → material → texture</c> chain is collapsed into a single
/// <see cref="MeshInstance3D"/>. That is safe because material and texture elements are
/// effectively properties, not structure: across all 52 shipped screens a texture is
/// always a leaf and a material has at most one child.
/// </summary>
public sealed class Engine_UiSceneBuilder
{
	/// <summary>
	/// Supplies a texture for a resolved asset path such as <c>art\gui\frames\frames_04.tex</c>.
	/// Return null and the quad still builds, just untextured — useful before a
	/// <c>.tex</c> decoder exists.
	/// </summary>
	public Func<string, Texture2D?>? TextureResolver;

	/// <summary>
	/// Supplies the bitmap font for a text element's point size. Return null and the
	/// text element still builds, carrying its placement metadata but drawing nothing —
	/// which is what the builder did for every string before fonts were wired in.
	/// </summary>
	public Func<int, (UiFont Font, float Scale)?>? FontResolver;

	/// <summary>
	/// Authored rotations are treated as degrees. Anark's tooling authored in degrees and
	/// the shipped values are consistent with that, but it has not been confirmed against
	/// a rendered frame — if screens come out rotated oddly, this is the first thing to try.
	/// </summary>
	public bool RotationsAreDegrees = true;

	/// <summary>Emit <see cref="BgfElementType.Behavior"/> elements as marker nodes.</summary>
	public bool IncludeBehaviors = true;

	/// <summary>
	/// Honour the slide baseline. On, a screen renders the state it was authored to
	/// show; off, it renders the authored SUPERSET — every stacked variant at once,
	/// which is useful for seeing what a screen contains but is not what the game shows.
	/// </summary>
	public bool ApplySlideVisibility = true;

	/// <summary>
	/// Authored scales are relative to a 100-unit quad — it spans -50..+50 — not a unit
	/// one. The <c>pivot</c> attribute is in the same units, which is why the shipped
	/// values are exactly ±50: they anchor a slice to its own edge.
	///
	/// Solved from menubox's <c>top</c> band, whose three slices share scale.x = 0.256
	/// and sit at pos.x = 0, 12.80, 25.60 with pivot.x = +50, 0, -50. Requiring the left
	/// slice's right edge to meet the middle's left edge, for a quad spanning ±H:
	///
	///     (H - 50)(0.256) = -H(0.256) + 12.8   =>   0.512H = 25.6   =>   H = 50
	///
	/// giving extents [-25.6, 0], [0, 25.6], [25.6, 51.2] — exactly edge to edge.
	///
	/// This holds ONLY with the pivot term in <see cref="BuildTransform"/>. Without it
	/// the slices overlap at 100, and a base of 50 appears to fix them because halving
	/// the quad happens to cancel the missing pivot offset for this band. That is a
	/// coincidence, not a derivation, and it fails as soon as a pivot is not ±50.
	/// </summary>
	public const float QuadBaseSize = 100f;

	private static readonly ArrayMesh SharedQuad = BuildQuadMesh(QuadBaseSize);

	/// <summary>
	/// Builds the shared quad. A plain one: U increases with +X, V downward from the
	/// top-left, normal toward +Z where <see cref="BuildTransform"/> puts the viewer.
	///
	/// No mirror is baked in here. Compensating in the mesh for a camera whose screen-right
	/// points at world -X fixes textures and leaves GEOMETRY mirrored: menubox's A/B prompts
	/// render bottom-LEFT, and the two halves of a paper panel swap so both their outer
	/// borders meet in the middle as a dark seam. The handedness belongs in the transform,
	/// once.
	/// </summary>
	private static ArrayMesh BuildQuadMesh(float size)
	{
		float h = size * 0.5f;

		// Wound counter-clockwise as seen from +Z, so the front face is the one the
		// viewer is on. Materials disable culling anyway; this keeps normals honest.
		var vertices = new[]
		{
			new Vector3(-h, +h, 0f),
			new Vector3(-h, -h, 0f),
			new Vector3(+h, -h, 0f),
			new Vector3(+h, +h, 0f),
		};
		var uvs = new[]
		{
			new Vector2(0f, 0f),
			new Vector2(0f, 1f),
			new Vector2(1f, 1f),
			new Vector2(1f, 0f),
		};
		var normals = new[]
		{
			// Facing the viewer, so lighting behaves as authored.
			new Vector3(0f, 0f, 1f),
			new Vector3(0f, 0f, 1f),
			new Vector3(0f, 0f, 1f),
			new Vector3(0f, 0f, 1f),
		};
		var indices = new[] { 0, 1, 2, 0, 2, 3 };

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.TexUV] = uvs;
		arrays[(int)Mesh.ArrayType.Normal] = normals;
		arrays[(int)Mesh.ArrayType.Index] = indices;

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}

	public int QuadsBuilt { get; private set; }

	/// <summary>Quads whose atlas rectangle has no area — see <see cref="UiQuad.IsDegenerate"/>.</summary>
	public int DegenerateQuads { get; private set; }
	public int TexturesResolved { get; private set; }
	public int TextsBuilt { get; private set; }

	/// <summary>Glyph quads emitted. Zero with a non-zero <see cref="TextsBuilt"/> means no font resolved.</summary>
	public int TextGlyphsBuilt { get; private set; }

	/// <summary>Elements dropped by the slide baseline, subtrees included.</summary>
	public int SkippedInactive { get; private set; }

	/// <summary>
	/// The built node for each element ORDINAL, or null where the slide baseline dropped one.
	///
	/// This is what lets a live screen be UPDATED rather than rebuilt. Everything the guest
	/// states arrives keyed by ordinal — slides, visibility, opacity, transforms — and without a
	/// way back from an ordinal to its node, the only way to apply any of it was to build the
	/// whole tree again. That is why a presentation which merely MOVES never moved: the change
	/// test could not afford to notice, because noticing meant rebuilding 3812 elements.
	///
	/// Empty until <see cref="Build"/> has run.
	/// </summary>
	public Node3D?[] ByOrdinal { get; private set; } = System.Array.Empty<Node3D?>();

	public Node3D Build(Engine_UiScreen screen)
	{
		QuadsBuilt = TexturesResolved = TextsBuilt = DegenerateQuads = TextGlyphsBuilt = 0;
		_inheritedOpacity = 1f;
		ByOrdinal = new Node3D?[screen.Nodes.Length];

		var root = new Node3D { Name = SanitizeName(screen.Name) };
		root.SetMeta("canvas_width", screen.CanvasWidth);
		root.SetMeta("canvas_height", screen.CanvasHeight);
		if (screen.ContractNames.Count > 0)
			root.SetMeta("contract", new Godot.Collections.Array(
				screen.ContractNames.ConvertAll(n => Variant.From(n))));

		foreach (UiNode n in screen.Roots)
		{
			Node3D? child = BuildNode(n);
			if (child is not null)
				root.AddChild(child);
		}
		return root;
	}

	/// <summary>
	/// Writes the guest's live transforms onto a tree that is already built, and reports how
	/// many elements actually moved.
	///
	/// THIS IS WHY THE BACKGROUND DID NOT SCROLL. The front end's backdrop is animated by the
	/// guest every frame — `sub_82990730` advances the `Foreground.Book` sub-object, and the
	/// ambient presentation's `"Scrolling"` element is driven the same way — and every one of
	/// those is an attribute write, not a slide or a visibility change. The only way we had to
	/// pick up a moved element was to rebuild the presentation, so the change test deliberately
	/// ignored transforms and a screen that merely moved never did.
	///
	/// Rebuilding is the wrong tool for it anyway: the tree is identical apart from a handful of
	/// transforms, and rebuilding 94 nodes sixty times a second to slide a backdrop — or 3431 to
	/// nudge a HUD counter — is work with nothing to show for it.
	///
	/// <paramref name="transform"/> is keyed by ordinal, with a null row where the guest states
	/// nothing for that element, in which case the file's own transform stands.
	/// </summary>
	/// <summary>The unit vector for a basis column index, for a basis with nothing left to keep.</summary>
	private static Vector3 UnitAxis(int axis) => axis switch
	{
		0 => new Vector3(1f, 0f, 0f),
		1 => new Vector3(0f, 1f, 0f),
		_ => new Vector3(0f, 0f, 1f),
	};

	public int ApplyLiveTransform(System.Collections.Generic.IReadOnlyList<float?[]?> transform)
	{
		int moved = 0;

		for (int i = 0; i < ByOrdinal.Length && i < transform.Count; ++i)
		{
			if (ByOrdinal[i] is not { } node || transform[i] is not { } values)
			{
				continue;
			}

			Transform3D before = node.Transform;
			Vector3 position = before.Origin;

			if (values.Length > 0 && values[0] is { } x) position.X = x;
			if (values.Length > 1 && values[1] is { } y) position.Y = y;
			if (values.Length > 2 && values[2] is { } z) position.Z = z;

			// SCALE THROUGH THE BASIS COLUMNS, never through `node.Scale`.
			//
			// `Basis.Scale` is derived, and on a SINGULAR basis it lies: Godot recovers each
			// axis length and multiplies by the determinant's sign, so a basis with one zero
			// column reports (0,0,0) for ALL THREE axes — the determinant is zero and
			// `Mathf.Sign(0)` is 0. Read-modify-write of that property therefore takes the two
			// axes the guest did NOT state down to zero along with it, and the property reads
			// back the value just written, so the collapse is invisible to a log.
			//
			// This is not hypothetical on this screen set. Every meter bar in `hud.bgf` is
			// authored `scale.x = 0` — an empty bar the fill animation opens — so any of them
			// receiving a live scale on one axis had the other two flattened. The same trap has
			// already cost this project two turns elsewhere; `Engine_UiWidescreen.SetScaleX`
			// writes columns for exactly this reason.
			//
			// A zero column also has no direction left to scale along, so the axis is rebuilt
			// from the cross product of the other two — which keeps the basis right-handed
			// rather than picking an arbitrary unit vector.
			Basis basis = before.Basis;
			bool scaled = false;
			for (int axis = 0; axis < 3; ++axis)
			{
				if (values.Length <= 3 + axis || values[3 + axis] is not { } length)
				{
					continue;
				}

				Vector3 column = basis[axis];
				if (column.LengthSquared() <= 0.000001f)
				{
					column = basis[(axis + 1) % 3].Cross(basis[(axis + 2) % 3]);
				}

				basis[axis] = column.LengthSquared() > 0.000001f
					? column.Normalized() * length
					: UnitAxis(axis) * length;
				scaled = true;
			}

			if (!scaled && before.Origin == position)
			{
				continue;
			}

			var after = new Transform3D(basis, position);
			if (after == before)
			{
				continue;
			}

			node.Transform = after;
			++moved;
		}

		return moved;
	}

	/// <summary>
	/// Opacity accumulated from the root down to the element being built, 0..1.
	///
	/// OPACITY IS A CHAIN, not a per-element value, and treating it as one is how a hidden HUD
	/// stays on screen. The engine multiplies down the tree — <c>sub_8229E250</c> takes the
	/// parent frame's accumulated value, multiplies by this element's own <c>opacity/100</c>,
	/// and stores the product on the render association — so an element at 100 under a group at
	/// 0 draws at 0.
	///
	/// That is exactly how the game hides the whole HUD: <c>hud.lua</c>'s <c>Enable</c>/
	/// <c>Disable</c> is <c>setAttribute(getElement('Scene.Layer'), 'opacity', 100 | 0)</c> on
	/// ONE group, and every element beneath it must go with it.
	///
	/// It is NOT a visibility gate, which is the other half of the trap. The traversal
	/// <c>sub_822849A0</c> never reads opacity, so at 0 the subtree is still walked, still
	/// reports <c>globalactive</c> true, and is still submitted — just at alpha 0. Collapsing
	/// it to <c>Visible = false</c> would give the right pixels and the wrong element state.
	/// </summary>
	private float _inheritedOpacity = 1f;

	private Node3D? BuildNode(UiNode n)
	{
		// Material and texture elements carry parameters for the quad on their parent
		// node; they are folded in there rather than emitted.
		if (n.Type is BgfElementType.Material or BgfElementType.Texture)
			return null;

		if (n.Type == BgfElementType.Behavior && !IncludeBehaviors)
			return null;

		// An inactive element takes its whole subtree with it. sub_8229E400 tests the
		// cached globalactive byte and branches straight to the next SIBLING, so no
		// descendant is ever visited — returning null here reproduces that exactly.
		if (ApplySlideVisibility && !n.Visible)
		{
			SkippedInactive++;
			return null;
		}

		// This element's own opacity folded into the chain, BEFORE the node is created —
		// CreateNodeFor builds the quad and needs the accumulated alpha, not the local one.
		float parentOpacity = _inheritedOpacity;
		_inheritedOpacity = parentOpacity * (n.Opacity / 100f);

		Node3D node = CreateNodeFor(n);
		node.Name = SanitizeName(n.Name);
		node.Transform = BuildTransform(n);

		if (n.Ordinal >= 0 && n.Ordinal < ByOrdinal.Length)
			ByOrdinal[n.Ordinal] = node;

		node.SetMeta("object_id", n.ObjectId);
		node.SetMeta("element_type", n.Type.ToString());
		if (!n.HasAuthoredName)
			node.SetMeta("name_synthesised", true);
		if (n.Opacity < 100f)
			node.SetMeta("opacity", n.Opacity / 100f);
		// The accumulated value too, and separately: a consumer asking "why is this invisible"
		// needs to tell "this element is transparent" from "something above it is".
		if (_inheritedOpacity < 1f)
			node.SetMeta("opacity_accumulated", _inheritedOpacity);
		// And what was inherited FROM ABOVE, which is the one of the three that cannot be
		// derived from the other two. The scripts set opacity on a GROUP —
		// `setAttribute(Tab.InnerRed, 'opacity', 100)` is how a row picks its face, and
		// `hud.lua` disables a whole layer the same way — so a host that owns a screen has to be
		// able to re-run that. Recomputing a subtree needs the factor above the group, and
		// dividing the accumulated by the group's own value cannot recover it: the case worth
		// changing is precisely the one where the group sits at zero.
		if (parentOpacity < 1f)
			node.SetMeta("opacity_inherited", parentOpacity);

		foreach (UiNode child in n.Children)
		{
			Node3D? built = BuildNode(child);
			if (built is not null)
				node.AddChild(built);
		}

		// Restore on the way back up. The walk is depth-first over siblings, so leaving this
		// at the child's value would make every later sibling inherit its transparency.
		_inheritedOpacity = parentOpacity;
		return node;
	}

	private Node3D CreateNodeFor(UiNode n)
	{
		switch (n.Type)
		{
			case BgfElementType.Camera:
			{
				var cam = new Camera3D();
				if (n.Element.TryGetAttribute("fov", out var fov) && fov.Type == BgfValueType.Float)
					cam.Fov = fov.AsFloat();
				if (n.Element.TryGetAttribute("clipnear", out var cn) && cn.Type == BgfValueType.Float && cn.AsFloat() > 0f)
					cam.Near = cn.AsFloat();
				if (n.Element.TryGetAttribute("clipfar", out var cf) && cf.Type == BgfValueType.Float && cf.AsFloat() > 0f)
					cam.Far = cf.AsFloat();
				if (n.Element.TryGetAttribute("orthographic", out var ortho) && ortho.AsBool())
					cam.Projection = Camera3D.ProjectionType.Orthogonal;

				// Cameras are aimed by an explicit look-at point, not by their own rotation.
				// Without this the camera keeps Godot's default -Z heading and faces away
				// from the content, which renders an empty frame.
				if (TryReadVector(n, "lookatpoint", out Vector3 target))
					cam.SetMeta("lookatpoint", new Vector3(target.X, target.Y, ToGodotZ(target.Z)));

				cam.Current = false;
				return cam;
			}

			case BgfElementType.Light:
			{
				// Every shipped light is near-white and local in extent; omni is the closest
				// match without a decoded light-type attribute.
				var light = new OmniLight3D();
				light.LightColor = ReadColour(n, "lightdiffuse", Colors.White);
				if (n.Element.TryGetAttribute("brightness", out var b) && b.Type == BgfValueType.Float)
					light.LightEnergy = b.AsFloat();
				return light;
			}

			case BgfElementType.Text:
				return BuildTextNode(n);

			default:
				return n.Quad is not null ? BuildQuad(n) : new Node3D();
		}
	}

	private Node3D BuildTextNode(UiNode n)
	{
		if (n.Text is null)
			return new Node3D();

		UiText text = n.Text;
		TextsBuilt++;

		// A MeshInstance3D even when nothing draws, so the metadata always hangs off the
		// same node type and a consumer need not special-case the empty string.
		var node = new MeshInstance3D();
		node.SetMeta("text", text.Display);
		node.SetMeta("text_tag", text.Tag);
		node.SetMeta("text_localised", text.Localised);
		node.SetMeta("text_size", text.Size);
		node.SetMeta("text_halign", text.HorzAlign);
		node.SetMeta("text_valign", text.VertAlign);
		node.SetMeta("text_wrap", text.WrapMode);
		node.SetMeta("text_box", new Vector2(text.BoxWidth, text.BoxHeight));
		node.SetMeta("text_colour", new Color(text.R, text.G, text.B));

		if (FontResolver?.Invoke(text.Size) is not { } selection)
			return node;

		(UiFont font, float scale) = selection;
		ArrayMesh? mesh = Engine_UiTextMesh.Build(text, font.Font, scale, out int lines);
		if (mesh is null)
			return node;

		node.Mesh = mesh;
		// The AUTHORED alpha is overwritten with 1.0 by the engine — a .bgf's own text colour
		// is always opaque. The opacity CHAIN is a different value and does apply: it is
		// accumulated on the render association, which text elements have like any other, so a
		// label under a group faded to 0 fades with it. Conflating the two is what left HUD
		// text on screen after the game had hidden the HUD.
		node.MaterialOverride =
			TextMaterial(new Color(text.R, text.G, text.B, _inheritedOpacity), font.Atlas);

		node.SetMeta("text_lines", lines);
		TextGlyphsBuilt += mesh.SurfaceGetArrayLen(0) / 4;
		return node;
	}

	/// <summary>
	/// The material every piece of UI text is drawn with, whether it came out of a
	/// <c>.bgf</c> or was authored natively.
	///
	/// Shared rather than repeated because the two halves of it are not free choices and a
	/// second copy drifts. Unshaded, because a glyph atlas is emissive art and shading it
	/// against the screen's authored lights leaves the text black. And an alpha SCISSOR
	/// rather than plain alpha blending, because the atlas is padded with transparent black:
	/// without the clip, the bilinear tail of the neighbouring glyph cell shows as a halo
	/// around every character.
	///
	/// <paramref name="colour"/> carries its own alpha. The scene builder always passes 1
	/// (see <see cref="BuildText"/>); a host-authored screen can fade a label by passing
	/// less, which is what the front-end menu's gutter slots need.
	/// </summary>
	public static StandardMaterial3D TextMaterial(Color colour, Texture2D? atlas) => new()
	{
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		AlbedoTexture = atlas,
		AlbedoColor = colour,
		AlphaScissorThreshold = 0.01f,
	};

	private MeshInstance3D BuildQuad(UiNode n)
	{
		UiQuad q = n.Quad!;
		QuadsBuilt++;
		if (q.IsDegenerate)
			DegenerateQuads++;

		var mesh = new MeshInstance3D { Mesh = SharedQuad };
		var mat = new StandardMaterial3D
		{
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			// CLAMPED, NOT REPEATED — this is what closes the seams between tiling panels.
			// The front end's backdrop is a 7x2 grid of 512x512 quads, each sampling its own
			// texture across the full 0..1, laid edge to edge. With the default repeat wrap the
			// bilinear tap at u=0 and u=1 blends with the OPPOSITE edge of the same texture, so
			// every panel boundary draws a one-pixel line of the wrong scenery. No UI quad wants
			// to tile: a repeated one would be a bug in the .fac, not a feature.
			TextureRepeat = false,
			// A UI atlas is emissive art, not a lit surface. Shading it would make the
			// screen depend on the authored lights, which are positioned for the game's
			// own renderer and leave the panel black here.
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			// The ACCUMULATED alpha, not this element's own. _inheritedOpacity already has
			// n.Opacity folded in by BuildNode, so this is the product down the whole chain.
			AlbedoColor = new Color(q.R, q.G, q.B, _inheritedOpacity),
			// The atlas rectangle rides on the material rather than custom mesh UVs, so
			// every quad can share one QuadMesh.
			Uv1Scale = new Vector3(q.UMax - q.UMin, q.VMax - q.VMin, 1f),
			Uv1Offset = new Vector3(q.UMin, q.VMin, 0f),
		};

		if (q.TexturePath.Length > 0)
		{
			Texture2D? tex = TextureResolver?.Invoke(q.TexturePath);
			if (tex is not null)
			{
				mat.AlbedoTexture = tex;
				TexturesResolved++;

				// HALF A TEXEL OFF EVERY EDGE — the seams that clamping cannot close.
				//
				// Clamping fixed the panorama, whose panels each own a whole texture. The
				// frames do not: the pillars, rims and tabs are CELLS of a shared atlas
				// (`frames_elements.tex`, `frames_04.tex`), selected by a sub-rect. A bilinear
				// tap on the boundary row of a cell reaches into the cell NEXT to it, so every
				// piece of frame art draws a hairline of its neighbour down the join — and no
				// wrap mode helps, because the sampler never leaves the texture.
				//
				// Insetting to texel CENTRES is the standard remedy and costs half a texel of
				// the cell's outermost row, which at these sizes is well under a screen pixel.
				// Skipped for a degenerate rect, where there is no inside to move into.
				// READ AT BUILD TIME, and the texture can grow afterwards: an enhanced UI texture
				// has its image replaced in place, so a material built at 512 keeps an inset sized
				// for 512 while sampling 1024. That over-insets by half an ORIGINAL texel — it
				// crops very slightly more of the cell rather than letting the neighbour bleed in,
				// so it errs the safe way and is well under a screen pixel either way. Worth
				// knowing before anyone measures a cell edge and finds it half a texel tight.
				//
				// Nothing else here depends on the texture's size. The quad is the shared mesh
				// scaled by the node transform, so an enhancement changes resolution and not
				// layout.
				float du = 0.5f / Mathf.Max(tex.GetWidth(), 1);
				float dv = 0.5f / Mathf.Max(tex.GetHeight(), 1);
				float uSpan = q.UMax - q.UMin;
				float vSpan = q.VMax - q.VMin;

				if (Mathf.Abs(uSpan) > 2f * du && Mathf.Abs(vSpan) > 2f * dv)
				{
					// Signed, so a flipped rect (UMax < UMin) is inset inward too rather than
					// being widened into its neighbours.
					float uStep = Mathf.Sign(uSpan) * du;
					float vStep = Mathf.Sign(vSpan) * dv;

					mat.Uv1Offset = new Vector3(q.UMin + uStep, q.VMin + vStep, 0f);
					mat.Uv1Scale = new Vector3(uSpan - (2f * uStep), vSpan - (2f * vStep), 1f);
				}
			}
			mesh.SetMeta("texture_path", q.TexturePath);
		}
		mesh.SetMeta("texture_hash", q.TextureHash);
		if (q.IsDegenerate)
			mesh.SetMeta("degenerate_uv", true);

		mesh.MaterialOverride = mat;
		return mesh;
	}

	private Transform3D BuildTransform(UiNode n)
	{
		var basis = Basis.Identity;
		if (n.RotationX != 0f || n.RotationY != 0f || n.RotationZ != 0f)
		{
			float sx = n.RotationX, sy = n.RotationY, sz = n.RotationZ;
			if (RotationsAreDegrees)
			{
				sx = Mathf.DegToRad(sx);
				sy = Mathf.DegToRad(sy);
				sz = Mathf.DegToRad(sz);
			}
			// Handedness, half one: conjugating a rotation by diag(1,1,-1) negates the
			// angles about X and Y and leaves the one about Z alone.
			basis = Basis.FromEuler(new Vector3(-sx, -sy, sz));
		}
		basis = basis.Scaled(new Vector3(n.ScaleX, n.ScaleY, n.ScaleZ));

		// The engine's local transform is  ((p - pivot) * scale) * R + position  —
		// sub_8229E4B8 seeds the matrix translation with -(scale * pivot) BEFORE
		// concatenating the rotation, then adds position after. In column-vector form
		// that is  T(position) * R * S * T(-pivot), i.e. the origin carries an extra
		// -(R*S)*pivot term.
		//
		// Dropping it displaces every element by pivot*scale in accumulated space.
		// Because the shipped slices have pivot.x = 0 with a non-zero pivot.y, the
		// symptom is specifically perfect horizontal tiling with vertical gaps.
		var pivot = new Vector3(n.PivotX, n.PivotY, ToGodotZ(n.PivotZ));
		var origin = new Vector3(n.PositionX, n.PositionY, ToGodotZ(n.PositionZ)) - basis * pivot;
		return new Transform3D(basis, origin);
	}

	/// <summary>
	/// Handedness, half two: Fable authors UI in a LEFT-handed space — X right, Y up, Z
	/// away from the viewer — which is why every screen's camera sits at negative Z and
	/// looks toward +Z. Godot is right-handed, so the conversion is to negate Z.
	///
	/// Negating Z rather than X is what keeps the screen the right way round. Reflecting
	/// through the other axis also produces a camera that can see the content — it
	/// renders, it just renders mirrored, and a symmetric panel gives that away only at
	/// the details: the A/B prompts on the wrong side, and a two-piece paper panel
	/// showing both its outer borders down the middle.
	///
	/// Applied per node rather than as one flip on the root, so no transform in the tree
	/// ends up with a negative determinant.
	/// </summary>
	private static float ToGodotZ(float fableZ) => -fableZ;

	/// <summary>Reads an <c>x/y/z</c> attribute triple, if all three are present.</summary>
	public static bool TryReadVector(UiNode n, string prefix, out Vector3 value)
	{
		value = Vector3.Zero;
		if (!n.Element.TryGetAttribute(prefix + ".x", out var ax) || ax.Type != BgfValueType.Float)
			return false;
		if (!n.Element.TryGetAttribute(prefix + ".y", out var ay) || ay.Type != BgfValueType.Float)
			return false;
		if (!n.Element.TryGetAttribute(prefix + ".z", out var az) || az.Type != BgfValueType.Float)
			return false;
		value = new Vector3(ax.AsFloat(), ay.AsFloat(), az.AsFloat());
		return true;
	}

	/// <summary>
	/// Aims every camera in a built tree at its authored look-at point. Call after the
	/// tree is in the scene, since <see cref="Node3D.LookAt"/> needs global transforms.
	/// </summary>
	public static void ApplyCameraAiming(Node root)
	{
		if (root is Camera3D cam && cam.HasMeta("lookatpoint"))
		{
			// The look-at point is authored in the camera's own parent space, so it has
			// to ride through the parent's global transform — including the root's
			// handedness flip. Using it as a global position directly aims the camera
			// away from the content and renders an empty frame.
			var local = (Vector3)cam.GetMeta("lookatpoint");
			Vector3 target = cam.GetParent() is Node3D parent
				? parent.GlobalTransform * local
				: local;

			if (!cam.GlobalPosition.IsEqualApprox(target))
				cam.LookAt(target, Vector3.Up);
		}
		foreach (Node child in root.GetChildren())
			ApplyCameraAiming(child);
	}

	/// <summary>
	/// Makes one camera current, so the tree actually renders. Building and aiming a
	/// camera is not enough — Godot renders through whichever camera is <c>Current</c>,
	/// and every camera built from screen data starts off.
	///
	/// Returns the camera that was activated. When the screen ships none, a framing
	/// camera is added under <paramref name="root"/> instead so the content is still
	/// visible.
	/// </summary>
	public static Camera3D ActivateCamera(Node3D root, bool preferFraming = false)
	{
		if (preferFraming)
		{
			return AddFramingCamera(root);
		}

		return AddCanvasCamera(root);
	}

	/// <summary>
	/// The reference height a Gameface screen is RENDERED against — a fixed 720, and
	/// deliberately not the presentation's own authored canvas.
	///
	/// The two are easy to conflate and are different things. The authored canvas is what
	/// Lua's getScreenInfo hands back, and it varies: across the 52 shipped presentations it
	/// is 960x720 on 40, 1280x720 on 8 (frontendmainmenu and frontendstartupscreen among
	/// them) and 800x450 on 4 — the last being HUD widgets rather than full screens. All 52
	/// carry scale-mode 1, "Fixed", so getScreenInfo returns that authored pair verbatim and
	/// the aspect-fit branch at 0x82BB89B0 is dead code for shipped content.
	///
	/// The RENDER scale ignores all of that and divides by a literal 720: sub_822271A0 stores
	/// screenHeight * (1/720) — flt_82000BD4 — into both axis scales immediately before the
	/// draw (0x822272DC / 0x822272E4), and sub_826D6878 computes the same ratio at
	/// 0x826D697C-0x826D699C. So a screen authored at 800x450 is not scaled up to fill the
	/// height; it simply occupies 450 of the same 720 units, which is what makes it a HUD
	/// overlay rather than a page.
	///
	/// That is why this is one constant and not a per-screen value.
	/// </summary>
	public const float CanvasHeight = 720f;

	/// <summary>
	/// The projection the GUEST uses, which is not the one the screens are authored with.
	///
	/// A .bgf carries a camera element with fov, orthographic, clipnear and clipfar, and
	/// using it is the obvious thing to do. The guest does not. sub_82226DE0 reads the
	/// layer's camera element and fov and hands them to the renderer's SetCamera at
	/// vtable+0x20 — and for the Gameface renderer (vtable 0x82006CC0) that slot is
	/// 0x82B96E78, which is <c>nullsub_1</c>: a single <c>blr</c>. The authored camera is
	/// read, passed and dropped. clipnear and clipfar (0x834BE554 / 0x834BE558) have no
	/// readers anywhere in the image at all.
	///
	/// What happens instead is a uniform scale by HEIGHT. sub_822271A0 recomputes both axis
	/// scales from the screen height alone immediately before the draw — 0x8222726C loads
	/// the height, 0x82227268 loads 1/720, and 0x822272DC / 0x822272E4 store the same
	/// product into +0xB0 and +0xAC. The constructor's aspect-corrected X scale is
	/// overwritten there, so there is no letterbox, no pillarbox and no stretch: 720 units
	/// fill the viewport height and a wider window simply sees more.
	///
	/// An orthographic camera of size 720 with Godot's default KeepAspect.Height is exactly
	/// that, so this is a replacement of the stated contract rather than an approximation of
	/// it. It also drops the perspective foreshortening the previous fov-60 canvas camera
	/// introduced, which the guest never had — elements at different position.z were being
	/// scaled by their depth.
	/// </summary>
	private static Camera3D AddCanvasCamera(Node3D root)
	{
		var camera = new Camera3D
		{
			Name = "CanvasCamera",
			Projection = Camera3D.ProjectionType.Orthogonal,
			Size = CanvasHeight,
			KeepAspect = Camera3D.KeepAspectEnum.Height,
			Near = 0.05f,
			Far = 100_000f,
			Current = true,

			// Orthographic, so this sets clipping only and not scale. Far enough back that
			// no authored position.z falls behind the near plane.
			Position = new Vector3(0f, 0f, 10_000f),
		};

		root.AddChild(camera);
		return camera;
	}

	/// <summary>
	/// Adds an orthographic camera that frames every quad in the tree. Orthographic
	/// because a UI screen is authored flat; a perspective fallback would splay the
	/// panel and make it hard to compare against the original.
	/// </summary>
	private static Camera3D AddFramingCamera(Node3D root)
	{
		Aabb bounds = ComputeBounds(root);
		var camera = new Camera3D
		{
			Name = "FramingCamera",
			Projection = Camera3D.ProjectionType.Orthogonal,
			Near = 0.05f,
			Far = 100_000f,
			Current = true,
		};
		root.AddChild(camera);

		if (bounds.Size == Vector3.Zero)
		{
			camera.Size = 1000f;
			camera.GlobalPosition = new Vector3(0f, 0f, 1000f);
		}
		else
		{
			Vector3 centre = bounds.GetCenter();
			// After the handedness conversion the viewer is on the +Z side, which is
			// where the authored cameras land too.
			camera.Size = Mathf.Max(bounds.Size.X, bounds.Size.Y) * 1.05f;
			camera.GlobalPosition = new Vector3(centre.X, centre.Y, centre.Z + Mathf.Max(bounds.Size.Z, 1f) + 1000f);
		}

		camera.LookAt(bounds.Size == Vector3.Zero ? Vector3.Zero : bounds.GetCenter(), Vector3.Up);
		return camera;
	}

	/// <summary>Union of every visual's world-space AABB, or a zero box when the tree has none.</summary>
	private static Aabb ComputeBounds(Node root)
	{
		Aabb acc = default;
		bool seeded = false;
		Accumulate(root, ref acc, ref seeded);
		return acc;

		static void Accumulate(Node node, ref Aabb acc, ref bool seeded)
		{
			if (node is VisualInstance3D visual)
			{
				Aabb world = visual.GlobalTransform * visual.GetAabb();
				acc = seeded ? acc.Merge(world) : world;
				seeded = true;
			}
			foreach (Node child in node.GetChildren())
				Accumulate(child, ref acc, ref seeded);
		}
	}

	private static Color ReadColour(UiNode n, string prefix, Color fallback)
	{
		bool any = false;
		float Read(string suffix, float d)
		{
			if (n.Element.TryGetAttribute(prefix + suffix, out var a) && a.Type == BgfValueType.Float)
			{
				any = true;
				// Authored colour channels are 0..255.
				return a.AsFloat() / 255f;
			}
			return d;
		}
		float r = Read(".r", fallback.R);
		float g = Read(".g", fallback.G);
		float b = Read(".b", fallback.B);
		return any ? new Color(r, g, b) : fallback;
	}

	/// <summary>
	/// Godot node names cannot contain <c>. : @ / " %</c>, and the authored names do —
	/// e.g. <c>Material3228</c> is fine but slide names are not.
	/// </summary>
	private static string SanitizeName(string name)
	{
		if (name.Length == 0)
			return "Node";

		Span<char> buf = stackalloc char[name.Length];
		for (int i = 0; i < name.Length; i++)
		{
			char c = name[i];
			buf[i] = c is '.' or ':' or '@' or '/' or '"' or '%' ? '_' : c;
		}
		return new string(buf);
	}

	/// <summary>
	/// Packs a built tree into a scene file. Godot does the serialising, so the
	/// <c>.tscn</c> format never has to be written by hand.
	/// </summary>
	public static Error SaveAsScene(Node3D root, string path)
	{
		// PackedScene only keeps descendants whose owner is the root.
		SetOwnerRecursive(root, root);
		var packed = new PackedScene();
		Error err = packed.Pack(root);
		return err != Error.Ok ? err : ResourceSaver.Save(packed, path);
	}

	private static void SetOwnerRecursive(Node node, Node owner)
	{
		foreach (Node child in node.GetChildren())
		{
			child.Owner = owner;
			SetOwnerRecursive(child, owner);
		}
	}
}

using System.Collections.Generic;
using Godot;


namespace Refii.Ui;

/// <summary>
/// The sixteen pooled world icons — quest markers, trader titles, region labels — drawn by
/// Godot from the guest's own per-frame statements.
///
/// The one part of the HUD the guest still arranges: an icon's place is the projection of a
/// world position, computed by `sub_824251E0` and written onto the element every frame. The
/// guest states position, content and which slots are lit; Godot owns only the drawing.
///
/// Not a subtree clone — an icon is one textured quad and one title, so this draws exactly that
/// and applies the stated position with the file's own pivot term, instead of running the
/// general live-transform pipeline.
///
/// Joined by ordinal: when the guest's element count disagrees with the file's, the sample is
/// declined rather than misread.
/// </summary>
public sealed partial class Engine_HudWorldIcons : Node3D
{
	/// <summary>The shipped look — glyphs for the titles.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>Stated texture hash to a loaded texture; null when the hash resolves to nothing.</summary>
	public System.Func<uint, Texture2D?>? ResolveTexture { get; set; }

	/// <summary>One pooled slot, resolved from the document once.</summary>
	private sealed class Slot
	{
		public required string Name { get; init; }
		public required int Ordinal { get; init; }

		/// <summary>The first quad-bearing descendant — the icon image itself.</summary>
		public int QuadOrdinal { get; init; } = -1;

		public UiQuad? Quad { get; init; }

		/// <summary>The quad's authored offset and scale within the component.</summary>
		public Vector2 QuadOffset { get; init; }

		public Vector2 QuadScale { get; init; } = Vector2.One;

		/// <summary>The `Contents.Title` text, when the slot has one.</summary>
		public int TitleOrdinal { get; init; } = -1;

		public UiText? Title { get; init; }

		public Vector2 TitleOffset { get; init; }

		/// <summary>
		/// The `Contents.Opinion` text — the amount line. Request types 36/37 write the value
		/// here and the name on `Title`; authored 8 units below the title.
		/// </summary>
		public int OpinionOrdinal { get; init; } = -1;

		/// <summary>The `Contents` child — the element the guest actually shows and hides.</summary>
		public int LitOrdinal { get; init; }

		public UiText? Opinion { get; init; }

		public Vector2 OpinionOffset { get; init; }

		/// <summary>The component's own pivot and scale — the file's terms in the origin.</summary>
		public Vector2 Pivot { get; init; }

		public Vector2 Scale { get; init; } = Vector2.One;

		// Live state.
		public Node3D? Node { get; set; }
		public MeshInstance3D? Icon { get; set; }
		public MeshInstance3D? Label { get; set; }
		public string Shown { get; set; } = string.Empty;
		public MeshInstance3D? OpinionLabel { get; set; }
		public string ShownOpinion { get; set; } = string.Empty;
		public uint ShownTexture { get; set; }
		public bool Lit { get; set; }
	}

	private readonly List<Slot> _slots = new();
	private int _documentElements;
	private string _reported = string.Empty;

	/// <summary>The file's element count — the length every live array must match.</summary>
	public int DocumentElements => _documentElements;

	/// <summary>How many slots the guest currently lights.</summary>
	public int Lit { get; private set; }

	/// <summary>
	/// Resolves the pooled slots from the parsed document. The file may carry the pool twice —
	/// the 4:3 and 16:9 layouts — and every copy is kept: the guest's own active bit says which
	/// copy draws, so choosing here would be a host-side guess.
	/// </summary>
	public void Bind(Engine_UiScreen screen)
	{
		_slots.Clear();
		_documentElements = screen.Nodes.Length;

		foreach (UiNode node in screen.Nodes)
		{
			if (node.Type != Refii.Ui.BgfElementType.Component || !node.HasAuthoredName ||
				!(IsWorldIconName(node.Name) || IsSingleIconName(node.Name)))
			{
				continue;
			}

			// The guest lights `Contents`, never the root: `sub_82825618` (0x828256d4) and
			// `sub_82825328` call `sub_82284210(Contents, pres, visible)` on the child, and the
			// component root stays authored-active for the life of the pool.
			int contents = node.Ordinal;
			foreach (UiNode child in node.Children)
			{
				if (child.Name == "Contents")
				{
					contents = child.Ordinal;
					break;
				}
			}

			(UiNode? quadNode, Vector2 quadOffset) = FirstQuad(node, Vector2.Zero);
			(UiNode? titleNode, Vector2 titleOffset) = FirstText(node, Vector2.Zero);
			(UiNode? opinionNode, Vector2 opinionOffset) = NamedText(node, Vector2.Zero, "Opinion");

			_slots.Add(new Slot
			{
				Name = node.Name,
				Ordinal = node.Ordinal,
				LitOrdinal = contents,
				QuadOrdinal = quadNode?.Ordinal ?? -1,
				Quad = quadNode?.Quad,
				QuadOffset = quadOffset,
				QuadScale = quadNode is null
					? Vector2.One
					: new Vector2(quadNode.ScaleX, quadNode.ScaleY),
				TitleOrdinal = titleNode?.Ordinal ?? -1,
				Title = titleNode?.Text,
				TitleOffset = titleOffset,
				OpinionOrdinal = opinionNode?.Ordinal ?? -1,
				Opinion = opinionNode?.Text,
				OpinionOffset = opinionOffset,
				Pivot = new Vector2(node.PivotX, node.PivotY),
				Scale = new Vector2(node.ScaleX, node.ScaleY),
			});
		}

		GD.Print($"[world-icons] {_slots.Count} pooled slot(s) resolved from the file" +
			(_slots.Count == 0 ? " — NOTHING TO DRAW" : string.Empty));
	}

	/// <summary>
	/// The three icons the guest draws OUTSIDE the pool of sixteen: registries 8, 9 and 10 —
	/// the hold-to-fill icon, and the hero's and henchman's off-screen markers. Same request
	/// path, same per-frame position write, one element each instead of a pool.
	/// </summary>
	private static bool IsSingleIconName(string name) =>
		name is "FillWorldIcon" or "HeroOffScreenWorldIcon" or "HenchmanOffScreenWorldIcon";

	/// <summary>`WorldIcon` followed by digits and nothing else — not `FillWorldIconButton`.</summary>
	private static bool IsWorldIconName(string name)
	{
		const string prefix = "WorldIcon";
		if (!name.StartsWith(prefix, System.StringComparison.Ordinal) ||
			name.Length == prefix.Length)
		{
			return false;
		}

		for (int i = prefix.Length; i < name.Length; ++i)
		{
			if (!char.IsAsciiDigit(name[i]))
			{
				return false;
			}
		}

		return true;
	}

	private static (UiNode?, Vector2) FirstQuad(UiNode node, Vector2 at)
	{
		foreach (UiNode child in node.Children)
		{
			Vector2 offset = at + new Vector2(child.PositionX, child.PositionY);
			if (child.Quad is not null)
			{
				return (child, offset);
			}

			(UiNode? inner, Vector2 innerAt) = FirstQuad(child, offset);
			if (inner is not null)
			{
				return (inner, innerAt);
			}
		}

		return (null, at);
	}

	/// <summary>
	/// The slot's `Title` text and its accumulated offset. By NAME: every world icon authors two
	/// `Text` elements with `Opinion` first in document order, so the first one is the wrong one.
	///
	/// <code>
	/// Contents [Node]
	///   Icon [Node] …
	///   Opinion [Text] ord=183 pos=(0,0,0)
	///   Title   [Text] ord=184 pos=(0,8,0)
	/// </code>
	///
	/// Falls back to the first `Text` when a slot has no `Title`.
	/// </summary>
	private static (UiNode?, Vector2) FirstText(UiNode node, Vector2 at) =>
		NamedText(node, at, "Title") is (not null, _) and var named ? named : AnyText(node, at);

	private static (UiNode?, Vector2) NamedText(UiNode node, Vector2 at, string name)
	{
		foreach (UiNode child in node.Children)
		{
			Vector2 offset = at + new Vector2(child.PositionX, child.PositionY);
			if (child.Type == Refii.Ui.BgfElementType.Text &&
				string.Equals(child.Name, name, System.StringComparison.Ordinal))
			{
				return (child, offset);
			}

			(UiNode? inner, Vector2 innerAt) = NamedText(child, offset, name);
			if (inner is not null)
			{
				return (inner, innerAt);
			}
		}

		return (null, at);
	}

	private static (UiNode?, Vector2) AnyText(UiNode node, Vector2 at)
	{
		foreach (UiNode child in node.Children)
		{
			Vector2 offset = at + new Vector2(child.PositionX, child.PositionY);
			if (child.Type == Refii.Ui.BgfElementType.Text)
			{
				return (child, offset);
			}

			(UiNode? inner, Vector2 innerAt) = AnyText(child, offset);
			if (inner is not null)
			{
				return (inner, innerAt);
			}
		}

		return (null, at);
	}

	/// <summary>
	/// Takes the slow-changing statements: which slots are lit, their titles, their art.
	/// Declined on a count mismatch — the live arrays join to the file by ordinal.
	/// </summary>
	public void Sample(IReadOnlyList<Engine_UiModel.UiElement> elements, uint[]? textures)
	{
		if (elements.Count != _documentElements)
		{
			return;
		}

		Lit = 0;
		foreach (Slot slot in _slots)
		{
			slot.Lit = slot.LitOrdinal < elements.Count && elements[slot.LitOrdinal].IsGloballyActive;
			if (!slot.Lit)
			{
				if (slot.Node is not null)
				{
					slot.Node.Visible = false;
				}
				continue;
			}

			++Lit;
			slot.Node ??= Build(slot);
			slot.Node.Visible = true;

			// The guest's stated art, per sample: quest icon, trader icon and region marker all
			// ride the same pooled quad and only the hash says which. The file's own hash is
			// the fallback, so an unstated slot keeps the authored image.
			uint stated = textures is not null && slot.QuadOrdinal >= 0 &&
				slot.QuadOrdinal < textures.Length && textures[slot.QuadOrdinal] != 0
					? textures[slot.QuadOrdinal]
					: slot.Quad?.TextureHash ?? 0;
			if (stated != slot.ShownTexture && slot.Icon is not null &&
				ResolveTexture?.Invoke(stated) is { } texture &&
				slot.Icon.MaterialOverride is StandardMaterial3D material)
			{
				// Advanced only on success, so an unresolvable hash is asked for again.
				slot.ShownTexture = stated;
				material.AlbedoTexture = texture;
			}

			// The title, through the same glyph path as every owned surface. Live text only:
			// the authored string is the artist's "Text" placeholder, never a fallback.
			string title = slot.TitleOrdinal >= 0 && slot.TitleOrdinal < elements.Count
				? elements[slot.TitleOrdinal].Text ?? string.Empty
				: string.Empty;
			// The `.fac` record's colour — the prompt title is authored (255,255,48).
			var colour = slot.Title is { } record
				? new Color(record.R, record.G, record.B, 1f)
				: new Color(1f, 224f / 255f, 128f / 255f, 1f);
			if (title != slot.Shown && slot.Label is not null && Theme is not null)
			{
				slot.Shown = title;
				Theme.Paint(
					slot.Label, title,
					slot.Title?.Size is > 0 and var size ? size : 18, colour,
					horizontalAlign: 1, verticalAlign: 1,
					boxWidth: 0f, boxHeight: 0f, wrapMode: 0);
			}

			// The amount line, live only, exactly like the title.
			string opinion = slot.OpinionOrdinal >= 0 && slot.OpinionOrdinal < elements.Count
				? elements[slot.OpinionOrdinal].Text ?? string.Empty
				: string.Empty;
			if (opinion != slot.ShownOpinion && slot.OpinionLabel is not null && Theme is not null)
			{
				slot.ShownOpinion = opinion;
				Theme.Paint(
					slot.OpinionLabel, opinion,
					slot.Opinion?.Size is > 0 and var opinionSize ? opinionSize : 18, colour,
					horizontalAlign: 1, verticalAlign: 1,
					boxWidth: 0f, boxHeight: 0f, wrapMode: 0);
			}
		}

		Report();
	}

	/// <summary>
	/// Follows the guest's per-frame positions. A slot with no stated row keeps the file's
	/// authored spot (offscreen storage for a pooled element); the active bit gates drawing.
	/// </summary>
	public void Track(float?[]?[]? transform)
	{
		if (transform is null || transform.Length != _documentElements)
		{
			return;
		}

		foreach (Slot slot in _slots)
		{
			if (!slot.Lit || slot.Node is null ||
				slot.Ordinal >= transform.Length || transform[slot.Ordinal] is not { } row)
			{
				continue;
			}

			float x = row.Length > 0 && row[0] is { } sx ? sx : 0f;
			float y = row.Length > 1 && row[1] is { } sy ? sy : 0f;

			// `origin = position - basis * pivot`, with the component's authored scale standing
			// in for the basis: these elements are never rotated.
			Vector2 origin = new Vector2(x, y) - (slot.Scale * slot.Pivot);
			if (float.IsFinite(origin.X) && float.IsFinite(origin.Y))
			{
				slot.Node.Position = new Vector3(origin.X, origin.Y, 0f);
			}
		}
	}

	/// <summary>
	/// The pool index a slot answers to: `WorldIcon{n}` is n-1, the three singles are 16/17/18
	/// — the order of the guest's slot records at 0x8349BC80 (`sub_82825618`).
	/// </summary>
	private static int PoolIndexOf(string name) => name switch
	{
		"FillWorldIcon" => 16,
		"HeroOffScreenWorldIcon" => 17,
		"HenchmanOffScreenWorldIcon" => 18,
		_ => name.StartsWith("WorldIcon", System.StringComparison.Ordinal) &&
			int.TryParse(name.Substring("WorldIcon".Length), out int n)
				? n - 1
				: -1,
	};

	/// <summary>
	/// Places the lit slots at canvas points projected by GODOT from the guest's world anchors —
	/// the pooled object's `+112` anchor plus its `+144` lift, the same inputs the guest projects
	/// through its own cameras. The guest's stated position would follow the guest's camera, not
	/// the one Godot draws the world with.
	/// </summary>
	public void TrackProjected(IReadOnlyDictionary<int, Vector2> canvasBySlot)
	{
		foreach (Slot slot in _slots)
		{
			if (!slot.Lit || slot.Node is null)
			{
				continue;
			}

			int index = PoolIndexOf(slot.Name);
			if (index < 0 || !canvasBySlot.TryGetValue(index, out Vector2 at))
			{
				continue;
			}

			Vector2 origin = at - (slot.Scale * slot.Pivot);
			if (float.IsFinite(origin.X) && float.IsFinite(origin.Y))
			{
				slot.Node.Position = new Vector3(origin.X, origin.Y, 0f);
			}
		}
	}

	/// <summary>One slot's scene: a textured quad and a title label at their authored offsets.</summary>
	private Node3D Build(Slot slot)
	{
		var node = new Node3D { Name = slot.Name };
		AddChild(node);

		if (slot.Quad is { } quad)
		{
			var icon = new MeshInstance3D
			{
				Name = "Icon",
				Position = new Vector3(slot.QuadOffset.X, slot.QuadOffset.Y, 0f),
				Mesh = new QuadMesh
				{
					// QuadBaseSize: a unit quad is 100 canvas units before scale.
					Size = new Vector2(100f * slot.QuadScale.X, 100f * slot.QuadScale.Y),
				},
				MaterialOverride = new StandardMaterial3D
				{
					Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					CullMode = BaseMaterial3D.CullModeEnum.Disabled,
					AlbedoTexture = ResolveTexture?.Invoke(quad.TextureHash),
				},
			};

			// The authored atlas rectangle, or the whole texture when degenerate.
			if (!quad.IsDegenerate && icon.Mesh is QuadMesh mesh)
			{
				// QuadMesh has no UV rect; the atlas cell rides the material's UV1 transform.
				if (icon.MaterialOverride is StandardMaterial3D material)
				{
					material.Uv1Scale = new Vector3(
						quad.UMax - quad.UMin, quad.VMax - quad.VMin, 1f);
					material.Uv1Offset = new Vector3(quad.UMin, quad.VMin, 0f);
				}
			}

			node.AddChild(icon);
			slot.Icon = icon;
			slot.ShownTexture = quad.TextureHash;
		}

		var label = new MeshInstance3D
		{
			Name = "Title",
			// A step in front of the icon: equal-depth alpha has no defined winner.
			Position = new Vector3(slot.TitleOffset.X, slot.TitleOffset.Y, 1f),
		};
		node.AddChild(label);
		slot.Label = label;

		if (slot.OpinionOrdinal >= 0)
		{
			var opinion = new MeshInstance3D
			{
				Name = "Opinion",
				Position = new Vector3(slot.OpinionOffset.X, slot.OpinionOffset.Y, 1f),
			};
			node.AddChild(opinion);
			slot.OpinionLabel = opinion;
		}

		return node;
	}

	/// <summary>Names what is lit, when the set changes.</summary>
	private void Report()
	{
		var lit = new List<string>();
		foreach (Slot slot in _slots)
		{
			if (slot.Lit)
			{
				lit.Add($"{slot.Name}" +
					(slot.Shown.Length == 0 ? string.Empty : $"='{slot.Shown}'"));
			}
		}

		string described = lit.Count == 0 ? "none lit" : string.Join("  ", lit);
		if (described == _reported)
		{
			return;
		}

		_reported = described;
		GD.Print($"[world-icons] {Lit}/{_slots.Count} lit: {described}");
	}
}

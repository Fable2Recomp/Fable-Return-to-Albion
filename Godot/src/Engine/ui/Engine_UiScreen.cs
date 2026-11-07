using System;
using System.Collections.Generic;
using System.IO;

using Refii.Core;

using Refii.Text;

using Refii.Textures;

namespace Refii.Ui;

/// <summary>A resolved textured quad hanging off a <see cref="UiNode"/>.</summary>
public sealed class UiQuad
{
	/// <summary>Resolved asset path, or empty if the texture is not present in the table.</summary>
	public string TexturePath = string.Empty;

	public uint TextureHash;

	/// <summary>Atlas rectangle. V is already flipped relative to the authored origin.</summary>
	public float UMin, UMax, VMin, VMax;

	/// <summary>Vertex tint, 0..1.</summary>
	public float R = 1, G = 1, B = 1;

	/// <summary>
	/// The guest's <c>TintingAmount</c>. Kept SEPARATE from the colour, which is only ever
	/// applied subtractively at this weight: <c>rgb -= (1 - tint) * amount</c>. Seeded from the
	/// <c>.fac</c>'s fourth colour lane (0 on ~85% of records) and raised at runtime through the
	/// attribute — <c>sub_8286ED40</c> routes it to <c>sub_8286EFB8</c>, lane .w.
	/// </summary>
	public float TintAmount;

	/// <summary>
	/// True when the atlas rectangle has no area — 442 of the 15,790 shipped quads. These are
	/// nine-slice edge cells: the zero axis is the one the edge stretches along
	/// (<c>UpperMiddle</c>/<c>LowerMiddle</c> du=0, <c>Left</c>/<c>Right</c> dv=0,
	/// <c>Middle</c> both; corners never), driven by <c>scale.x</c>/<c>scale.y</c>, never
	/// <c>scaleu</c>/<c>scalev</c>. Keep them and expect them to render; the builder's half-texel
	/// inset skips the zero axis.
	/// </summary>
	public bool IsDegenerate => UMax - UMin <= 0f || VMax - VMin <= 0f;

	public FacUvRecord Source = null!;
}

/// <summary>A resolved text placement hanging off a <see cref="UiNode"/>.</summary>
public sealed class UiText
{
	/// <summary>The authored tag or literal, before localisation.</summary>
	public string Tag = string.Empty;

	/// <summary>True when the string came from the running guest rather than the file.</summary>
	public bool Live;

	/// <summary>The string to display, after Babel lookup. Empty for the <c>"-"</c> sentinel.</summary>
	public string Display = string.Empty;

	/// <summary>True when <see cref="Display"/> came from the string database rather than the literal.</summary>
	public bool Localised;

	public int Size;
	public int HorzAlign;
	public int VertAlign;

	/// <summary>1 = wrapped box mode (per-line align); 0 = a single line the engine truncates.</summary>
	public int WrapMode;

	/// <summary>Box extents in virtual UI pixels — the authored 0..1 already scaled.</summary>
	public float BoxWidth, BoxHeight;

	public float R = 1, G = 1, B = 1;

	public FacTextRecord Source = null!;
}

/// <summary>
/// One element of a screen, with its children and whatever render data the
/// <c>.fac</c> sidecar attached to it.
/// </summary>
public sealed class UiNode
{
	public int Ordinal;

	/// <summary>
	/// The authored name, or a synthesised <c>Type_Ordinal</c> when the presentation
	/// ships without one. Check <see cref="HasAuthoredName"/> to tell them apart.
	/// </summary>
	public string Name = string.Empty;

	/// <summary>
	/// False when the element carried no <c>name</c> attribute — 15 of the 52 shipped screens
	/// export this way, so names cannot be relied on. The element's hash field is still
	/// <c>sdbm32</c> of the original name and stays a stable identity.
	/// </summary>
	public bool HasAuthoredName;

	public BgfElementType Type;
	public uint ObjectId;

	public UiNode? Parent;
	public readonly List<UiNode> Children = new();

	public float PositionX, PositionY, PositionZ;
	public float RotationX, RotationY, RotationZ;
	public float ScaleX = 1, ScaleY = 1, ScaleZ = 1;
	public float PivotX, PivotY, PivotZ;

	/// <summary>Authored 0..100; the engine multiplies by 0.01 on its way to the node.</summary>
	public float Opacity = 100f;

	/// <summary>
	/// False when the master + named slide baseline switches this element off. The
	/// engine skips an inactive node AND its entire subtree, so a consumer must do the
	/// same rather than merely hiding the one node.
	/// </summary>
	public bool Visible = true;

	public UiQuad? Quad;
	public UiText? Text;

	public BgfElement Element = null!;

	/// <summary>
	/// True when <see cref="Engine_UiMenuCloning"/> produced this node rather than the file. A
	/// clone has <see cref="Ordinal"/> -1 and so takes no live state (every live array is indexed
	/// by document ordinal); it carries the source's <see cref="Element"/>, so an attribute read
	/// answers with the template's authored value.
	/// </summary>
	public bool IsClone;

	/// <summary>The document ordinal of the template subtree this was copied from, or -1.</summary>
	public int CloneSourceOrdinal = -1;

	public bool IsRoot => Parent is null;
}

/// <summary>
/// A screen assembled from its <c>.bgf</c> presentation and <c>.fac</c> sidecar, with
/// textures and localised strings resolved. The converter's input model — no engine types.
///
/// Joined by <see cref="BgfElement.ObjectId"/>. <c>LIGHT</c> records address the <c>.bsg</c>
/// scene graph and never match an element, and a few shipped <c>UV</c> records point at deleted
/// ones; both are counted in <see cref="UnmatchedFacRecords"/> rather than throwing.
/// </summary>
public sealed class Engine_UiScreen
{
	public string Name = string.Empty;

	/// <summary>Authoring canvas — 960x720 for most screens.</summary>
	public int CanvasWidth;
	public int CanvasHeight;

	/// <summary>True when the guest's live element state was applied to this screen.</summary>
	public bool IsLive;

	public UiNode[] Nodes = Array.Empty<UiNode>();

	/// <summary>
	/// The element carrying a given <see cref="UiNode.ObjectId"/>, or null. The id is the stable
	/// join between a built node and its element: the builder stamps it as metadata and Godot's
	/// `Duplicate()` carries metadata, so a clone still says which element drives it. Lazy.
	/// </summary>
	public UiNode? NodeForObjectId(uint objectId)
	{
		if (_byObjectId is null)
		{
			_byObjectId = new Dictionary<uint, UiNode>(Nodes.Length);
			foreach (UiNode node in Nodes)
			{
				// First wins: ids are unique in every shipped file.
				_byObjectId.TryAdd(node.ObjectId, node);
			}
		}

		return _byObjectId.TryGetValue(objectId, out UiNode? found) ? found : null;
	}

	private Dictionary<uint, UiNode>? _byObjectId;
	public UiNode[] Roots = Array.Empty<UiNode>();

	/// <summary>Exported entry points the game calls into, e.g. <c>OnPressA</c>.</summary>
	public readonly List<string> ContractNames = new();

	/// <summary>UV and TEXT records whose element could not be found. Expected to be small.</summary>
	public int UnmatchedFacRecords;

	public Engine_BgfDocument Document = null!;

	/// <summary>
	/// Loads a screen. <paramref name="bgfPath"/>'s sibling <c>.fac</c> is picked up
	/// automatically when present.
	/// </summary>
	public static Engine_UiScreen Load(string bgfPath, Engine_TextureNameTable? textures = null,
		Engine_BabelDatabase? strings = null, int sceneSlide = 0,
		IReadOnlyList<int>? liveSlides = null)
	{
		string facPath = Path.ChangeExtension(bgfPath, ".fac");
		string bsgPath = Path.ChangeExtension(bgfPath, ".bsg");
		return Parse(Path.GetFileNameWithoutExtension(bgfPath),
			File.ReadAllBytes(bgfPath),
			File.Exists(facPath) ? File.ReadAllBytes(facPath) : null,
			File.Exists(bsgPath) ? File.ReadAllBytes(bsgPath) : null,
			textures, strings, sceneSlide, liveSlides);
	}

	/// <summary>
	/// Assembles a screen from bytes. This is the form a bank uses, where each sidecar
	/// is a separate entry rather than a sibling file, so the caller supplies them.
	///
	/// <paramref name="bsgBytes"/> supplies the base transform the <c>.bgf</c> overrides.
	/// Omitting it is supported but leaves the ~1.3% of elements that do not author a
	/// given component sitting at 0/1 rather than their real base.
	/// </summary>
	public static Engine_UiScreen Parse(string name, byte[] bgfBytes, byte[]? facBytes,
		byte[]? bsgBytes = null, Engine_TextureNameTable? textures = null, Engine_BabelDatabase? strings = null,
		int sceneSlide = 0, IReadOnlyList<int>? liveSlides = null,
		IReadOnlyList<string?>? liveText = null,
		IReadOnlyList<float?>? liveOpacity = null,
		IReadOnlyList<bool>? liveVisible = null,
		IReadOnlyList<float?[]?>? liveTransform = null,
		IReadOnlyList<uint>? liveTextures = null,
		IReadOnlyList<LiveTint?>? liveTint = null)
	{
		var doc = Engine_BgfDocument.Parse(bgfBytes);

		// Before anything reads MasterActive or an override: visibility and every attribute
		// lookup resolve against the slide state. The guest's per-element slides win when
		// present; the single scene slide is the no-guest fallback.
		bool live = liveSlides is { Count: > 0 } && doc.SetElementSlides(liveSlides);
		if (!live && sceneSlide > 0)
		{
			doc.SetSceneSlide(sceneSlide);
		}

		BsgSceneNode[]? sceneNodes = null;
		if (bsgBytes is not null)
		{
			// A malformed sidecar must not take the screen down; the .bgf alone still renders,
			// without the base layer.
			try
			{
				sceneNodes = Engine_BsgDocument.Parse(bsgBytes).Nodes;
			}
			catch (InvalidDataException)
			{
				sceneNodes = null;
			}
		}
		int spatialIndex = 0;
		var screen = new Engine_UiScreen
		{
			Name = name,
			CanvasWidth = doc.CanvasWidth,
			CanvasHeight = doc.CanvasHeight,
			Document = doc,
		};

		// 1. Elements to nodes, pulling the transform attributes across.
		var nodes = new UiNode[doc.Elements.Length];
		for (int i = 0; i < doc.Elements.Length; i++)
		{
			BgfElement e = doc.Elements[i];
			string authored = doc.GetName(e);
			var n = new UiNode
			{
				Ordinal = i,
				HasAuthoredName = authored.Length > 0,
				Name = authored.Length > 0 ? authored : $"{e.Type}_{i}",
				Type = e.Type,
				ObjectId = e.ObjectId,
				Element = e,
			};

			// The .bgf is an OVERRIDE layer: sub_82188D10 writes only the attributes an element
			// carries; the rest keep the .bsg base. ~1.3% of spatial elements omit one.
			BsgSceneNode? b = null;
			if (IsSpatial(e.Type) && sceneNodes is not null && spatialIndex < sceneNodes.Length)
				b = sceneNodes[spatialIndex];
			if (IsSpatial(e.Type))
				spatialIndex++;

			// Three layers, lowest first: the .bsg base, the element's own attributes, then the
			// applied slide's commands. The slide is the engine's LAST write and wins.
			float Attr(string name, float fallback) => Float(doc, i, e, name, fallback);

			n.PositionX = Attr("position.x", b?.PositionX ?? 0f);
			n.PositionY = Attr("position.y", b?.PositionY ?? 0f);
			n.PositionZ = Attr("position.z", b?.PositionZ ?? 0f);
			n.RotationX = Attr("rotation.x", b?.RotationX ?? 0f);
			n.RotationY = Attr("rotation.y", b?.RotationY ?? 0f);
			n.RotationZ = Attr("rotation.z", b?.RotationZ ?? 0f);
			n.ScaleX = Attr("scale.x", b?.ScaleX ?? 1f);
			n.ScaleY = Attr("scale.y", b?.ScaleY ?? 1f);
			n.ScaleZ = Attr("scale.z", b?.ScaleZ ?? 1f);
			n.PivotX = Attr("pivot.x", b?.PivotX ?? 0f);
			n.PivotY = Attr("pivot.y", b?.PivotY ?? 0f);
			n.PivotZ = Attr("pivot.z", b?.PivotZ ?? 0f);
			// .bsg opacity is 0..1; the .bgf attribute is 0..100.
			n.Opacity = Attr("opacity", (b?.Opacity ?? 1f) * 100f);
			n.Visible = i < doc.MasterActive.Length ? doc.MasterActive[i] : true;

			nodes[i] = n;
		}

		// 2. Hierarchy. Parent is an ordinal into this same array; -1 is a root.
		var roots = new List<UiNode>();
		for (int i = 0; i < nodes.Length; i++)
		{
			int parent = doc.Elements[i].ParentOrdinal;
			if (parent >= 0 && parent < nodes.Length)
			{
				nodes[i].Parent = nodes[parent];
				nodes[parent].Children.Add(nodes[i]);
			}
			else
			{
				roots.Add(nodes[i]);
			}
		}

		screen.Nodes = nodes;
		screen.Roots = roots.ToArray();

		foreach (var c in doc.Contracts)
			foreach (var contractName in c.Names)
				if (contractName.Length > 0)
					screen.ContractNames.Add(contractName);

		// 3. Join the .fac sidecar by object id.
		if (facBytes is not null)
			screen.AttachFac(Engine_FacDocument.Parse(facBytes), textures, strings);

		// Video slots, read off the file before the guest names a movie. A sprite whose `Movie`
		// string is non-empty draws a Bink movie into its rect INSTEAD of a texture; the shipped
		// value is a placeholder the guest restates from the ability's GDB record.
		screen.FindMovieSlots();
		screen.ResolveMasks();

		// Last, over everything the file said: what the element holds right now. The shipped
		// presentation carries placeholders (`DEFAULT_TEXT_ID`) and some tags resolve through a
		// table that is not the Babel book.
		screen.IsLive = live;

		// The screen's Lua hides and moves elements with setAttribute and none of that is in the
		// file — Page.Initialize zeroes DimensionsImage opacity, display boxes hide themselves.
		if (live && liveVisible is { Count: > 0 })
		{
			screen.ApplyLiveVisibility(liveVisible);
		}
		if (live && liveOpacity is { Count: > 0 })
		{
			screen.ApplyLiveOpacity(liveOpacity);
		}
		// After the slide state: a slide's move is an attribute write too, so what the guest
		// currently holds already includes it and is the final word.
		if (live && liveTransform is { Count: > 0 })
		{
			screen.ApplyLiveTransform(liveTransform);
		}
		if (live && liveText is { Count: > 0 })
		{
			screen.ApplyLiveText(liveText);
		}

		// After the quads exist (AttachFac builds them) and after the slide baseline, so a swap
		// lands on the quad the screen will draw.
		if (live && liveTextures is { Count: > 0 })
		{
			screen.LiveTexturesRead = true;
			screen.ApplyLiveTextures(liveTextures, textures);
		}

		// The tint WEIGHT is a live attribute; the file's copy is only where it starts. After the
		// quads exist, because it lands on a quad rather than an element.
		if (live && liveTint is { Count: > 0 })
		{
			screen.ApplyLiveTint(liveTint);
		}

		// Before the frame engine: a wheel item's frame is copied off MenuItemTemplate rather
		// than authored, and it is a nine-slice, so laying out first would size panels against a
		// screen still missing that content.
		screen.MenuCloning = Engine_UiMenuCloning.Apply(screen);

		// Last: the frame engine sizes a panel from what its contents measure and which are
		// switched on, so it needs the slide baseline and the quads, both done above. Every
		// screen goes through this one call site.
		screen.FrameLayout = Engine_UiFrameDriver.Apply(screen);

		return screen;
	}

	/// <summary>
	/// What the frame engine did to this screen. Zero frames is a real answer — a screen may have
	/// none — but zero on the map or the pause menu means the detection missed.
	/// </summary>
	public Engine_UiFrameDriver.Report FrameLayout { get; private set; }

	/// <summary>
	/// What the wheel-menu cloning step did to this screen. Default on the 49 screens with no
	/// <c>MenuItemTemplate</c>; only <c>quickmainmenu</c>, <c>spellsmenu</c> and
	/// <c>regionsscreen</c> ship one.
	/// </summary>
	public Engine_UiMenuCloning.Report MenuCloning { get; private set; }

	/// <summary>
	/// An element that can host a Bink movie, and the path the FILE authored on it. The authored
	/// path is a placeholder the guest overwrites; its presence is what marks the slot, and a
	/// slot still playing it means the guest never restated it.
	/// </summary>
	public readonly record struct MovieSlot(int Ordinal, string Name, string AuthoredPath);

	/// <summary>Elements carrying a <c>Movie</c> attribute. Empty on most screens.</summary>
	public readonly List<MovieSlot> MovieSlots = new();

	/// <summary>
	/// The mask element clipping each element, by ordinal, or -1 for none.
	///
	/// Gameface clipping is a by-NAME binding, and the mask lives nowhere near what it clips —
	/// at <c>Layer</c> level, while the content is deep under <c>Foreground/.../Pages</c>.
	/// </summary>
	public int[] MaskByOrdinal = Array.Empty<int>();

	/// <summary>Elements that ARE masks. They draw nothing; they only publish a rect.</summary>
	public readonly List<int> MaskElements = new();

	/// <summary>
	/// Resolves every element's clip mask, following the guest's lookup. An empty `MaskName`
	/// means INHERIT, not unbound: `sub_82427188` recurses to the parent until it finds a
	/// non-empty one or reaches the root. The value is a `.`-separated path resolved against the
	/// element named <c>Layer</c> (`sub_824265A0`); every shipped value is a single name, so this
	/// takes the leaf.
	/// </summary>
	private void ResolveMasks()
	{
		MaskByOrdinal = new int[Nodes.Length];
		Array.Fill(MaskByOrdinal, -1);

		uint maskId = Engine_GameHash.AttributeId("Mask");
		uint maskNameId = Engine_GameHash.AttributeId("MaskName");

		var byName = new Dictionary<string, int>(StringComparer.Ordinal);
		for (int i = 0; i < Nodes.Length; ++i)
		{
			if (Nodes[i].Element.TryGetAttribute(maskId, out BgfAttribute flag) &&
				flag.Type == BgfValueType.Bool && flag.AsBool())
			{
				MaskElements.Add(i);
			}

			// Last writer wins: a mask is named uniquely within its own presentation.
			byName[Nodes[i].Name] = i;
		}

		if (MaskElements.Count == 0)
		{
			return;
		}

		for (int i = 0; i < Nodes.Length; ++i)
		{
			for (UiNode? walk = Nodes[i]; walk is not null; walk = walk.Parent)
			{
				if (!walk.Element.TryGetAttribute(maskNameId, out BgfAttribute named) ||
					named.Type != BgfValueType.String)
				{
					continue;
				}

				string path = Document.GetString(named.AsStringIndex());
				if (path.Length == 0)
				{
					continue;  // empty: keep walking up, exactly as sub_82427188 does
				}

				string leaf = path[(path.LastIndexOf('.') + 1)..];
				if (byName.TryGetValue(leaf, out int mask))
				{
					MaskByOrdinal[i] = mask;
				}

				break;
			}
		}
	}

	/// <summary>
	/// Finds every element the file marks as a movie host. Both the Node and its Material child
	/// carry the attribute and only the NODE has a quad, so a consumer wanting pixels should
	/// prefer the one whose <see cref="UiNode.Quad"/> is non-null. Both are listed.
	/// </summary>
	private void FindMovieSlots()
	{
		uint movieId = Engine_GameHash.AttributeId("Movie");

		for (int i = 0; i < Nodes.Length; ++i)
		{
			if (!Nodes[i].Element.TryGetAttribute(movieId, out BgfAttribute attribute) ||
				attribute.Type != BgfValueType.String)
			{
				continue;
			}

			MovieSlots.Add(new MovieSlot(
				i, Nodes[i].Name, Document.GetString(attribute.AsStringIndex())));
		}
	}

	private void AttachFac(Engine_FacDocument fac, Engine_TextureNameTable? textures, Engine_BabelDatabase? strings)
	{
		var byObjectId = new Dictionary<uint, UiNode>(Nodes.Length);
		foreach (var n in Nodes)
			byObjectId[n.ObjectId] = n;

		foreach (var uv in fac.Uv)
		{
			if (!byObjectId.TryGetValue((uint)uv.StableId, out var node))
			{
				UnmatchedFacRecords++;
				continue;
			}
			node.Quad = new UiQuad
			{
				TextureHash = uv.TextureHash,
				TexturePath = textures?.Resolve(uv) ?? string.Empty,
				UMin = uv.UMin, UMax = uv.UMax, VMin = uv.VMin, VMax = uv.VMax,
				R = uv.R, G = uv.G, B = uv.B,
				TintAmount = uv.VertexLane3,
				Source = uv,
			};
		}

		foreach (var t in fac.Text)
		{
			if (!byObjectId.TryGetValue((uint)t.StableId, out var node))
			{
				UnmatchedFacRecords++;
				continue;
			}
			node.Text = BuildText(t, strings);
		}

		// LIGHT records address the .bsg scene graph, so they are not joined here.
	}

	/// <summary>
	/// Replaces each node's visibility with the guest's own <c>globalactive</c>, overriding the
	/// slide-derived state entirely. The guest derives it every frame as
	/// <c>active AND in-time-range AND parent's globalactive</c> and gates drawing on it; the
	/// parent chain is folded in guest-side, so an element whose ancestor is off arrives false
	/// and the builder drops its subtree. A node the guest did not report keeps the file's answer.
	/// </summary>
	private void ApplyLiveVisibility(IReadOnlyList<bool> liveVisible)
	{
		// Keep what the file said before overwriting it, so the disagreement is measurable.
		_fileVisible = new bool[Nodes.Length];
		for (int i = 0; i < Nodes.Length; ++i)
		{
			_fileVisible[i] = Nodes[i].Visible;
		}

		for (int i = 0; i < Nodes.Length && i < liveVisible.Count; ++i)
		{
			Nodes[i].Visible = liveVisible[i];
		}
	}

	private bool[]? _fileVisible;

	/// <summary>
	/// What the .bgf's own slide state said about element <paramref name="index"/>, before the
	/// guest's <c>globalactive</c> replaced it. Falls back to the live value when no live
	/// visibility was applied.
	/// </summary>
	public bool FileVisible(int index) =>
		_fileVisible is not null && index >= 0 && index < _fileVisible.Length
			? _fileVisible[index]
			: index >= 0 && index < Nodes.Length && Nodes[index].Visible;

	/// <summary>The guest's live tint for one element: a weight, and the colour it weights.</summary>
	public readonly record struct LiveTint(float Amount, float? R, float? G, float? B);

	/// <summary>
	/// Applies the guest's <c>TintingAmount</c> to each quad that has one. A null entry means the
	/// guest states no amount and the authored lane stands — unstated is not zero. Elements with
	/// no quad are skipped: the attribute is legal anywhere, but only a quad has a colour.
	/// </summary>
	private void ApplyLiveTint(IReadOnlyList<LiveTint?> liveTint)
	{
		for (int i = 0; i < Nodes.Length && i < liveTint.Count; ++i)
		{
			if (liveTint[i] is not { } tint)
			{
				continue;
			}

			// THE ATTRIBUTE IS ON A MATERIAL CHILD; THE QUAD IS ON ITS PARENT.
			// `g_MenuFormatter:SetTintingAmountAndDiffuseColour` targets `GetFirstChild(border)`,
			// so the element carrying `TintingAmount` never has a quad and vice versa. Hoisting
			// to the parent is the first-child rule `Engine_UiMenuCloning.TintWritesUnder` uses.
			UiQuad? quad = Nodes[i].Quad;
			if (quad is null && Nodes[i].Parent is { Quad: { } parentQuad })
			{
				quad = parentQuad;
			}

			if (quad is null)
			{
				continue;
			}

			quad.TintAmount = tint.Amount;

			// Colour only where the guest states it — an unstated channel keeps the .fac's,
			// which is the same "unstated is not zero" rule the transform read follows.
			if (tint.R is { } r) quad.R = r;
			if (tint.G is { } g) quad.G = g;
			if (tint.B is { } b) quad.B = b;

			++LiveTintsApplied;
		}
	}

	/// <summary>Quads whose tint weight the guest stated. Zero on a screen it never tints.</summary>
	public int LiveTintsApplied;

	/// <summary>
	/// Replaces each node's opacity with the guest's. Indexed by element ordinal like the
	/// slides; a node the guest did not report keeps the authored value.
	/// </summary>
	private void ApplyLiveOpacity(IReadOnlyList<float?> liveOpacity)
	{
		for (int i = 0; i < Nodes.Length && i < liveOpacity.Count; ++i)
		{
			if (liveOpacity[i] is { } opacity)
			{
				Nodes[i].Opacity = opacity;
			}
		}
	}

	/// <summary>
	/// Applies the guest's live transform attributes over the file's. A slide moves an element by
	/// writing attributes (`setAttribute(el, 'position.x', …)`) and the shipped Lua does the same
	/// for anything it lays out. A null entry means the guest states none and the file stands.
	/// </summary>
	private void ApplyLiveTransform(IReadOnlyList<float?[]?> liveTransform)
	{
		for (int i = 0; i < Nodes.Length && i < liveTransform.Count; ++i)
		{
			if (liveTransform[i] is not { } values)
			{
				continue;
			}

			UiNode n = Nodes[i];
			if (values.Length > 0 && values[0] is { } x) n.PositionX = x;
			if (values.Length > 1 && values[1] is { } y) n.PositionY = y;
			if (values.Length > 2 && values[2] is { } z) n.PositionZ = z;
			if (values.Length > 3 && values[3] is { } sx) n.ScaleX = sx;
			if (values.Length > 4 && values[4] is { } sy) n.ScaleY = sy;
			if (values.Length > 5 && values[5] is { } sz) n.ScaleZ = sz;
		}
	}

	/// <summary>
	/// Replaces each quad's texture with the one the guest has put on that element. The file's
	/// texture is a placeholder for a whole class of widget — the dpad quick-slots are authored
	/// on `art\gui\expressions\empty.tex`, the HUD's `WorldIcons` slots share one icon. A zero
	/// hash means the guest states nothing and the file stands; a hash that resolves to no path
	/// is also left alone, being a gap in the name table.
	/// </summary>
	private void ApplyLiveTextures(IReadOnlyList<uint> liveTextures, Engine_TextureNameTable? textures)
	{
		if (textures is null)
		{
			return;
		}

		for (int i = 0; i < Nodes.Length && i < liveTextures.Count; ++i)
		{
			uint hash = liveTextures[i];
			if (hash == 0)
			{
				continue;
			}

			++LiveTexturesStated;

			if (Nodes[i].Quad is not { } quad)
			{
				++LiveTexturesNotAQuad;
				continue;
			}

			if (quad.TextureHash == hash)
			{
				++LiveTexturesSame;
				continue;
			}

			if (!textures.TryResolve(hash, out string path))
			{
				++LiveTexturesUnresolved;

				// Named, not just counted: an unresolved hash is either a gap in the name table
				// or a surface the guest RENDERS INTO (a Bink target), and those need opposite
				// work. Capped — a genuine table gap would print thousands.
				if (UnresolvedTextures.Count < 8)
				{
					UnresolvedTextures.Add($"{Nodes[i].Name}#{i}=0x{hash:X8}");
				}

				continue;
			}

			quad.TextureHash = hash;
			quad.TexturePath = path;
			++LiveTexturesApplied;
		}
	}

	/// <summary>Quads whose texture the guest replaced. Zero on a screen that swaps none.</summary>
	public int LiveTexturesApplied;

	/// <summary>
	/// Elements for which the guest stated ANY texture, applied or not. The counters below split
	/// the four causes of "0 swapped": array never read, guest states nothing, guest states what
	/// the file already says, or a hash the name table cannot resolve — only the last is ours.
	/// </summary>
	public int LiveTexturesStated;

	/// <summary>Stated textures identical to the file's — the guest confirming, not swapping.</summary>
	public int LiveTexturesSame;

	/// <summary>Stated textures whose hash is not in the name table. These are the losses.</summary>
	public int LiveTexturesUnresolved;

	/// <summary>The first few unresolved ones by element and hash — see ApplyLiveTextures.</summary>
	public readonly List<string> UnresolvedTextures = new();

	/// <summary>
	/// Stated textures on an element with no quad. Not a defect: the guest holds texture
	/// references on Material and Texture elements too, and those never draw.
	/// </summary>
	public int LiveTexturesNotAQuad;

	/// <summary>True when the host handed us a per-element texture array at all.</summary>
	public bool LiveTexturesRead;

	/// <summary>
	/// One line describing the texture-swap pass. Not silent when nothing happened: an array read
	/// back entirely zero is a different state from one never read.
	/// </summary>
	public string DescribeLiveTextures()
	{
		if (!LiveTexturesRead)
		{
			return ", no live texture array";
		}

		if (LiveTexturesStated == 0)
		{
			return ", live textures read but every element stated 0";
		}

		return $", textures stated {LiveTexturesStated}: {LiveTexturesApplied} swapped, " +
			$"{LiveTexturesSame} already correct" +
			(LiveTexturesNotAQuad > 0
				? $", {LiveTexturesNotAQuad} on elements that draw no quad"
				: string.Empty) +
			(LiveTexturesUnresolved > 0
				? $", {LiveTexturesUnresolved} NOT IN THE NAME TABLE " +
				  $"[{string.Join(", ", UnresolvedTextures)}]"
				: string.Empty);
	}

	/// <summary>
	/// Replaces each text node's string with the guest's, keyed by the element's sdbm name hash.
	/// A node the guest did not report keeps whatever the file resolved to.
	/// </summary>
	private void ApplyLiveText(IReadOnlyList<string?> liveText)
	{
		for (int i = 0; i < Nodes.Length && i < liveText.Count; ++i)
		{
			if (Nodes[i].Text is not { } text || liveText[i] is not { Length: > 0 } live)
			{
				continue;
			}

			text.Display = live;
			text.Live = true;
		}
	}

	private static UiText BuildText(FacTextRecord t, Engine_BabelDatabase? strings)
	{
		var ui = new UiText
		{
			Tag = t.Text,
			Size = t.Size,
			HorzAlign = t.HorzAlign,
			VertAlign = t.VertAlign,
			WrapMode = t.WrapMode,
			// The element's own canvas, not the presentation's: 960 in Fable 2, 1280 in Fable 3
			// (Engine_UiTextContract).
			BoxWidth = t.BoxWidth * Engine_UiTextContract.Active.BoxWidthUnits,
			BoxHeight = t.BoxHeight * Engine_UiTextContract.Active.BoxHeightUnits,
			R = t.R, G = t.G, B = t.B,
			Source = t,
		};

		if (t.IsEmpty)
			return ui;

		// The engine tries the tag first and falls back to the authored string verbatim, which is
		// how literals like "Ready to Quit ?" reach the screen.
		if (strings is not null && strings.TryGetString(t.Text, out var localised))
		{
			ui.Display = localised;
			ui.Localised = true;
		}
		else
		{
			ui.Display = t.Text;
		}
		return ui;
	}

	/// <summary>
	/// Element kinds that occupy a slot in the <c>.bsg</c> scene graph. Material, Texture and
	/// Behavior do not, which is what makes the sequential mapping work.
	/// </summary>
	public static bool IsSpatial(BgfElementType type) => type is
		BgfElementType.Node or BgfElementType.Camera or BgfElementType.Light or
		BgfElementType.Text or BgfElementType.Component;

	/// <summary>
	/// A float attribute for element <paramref name="ordinal"/>, the applied slide's write taking
	/// precedence over the element's own.
	///
	/// A slide command overwrites an existing attribute record and never creates one:
	/// <c>sub_82284798</c> (Lua's <c>element:setAttribute()</c> entry point) calls
	/// <c>sub_822848E0</c> to find the record by id and returns doing nothing when there is none
	/// (<c>beq</c> at 0x822847C8). The element's record keeps its own TYPE — the applier forwards
	/// only the id and the raw value word (0x82BB6934).
	/// </summary>
	private static float Float(Engine_BgfDocument doc, int ordinal, BgfElement e, string attribute,
		float fallback)
	{
		uint id = Engine_GameHash.AttributeId(attribute);

		if (!e.TryGetAttribute(id, out var a) || a.Type != BgfValueType.Float)
			return fallback;

		return doc.SlideState.TryGetOverride(ordinal, id, out BgfAttribute o)
			? o.AsFloat()
			: a.AsFloat();
	}

	/// <summary>
	/// One element's float attribute UNDER A GIVEN SLIDE STATE — the layered read against a slide
	/// selection chosen at runtime rather than the one this screen was parsed with.
	///
	/// A slide is not only a visibility mask: its commands are ATTRIBUTE WRITES, and most of them
	/// reposition. The front end's menu assembly is authored at x −800, off canvas, and every root
	/// slide slides it in — a pattern-book tree is parsed with the preview slide and must be told.
	///
	/// Three answers, in order:
	/// <list type="number">
	/// <item>the live slide wrote this attribute — that is the value;</item>
	/// <item>it did not, but the PARSE-TIME slide did — so the parsed node carries a value from a
	/// slide that is not the one showing, and the element's OWN attribute is the honest base;</item>
	/// <item>neither wrote it — the parsed node's value already is the answer, `.bsg` base
	/// included, and that is the only layer this class does not keep separately.</item>
	/// </list>
	/// </summary>
	public float AttributeUnder(BgfSlideState live, int ordinal, string attribute, float parsed)
	{
		if (ordinal < 0 || ordinal >= Document.Elements.Length)
		{
			return parsed;
		}

		uint id = Engine_GameHash.AttributeId(attribute);
		if (live.TryGetOverride(ordinal, id, out BgfAttribute slid) &&
			slid.Type == BgfValueType.Float)
		{
			return slid.AsFloat();
		}

		if (Document.SlideState.TryGetOverride(ordinal, id, out _) &&
			Document.Elements[ordinal].TryGetAttribute(id, out BgfAttribute own) &&
			own.Type == BgfValueType.Float)
		{
			return own.AsFloat();
		}

		return parsed;
	}

	/// <summary>Depth-first walk from the roots.</summary>
	public IEnumerable<UiNode> Walk()
	{
		var stack = new Stack<UiNode>();
		for (int i = Roots.Length - 1; i >= 0; i--)
			stack.Push(Roots[i]);

		while (stack.Count > 0)
		{
			UiNode n = stack.Pop();
			yield return n;
			for (int i = n.Children.Count - 1; i >= 0; i--)
				stack.Push(n.Children[i]);
		}
	}
}

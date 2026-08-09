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
	/// True when the atlas rectangle has no area. 442 of the 15,790 shipped quads are
	/// like this — 249 with zero U extent, 251 with zero V, and 36 outright negative.
	/// The values are cleanly authored, so this is real data rather than a parse error:
	/// almost certainly elements that start collapsed and animate open, since
	/// <c>scaleu</c> and <c>scalev</c> are among the animated attributes.
	/// Keep them — dropping one loses whatever it animates into — but expect them to
	/// render as nothing until a timeline drives them.
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
	/// False when the element carried no <c>name</c> attribute. 15 of the 52 shipped
	/// screens are exported this way — the prototypes plus a handful of small ones like
	/// <c>coopquit</c> and <c>holdbutton</c> — so a consumer cannot rely on names being
	/// present. The element's own hash field is still <c>sdbm32</c> of the original name,
	/// so it remains a stable identity even when the string is gone.
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

	public bool IsRoot => Parent is null;
}

/// <summary>
/// A screen assembled from its <c>.bgf</c> presentation and <c>.fac</c> sidecar, with
/// textures and localised strings resolved.
///
/// This is the converter's input model: everything joined and named, with no engine
/// types involved, so it can be inspected and tested before anything emits a scene.
///
/// Joining is by <see cref="BgfElement.ObjectId"/>. Two quirks are handled rather than
/// asserted away — <c>LIGHT</c> records address the <c>.bsg</c> scene graph and never
/// match an element, and a couple of shipped <c>UV</c> records are stale exporter
/// leftovers pointing at deleted elements. Both are counted in
/// <see cref="UnmatchedFacRecords"/> rather than throwing.
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
		IReadOnlyList<string?>? liveText = null)
	{
		var doc = Engine_BgfDocument.Parse(bgfBytes);

		// Before anything reads MasterActive or an override: node visibility and every
		// attribute lookup are resolved against the slide state, so choosing slides
		// afterwards would leave a screen built from the previous ones.
		//
		// The guest's own per-element slides win when we have them; the single scene
		// slide is the fallback for a preview with no guest attached.
		bool live = liveSlides is { Count: > 0 } && doc.SetElementSlides(liveSlides);
		if (!live && sceneSlide > 0)
		{
			doc.SetSceneSlide(sceneSlide);
		}

		BsgSceneNode[]? sceneNodes = null;
		if (bsgBytes is not null)
		{
			// A malformed sidecar must not take the screen down with it — the .bgf
			// alone still renders, just without the base layer.
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

			// The .bgf is an OVERRIDE layer: sub_82188D10 writes only the attributes an
			// element actually carries, and everything else keeps its .bsg base. About
			// 1.3% of spatial elements omit a transform component, so without this they
			// silently fall back to 0/1 instead of their authored base.
			BsgSceneNode? b = null;
			if (IsSpatial(e.Type) && sceneNodes is not null && spatialIndex < sceneNodes.Length)
				b = sceneNodes[spatialIndex];
			if (IsSpatial(e.Type))
				spatialIndex++;

			// Three layers, lowest first: the .bsg base, the .bgf element's own
			// attributes, then the applied slide's commands. The slide is the engine's
			// LAST write — the element's attributes go on at load and a slide's on
			// switch — so it wins, and dropping that layer leaves a component's pieces
			// at their superset positions instead of the layout the slide builds.
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

		// Last, over everything the file said: what the element ACTUALLY holds right now.
		//
		// The shipped presentation carries placeholders — the dialog panel authors
		// "DEFAULT_TEXT_ID" — and some tags resolve through a table that is not the Babel
		// book (GUI_CONTINUE_WITHOUT_SAVING is in neither, under any of the engine's hash
		// variants). Both are moot if we take the string the game is displaying.
		screen.IsLive = live;
		if (live && liveText is { Count: > 0 })
		{
			screen.ApplyLiveText(liveText);
		}

		return screen;
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
	/// Replaces each text node's string with the one the running guest holds, keyed by
	/// the element's sdbm name hash.
	///
	/// A node the guest did not report keeps whatever the file resolved to, so a partial
	/// read degrades to the authored text rather than blanking the screen.
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
			BoxWidth = t.BoxWidth * 960f,
			BoxHeight = t.BoxHeight * 720f,
			R = t.R, G = t.G, B = t.B,
			Source = t,
		};

		if (t.IsEmpty)
			return ui;

		// The engine tries the tag first and falls back to displaying the authored
		// string verbatim, which is how literals like "Ready to Quit ?" get on screen.
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
	/// Element kinds that occupy a slot in the <c>.bsg</c> scene graph. Material,
	/// Texture and Behavior do not — which is what makes the sequential mapping work.
	/// Verified across all 52 screens: the count matches every file and all 26,153
	/// parent links validate.
	/// </summary>
	public static bool IsSpatial(BgfElementType type) => type is
		BgfElementType.Node or BgfElementType.Camera or BgfElementType.Light or
		BgfElementType.Text or BgfElementType.Component;

	/// <summary>
	/// A float attribute for element <paramref name="ordinal"/>, with the applied
	/// slide's write taking precedence over the element's own.
	///
	/// A slide command OVERWRITES an existing attribute record; it never creates one.
	/// <c>sub_82284798</c> — the same entry point Lua's <c>element:setAttribute()</c>
	/// uses — calls <c>sub_822848E0</c> to find the record by id and returns doing
	/// nothing when there is none (<c>beq</c> at 0x822847C8). So a command naming an
	/// attribute the element does not carry is silently dropped by the engine, and
	/// honouring it here would move elements the game leaves alone.
	///
	/// The element's record also keeps its own TYPE — the applier forwards only the id
	/// and the raw value word (0x82BB6934), so the element is the authority on how the
	/// value word is read, not the command.
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

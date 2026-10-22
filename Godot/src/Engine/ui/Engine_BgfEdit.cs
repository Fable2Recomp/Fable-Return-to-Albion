using System;
using System.Collections.Generic;
using System.Linq;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// Edits on a parsed presentation — the <c>.bgf</c> and its <c>.fac</c> / <c>.bsg</c> sidecars —
/// that keep it one the engine's loader and our parser read the same way: a node added, cloned,
/// renamed or moved, with every ordinal reference — parents, slide owners and entries, animation
/// tracks, contracts — kept pointing at the element it pointed at, and the sidecar records that
/// give a node its art following it. <see cref="Engine_BgfWriter.Recount"/> restates the
/// allocation manifest after; the writers' <c>TryWrite</c> prove the result parses back equal.
///
/// THE ELEMENT ORDER IS THE TREE. Elements are stored depth-first, child before sibling, and the
/// runtime walks them in that order; every reference is an ordinal into it. An insertion
/// therefore goes at the end of the parent's subtree and shifts every ordinal at or past it.
///
/// THE SIDECARS JOIN TWO WAYS. The <c>.bsg</c> scene node of an element is POSITIONAL — the
/// k-th spatial element (Node, Camera, Light, Text, Component; <see cref="Engine_UiScreen.IsSpatial"/>)
/// takes the k-th node, and a node's parent is a node index — so an inserted spatial element gets
/// a node inserted at its spatial index and the parent indices past it shift. The <c>.fac</c>
/// records and the <c>.bsg</c> asset records are keyed on <see cref="BgfElement.ObjectId"/>, so a
/// copy gets them re-keyed to its fresh id.
/// </summary>
public static class Engine_BgfEdit
{
	public static readonly uint NameId = Engine_GameHash.AttributeId("name");
	public static readonly uint PositionXId = Engine_GameHash.AttributeId("position.x");
	public static readonly uint PositionYId = Engine_GameHash.AttributeId("position.y");
	public static readonly uint ScaleXId = Engine_GameHash.AttributeId("scale.x");
	public static readonly uint ScaleYId = Engine_GameHash.AttributeId("scale.y");

	/// <summary>The side of the shared quad every textured node is a scale of (<c>Engine_UiSceneBuilder.QuadBaseSize</c>).</summary>
	public const float QuadBaseSize = 100f;

	/// <summary>The index of <paramref name="text"/> in the string table, appended when absent.</summary>
	public static int StringIndex(Engine_BgfDocument doc, string text)
	{
		int at = Array.IndexOf(doc.Strings, text);
		if (at >= 0)
		{
			return at;
		}

		Array.Resize(ref doc.Strings, doc.Strings.Length + 1);
		doc.Strings[^1] = text;
		return doc.Strings.Length - 1;
	}

	/// <summary>The element's authored name, from its <c>name</c> attribute.</summary>
	public static string NameOf(Engine_BgfDocument doc, int ordinal) =>
		ordinal >= 0 && ordinal < doc.Elements.Length ? doc.GetName(doc.Elements[ordinal]) : string.Empty;

	/// <summary>Renames an element: its name hash and its <c>name</c> attribute both state the new name.</summary>
	public static void Rename(Engine_BgfDocument doc, int ordinal, string name)
	{
		BgfElement e = doc.Elements[ordinal];
		e.NameHash = Engine_GameHash.Sdbm(name);
		SetAttribute(e, new BgfAttribute(NameId, BgfValueType.String, unchecked((uint)StringIndex(doc, name))));
		Engine_BgfWriter.Recount(doc);
	}

	/// <summary>Writes a float attribute on an element, replacing the one of that id or appending it.</summary>
	public static void SetFloat(Engine_BgfDocument doc, int ordinal, string attributeName, float value)
	{
		SetAttribute(doc.Elements[ordinal], new BgfAttribute(
			Engine_GameHash.AttributeId(attributeName), BgfValueType.Float, BitConverter.SingleToUInt32Bits(value)));
		Engine_BgfWriter.Recount(doc);
	}

	/// <summary>The element's authored position, from its <c>position.x</c> / <c>position.y</c> attributes (0 where absent).</summary>
	public static (float X, float Y) PositionOf(Engine_BgfDocument doc, int ordinal)
	{
		BgfElement e = doc.Elements[ordinal];
		return (
			e.TryGetAttribute(PositionXId, out BgfAttribute x) && x.Type == BgfValueType.Float ? x.AsFloat() : 0f,
			e.TryGetAttribute(PositionYId, out BgfAttribute y) && y.Type == BgfValueType.Float ? y.AsFloat() : 0f);
	}

	/// <summary>Moves an element on the canvas: its <c>position.x</c> and <c>position.y</c>.</summary>
	public static void Move(Engine_BgfDocument doc, int ordinal, float x, float y)
	{
		SetFloat(doc, ordinal, "position.x", x);
		SetFloat(doc, ordinal, "position.y", y);
	}

	/// <summary>
	/// One past the last element of <paramref name="ordinal"/>'s subtree. Elements are depth-first,
	/// so the subtree is the contiguous run from the element through every element whose parent
	/// chain reaches it.
	/// </summary>
	public static int SubtreeEnd(Engine_BgfDocument doc, int ordinal)
	{
		int end = ordinal + 1;
		while (end < doc.Elements.Length && Descends(doc, end, ordinal))
		{
			++end;
		}

		return end;
	}

	private static bool Descends(Engine_BgfDocument doc, int element, int ancestor)
	{
		for (int at = doc.Elements[element].ParentOrdinal; at >= 0 && at < doc.Elements.Length; at = doc.Elements[at].ParentOrdinal)
		{
			if (at == ancestor)
			{
				return true;
			}

			if (at >= element)
			{
				break;
			}
		}

		return false;
	}

	/// <summary>
	/// The <c>.bsg</c> node index of an element: the count of spatial elements before it. For a
	/// non-spatial element (Material, Texture, Behavior) it is the index the next spatial one takes.
	/// </summary>
	public static int SpatialIndex(Engine_BgfDocument doc, int ordinal)
	{
		int n = 0;
		for (int i = 0; i < ordinal && i < doc.Elements.Length; i++)
		{
			if (Engine_UiScreen.IsSpatial(doc.Elements[i].Type))
			{
				++n;
			}
		}

		return n;
	}

	/// <summary>
	/// Adds an empty, active <see cref="BgfElementType.Node"/> as the last child of
	/// <paramref name="parent"/>, named and placed, with its <c>.bsg</c> group node when the scene
	/// graph is given; its ordinal.
	/// </summary>
	public static int AddChild(Engine_BgfDocument doc, int parent, string name, float x = 0f, float y = 0f, Engine_BsgDocument? bsg = null)
	{
		if (parent < 0 || parent >= doc.Elements.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(parent));
		}

		int at = SubtreeEnd(doc, parent);
		var node = new BgfElement
		{
			NameHash = Engine_GameHash.Sdbm(name),
			ParentOrdinal = parent,
			ObjectId = FreshObjectId(doc),
			Type = BgfElementType.Node,
			Flags = 1,
			Attributes = new[]
			{
				new BgfAttribute(NameId, BgfValueType.String, unchecked((uint)StringIndex(doc, name))),
				new BgfAttribute(PositionXId, BgfValueType.Float, BitConverter.SingleToUInt32Bits(x)),
				new BgfAttribute(PositionYId, BgfValueType.Float, BitConverter.SingleToUInt32Bits(y)),
			},
		};
		int spatial = SpatialIndex(doc, at);
		int parentNode = SpatialIndex(doc, parent);
		Insert(doc, at, new[] { node });
		if (bsg is not null)
		{
			InsertNodes(bsg, spatial, new[] { GroupNode(parentNode, node.ObjectId) });
		}

		return at;
	}

	/// <summary>
	/// Adds a textured quad: a Node sized <paramref name="width"/> × <paramref name="height"/>
	/// canvas units (the shared quad scaled), its <c>.bsg</c> geometry node, and a <c>.fac</c> UV
	/// record sampling <paramref name="texturePath"/> — a shipped texture's bank path or a mod's
	/// (a mod's <c>foo.tex.png</c> is served as <c>foo.tex</c>, and that is the path to name) —
	/// at the authored atlas rectangle (<paramref name="u0"/>, <paramref name="v0"/>, size
	/// <paramref name="du"/> × <paramref name="dv"/>; 0, 0, 1, 1 is the whole texture). Its ordinal.
	/// </summary>
	public static int AddQuad(
		Engine_BgfDocument doc, Engine_FacDocument fac, Engine_BsgDocument? bsg,
		int parent, string name, float x, float y, float width, float height,
		string texturePath, float u0 = 0f, float v0 = 0f, float du = 1f, float dv = 1f)
	{
		int at = AddChild(doc, parent, name, x, y, bsg);
		SetFloat(doc, at, "scale.x", width / QuadBaseSize);
		SetFloat(doc, at, "scale.y", height / QuadBaseSize);
		uint id = doc.Elements[at].ObjectId;
		if (bsg is not null)
		{
			BsgSceneNode node = bsg.Nodes[SpatialIndex(doc, at)];
			node.NodeType = 2;
			node.ForceTranslucent = true;
			(node.MinX, node.MinY, node.MinZ) = (-0.5f * QuadBaseSize, -0.5f * QuadBaseSize, 0f);
			(node.MaxX, node.MaxY, node.MaxZ) = (0.5f * QuadBaseSize, 0.5f * QuadBaseSize, 0f);
		}

		fac.Add(new FacUvRecord
		{
			StableId = unchecked((int)id),
			TextureHash = Engine_GameHash.ResourcePath(texturePath),
			Du = du,
			Dv = dv,
			U0 = u0,
			V0 = v0,
			R = 1f,
			G = 1f,
			B = 1f,
		});
		return at;
	}

	/// <summary>
	/// Duplicates an element and its subtree as the next sibling of the source, the copy's root
	/// renamed. Every copied element gets a fresh object id; parent links inside the copy point
	/// into the copy. With the sidecars given, the copy's <c>.bsg</c> nodes are inserted at its
	/// spatial index and every <c>.fac</c> UV / TEXT / LIGHT record and <c>.bsg</c> asset record
	/// keyed on a copied element is duplicated under the copy's id, so the copy draws as its
	/// source does. Slides, tracks and contracts that name the source are not duplicated.
	/// </summary>
	public static int Clone(Engine_BgfDocument doc, int ordinal, string name, Engine_FacDocument? fac = null, Engine_BsgDocument? bsg = null)
	{
		if (ordinal < 0 || ordinal >= doc.Elements.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(ordinal));
		}

		int end = SubtreeEnd(doc, ordinal);
		int count = end - ordinal;
		int spatialStart = SpatialIndex(doc, ordinal);
		int spatialEnd = SpatialIndex(doc, end);
		uint nextId = FreshObjectId(doc);
		var copies = new BgfElement[count];
		var newIds = new Dictionary<int, uint>();
		for (int i = 0; i < count; i++)
		{
			BgfElement src = doc.Elements[ordinal + i];
			copies[i] = new BgfElement
			{
				NameHash = src.NameHash,
				// Inside the subtree a parent is relocated with the copy; the root keeps the source's parent.
				ParentOrdinal = i == 0 ? src.ParentOrdinal : src.ParentOrdinal - ordinal + end,
				ObjectId = nextId++,
				Type = src.Type,
				Flags = src.Flags,
				ComponentExtra = src.ComponentExtra,
				ComponentPad = src.ComponentPad,
				Attributes = (BgfAttribute[])src.Attributes.Clone(),
			};
			newIds[ordinal + i] = copies[i].ObjectId;
		}

		if (bsg is not null && spatialEnd <= bsg.Nodes.Length)
		{
			int spatialCount = spatialEnd - spatialStart;
			var nodes = new BsgSceneNode[spatialCount];
			int k = 0;
			for (int i = 0; i < count; i++)
			{
				if (!Engine_UiScreen.IsSpatial(doc.Elements[ordinal + i].Type))
				{
					continue;
				}

				BsgSceneNode src = bsg.Nodes[spatialStart + k];
				BsgSceneNode copy = CopyNode(src);
				copy.ObjectId = copies[i].ObjectId;
				bool inside = src.ParentIndex >= spatialStart && src.ParentIndex < spatialEnd;
				copy.ParentIndex = inside ? src.ParentIndex - spatialStart + spatialEnd : src.ParentIndex;
				nodes[k++] = copy;
			}

			InsertNodes(bsg, spatialEnd, nodes);
		}

		if (fac is not null)
		{
			var byOldId = new Dictionary<uint, uint>();
			for (int i = 0; i < count; i++)
			{
				byOldId.TryAdd(doc.Elements[ordinal + i].ObjectId, newIds[ordinal + i]);
			}

			foreach (FacRecord record in fac.Records.ToArray())
			{
				if (record.Uv is { } uv && byOldId.TryGetValue(unchecked((uint)uv.StableId), out uint uvId))
				{
					fac.Add(new FacUvRecord
					{
						StableId = unchecked((int)uvId), Extra = (string[])uv.Extra.Clone(), TextureHash = uv.TextureHash,
						Du = uv.Du, Dv = uv.Dv, U0 = uv.U0, V0 = uv.V0, DetailMapMode = uv.DetailMapMode,
						SecondaryTexture = uv.SecondaryTexture, Field12 = uv.Field12, VertexLane3 = uv.VertexLane3,
						R = uv.R, G = uv.G, B = uv.B,
					});
				}
				else if (record.Text is { } text && byOldId.TryGetValue(unchecked((uint)text.StableId), out uint textId))
				{
					fac.Add(new FacTextRecord
					{
						StableId = unchecked((int)textId), Text = text.Text, WrapMode = text.WrapMode, Size = text.Size,
						BoxWidth = text.BoxWidth, BoxHeight = text.BoxHeight, HorzAlign = text.HorzAlign,
						VertAlign = text.VertAlign, Style = text.Style, R = text.R, G = text.G, B = text.B,
					});
				}
				else if (record.Light is { } light && byOldId.TryGetValue(unchecked((uint)light.StableId), out uint lightId))
				{
					fac.Add(new FacLightRecord { StableId = unchecked((int)lightId), R = light.R, G = light.G, B = light.B });
				}
			}

			if (bsg is not null)
			{
				var assets = new List<BsgAsset>(bsg.Assets);
				foreach (BsgAsset asset in bsg.Assets)
				{
					if (byOldId.TryGetValue(asset.ObjectId, out uint assetId))
					{
						assets.Add(new BsgAsset(assetId, asset.Type));
					}
				}

				bsg.Assets = assets.ToArray();
			}
		}

		Insert(doc, end, copies);
		Rename(doc, end, name);
		return end;
	}

	/// <summary>The lowest object id above every one in use.</summary>
	public static uint FreshObjectId(Engine_BgfDocument doc) =>
		doc.Elements.Length == 0 ? 1u : doc.Elements.Max(e => e.ObjectId) + 1u;

	/// <summary>A group node (type 1) as every shipped Component and childless Node carries: identity transform, the empty-bounds sentinel.</summary>
	private static BsgSceneNode GroupNode(int parentIndex, uint objectId) => new()
	{
		ParentIndex = parentIndex,
		ObjectId = objectId,
		NodeType = 1,
		ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f,
		MinX = 1f, MinY = 1f, MinZ = 1f,
		MaxX = -1f, MaxY = -1f, MaxZ = -1f,
		Opacity = 1f,
		LayerIndex = 0,
		RotationOrder = 4,
	};

	private static BsgSceneNode CopyNode(BsgSceneNode s) => new()
	{
		ParentIndex = s.ParentIndex, ObjectId = s.ObjectId, NodeType = s.NodeType,
		PositionX = s.PositionX, PositionY = s.PositionY, PositionZ = s.PositionZ,
		RotationX = s.RotationX, RotationY = s.RotationY, RotationZ = s.RotationZ,
		ScaleX = s.ScaleX, ScaleY = s.ScaleY, ScaleZ = s.ScaleZ,
		PivotX = s.PivotX, PivotY = s.PivotY, PivotZ = s.PivotZ,
		MinX = s.MinX, MinY = s.MinY, MinZ = s.MinZ, MaxX = s.MaxX, MaxY = s.MaxY, MaxZ = s.MaxZ,
		Opacity = s.Opacity, LayerIndex = s.LayerIndex, RotationOrder = s.RotationOrder,
		Orientation = s.Orientation, ForceTranslucent = s.ForceTranslucent,
		Raw = (byte[])s.Raw.Clone(),
	};

	/// <summary>Inserts scene nodes at <paramref name="at"/>, shifting every parent index at or past it; the inserted nodes' parent indices are taken as final.</summary>
	private static void InsertNodes(Engine_BsgDocument bsg, int at, BsgSceneNode[] inserted)
	{
		int n = inserted.Length;
		foreach (BsgSceneNode node in bsg.Nodes)
		{
			if (node.ParentIndex >= at)
			{
				node.ParentIndex += n;
			}
		}

		var nodes = new List<BsgSceneNode>(bsg.Nodes.Length + n);
		nodes.AddRange(bsg.Nodes.Take(at));
		nodes.AddRange(inserted);
		nodes.AddRange(bsg.Nodes.Skip(at));
		bsg.Nodes = nodes.ToArray();
	}

	/// <summary>
	/// Inserts elements at <paramref name="at"/>, shifting every ordinal reference at or past it
	/// by their count. The inserted records' own parent ordinals are taken as final.
	/// </summary>
	private static void Insert(Engine_BgfDocument doc, int at, BgfElement[] inserted)
	{
		int n = inserted.Length;
		int Shifted(int ordinal) => ordinal >= at ? ordinal + n : ordinal;

		foreach (BgfElement e in doc.Elements)
		{
			e.ParentOrdinal = Shifted(e.ParentOrdinal);
		}

		foreach (BgfSlide s in doc.Slides)
		{
			s.OwnerElement = Shifted(s.OwnerElement);
			for (int i = 0; i < s.Entries.Length; i++)
			{
				BgfSlideEntry entry = s.Entries[i];
				s.Entries[i] = new BgfSlideEntry
				{
					ElementOrdinal = Shifted(entry.ElementOrdinal),
					Active = entry.Active,
					Commands = entry.Commands,
					EventCount = entry.EventCount,
					ActivatedAnimations = entry.ActivatedAnimations,
					AnimationEvents = entry.AnimationEvents,
				};
			}
		}

		foreach (BgfAnimation a in doc.Animations)
		{
			a.ElementOrdinal = Shifted(a.ElementOrdinal);
		}

		foreach (BgfContract c in doc.Contracts)
		{
			c.ElementOrdinal = Shifted(c.ElementOrdinal);
		}

		var elements = new List<BgfElement>(doc.Elements.Length + n);
		elements.AddRange(doc.Elements.Take(at));
		elements.AddRange(inserted);
		elements.AddRange(doc.Elements.Skip(at));
		doc.Elements = elements.ToArray();

		Engine_BgfWriter.Recount(doc);
		doc.SlideState = doc.ComputeSlideState(null);
	}

	private static void SetAttribute(BgfElement e, BgfAttribute value)
	{
		for (int i = 0; i < e.Attributes.Length; i++)
		{
			if (e.Attributes[i].Id == value.Id)
			{
				e.Attributes[i] = value;
				return;
			}
		}

		Array.Resize(ref e.Attributes, e.Attributes.Length + 1);
		e.Attributes[^1] = value;
	}

	/// <summary>
	/// A presentation with nothing in it but a root: one active component element named
	/// <paramref name="sceneName"/>, no slides, no tracks. The shape a shipped file starts from:
	/// every shipped root is <c>flags 5</c> (active component) with no attributes, and the
	/// commonest carries pad 3 and extra 500 (both bytes our parser carries and does not read).
	/// The header's reserved bytes are the shipped constant ones — byte 1 is 4 and the word at 2
	/// is 26 in all 52 files — with the per-file bytes at 26..31 left zero.
	/// </summary>
	public static Engine_BgfDocument NewDocument(string sceneName, int canvasWidth, int canvasHeight, bool bigEndian)
	{
		var header = new byte[Engine_BgfDocument.SectionsStart];
		header[1] = 4;
		if (bigEndian)
		{
			header[5] = 26;
		}
		else
		{
			header[2] = 26;
		}

		var doc = new Engine_BgfDocument
		{
			BigEndian = bigEndian,
			Version = 3,
			CanvasWidth = canvasWidth,
			CanvasHeight = canvasHeight,
			Header = header,
			MemoryCounts = new uint[Engine_BgfDocument.MemoryCountCount],
			MemoryExtra = new uint[6],
			Strings = Array.Empty<string>(),
			Elements = new[]
			{
				new BgfElement
				{
					NameHash = Engine_GameHash.Sdbm(sceneName),
					ParentOrdinal = -1,
					ObjectId = 1,
					Type = BgfElementType.Component,
					Flags = 5,
					ComponentPad = 3,
					ComponentExtra = 500,
				},
			},
		};
		Engine_BgfWriter.Recount(doc);
		doc.SlideState = doc.ComputeSlideState(null);
		return doc;
	}

	/// <summary>
	/// The scene graph a new presentation starts with: one root group node for the root component
	/// (parent -1, layer 255 and orientation 1 as every shipped root carries), version 2, the
	/// shipped constant header bytes (byte 1 = 4, the word at 4 = 24).
	/// </summary>
	public static Engine_BsgDocument NewSceneGraph(bool bigEndian)
	{
		var header = new byte[Engine_BsgDocument.SectionsStart];
		header[1] = 4;
		if (bigEndian)
		{
			header[7] = 24;
		}
		else
		{
			header[4] = 24;
		}

		BsgSceneNode root = GroupNode(-1, 1);
		root.LayerIndex = 255;
		root.Orientation = 1;
		return new Engine_BsgDocument
		{
			BigEndian = bigEndian,
			Version = 2,
			Header = header,
			Nodes = new[] { root },
		};
	}

	/// <summary>The sidecar a new presentation starts with: no records, CRLF, as shipped.</summary>
	public static Engine_FacDocument NewSidecar() => new();
}

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Ui;

/// <summary>
/// What the 2D pass draws, by viewport and CanvasLayer: each layer's visible canvas items with the
/// pixels they cover, their materials and screen-texture reads, and an estimate of the colour
/// traffic each layer's own render pass costs. Debug use (`{"op":"canvascensus"}`): it searches the
/// whole tree on the main thread.
/// </summary>
/// <remarks>
/// THE MODEL (Engine_CanvasLean): every layer with a visible item is one render pass over its whole
/// target, a load and a store at the target's bytes per pixel; a screen-texture read or a
/// BackBufferCopy adds a copy of the same size. The estimate is bytes, and milliseconds at the
/// ~21 GB/s the S10e's hdr_2d A/B implies (4.0 -> 2.1 ms for twice the bytes). "Visible" is the
/// node's flag: a visible container that draws nothing is listed but opens no pass in Godot.
/// </remarks>
public static class Engine_CanvasCensus
{
	/// <summary>Bytes per millisecond the estimate assumes.</summary>
	private const double BytesPerMs = 21e9 / 1000.0;

	/// <summary>The largest items listed per layer; the counts cover every one.</summary>
	private const int ItemsListed = 12;

	private sealed class Item
	{
		public required string Path;
		public required string Type;
		public double? Area;
		public double? Cover;
		public string? Material;
		public bool ScreenTexture;
		public Dictionary<string, object?>? Notes;
	}

	public static Dictionary<string, object?> Describe(SceneTree tree)
	{
		Window root = tree.Root;
		var byViewport = new Dictionary<Viewport, List<CanvasLayer>>();
		var order = new List<Viewport> { root };
		byViewport[root] = new List<CanvasLayer>();
		foreach (Node found in root.FindChildren("*", nameof(CanvasLayer), recursive: true, owned: false))
		{
			if (found is not CanvasLayer layer || !layer.IsInsideTree() || Into(layer) is not { } into) continue;
			if (!byViewport.TryGetValue(into, out List<CanvasLayer>? list))
			{
				byViewport[into] = list = new List<CanvasLayer>();
				order.Add(into);
			}

			list.Add(layer);
		}

		// Items in no layer draw on the viewport's own canvas, layer 0.
		var loose = new Dictionary<Viewport, List<CanvasItem>>();
		foreach (Node found in root.FindChildren("*", nameof(CanvasItem), recursive: true, owned: false))
		{
			if (found is not CanvasItem item || item.GetParent() is CanvasItem || !item.IsInsideTree() ||
				item.GetCanvasLayerNode() is not null || item.GetViewport() is not { } into)
			{
				continue;
			}

			if (!loose.TryGetValue(into, out List<CanvasItem>? list))
			{
				loose[into] = list = new List<CanvasItem>();
				if (!byViewport.ContainsKey(into))
				{
					byViewport[into] = new List<CanvasLayer>();
					order.Add(into);
				}
			}

			list.Add(item);
		}

		var viewports = new List<object?>();
		foreach (Viewport viewport in order)
		{
			viewports.Add(DescribeViewport(viewport, root, byViewport[viewport],
				loose.TryGetValue(viewport, out List<CanvasItem>? items) ? items : null));
		}

		return new Dictionary<string, object?>
		{
			["physicalSize"] = new { width = root.Size.X, height = root.Size.Y },
			["logicalSize"] = new { width = root.GetVisibleRect().Size.X, height = root.GetVisibleRect().Size.Y },
			["canvasLean"] = new Dictionary<string, object?>
			{
				["enabled"] = Engine_CanvasLean.Enabled,
				["sharedFrom"] = Engine_CanvasLean.SharedFrom,
				["sharedInto"] = Engine_CanvasLean.SharedInto,
				["blocker"] = Engine_CanvasLean.Blocker,
			},
			["model"] = "each layer with a visible item: one pass, load+store of the whole target; " +
				"+1 copy per screen-texture read; ms at 21 GB/s",
			["viewports"] = viewports,
		};
	}

	private static Viewport? Into(CanvasLayer layer) =>
		layer.CustomViewport is Viewport custom && GodotObject.IsInstanceValid(custom) ? custom : layer.GetViewport();

	private static Vector2I TargetSize(Viewport viewport) => viewport switch
	{
		Window window => window.Size,
		SubViewport sub => sub.Size,
		_ => Vector2I.Zero,
	};

	private static Dictionary<string, object?> DescribeViewport(Viewport viewport, Window root,
		List<CanvasLayer> layers, List<CanvasItem>? loose)
	{
		Vector2I size = TargetSize(viewport);
		int bytesPerPixel = viewport.UseHdr2D ? 8 : 4;
		long passBytes = Engine_CanvasLean.PassBytes(size.X, size.Y, bytesPerPixel);
		var visible = new List<Dictionary<string, object?>>();
		var hidden = new List<string>();
		int passes = 0, copies = 0;
		long textureBytes = 0;

		void Tally(Dictionary<string, object?> entry, bool pass, bool copy, long reads)
		{
			if (pass) passes++;
			if (copy) copies++;
			textureBytes += reads;
			visible.Add(entry);
		}

		if (loose is { Count: > 0 })
		{
			var items = new List<Item>();
			foreach (CanvasItem item in loose)
			{
				if (item.Visible) Collect(item, viewport, size, item.GetParent(), items);
			}

			if (items.Count > 0)
			{
				(Dictionary<string, object?> entry, bool copy, long reads) = Entry("(viewport canvas)", null, 0, items, true);
				Tally(entry, true, copy, reads);
			}
		}

		foreach (CanvasLayer layer in layers.OrderBy(l => l.Layer))
		{
			var items = new List<Item>();
			if (layer.Visible) CollectChildren(layer, viewport, size, layer, items);
			if (items.Count == 0)
			{
				hidden.Add($"{layer.Name}@{layer.Layer}{(layer.Visible ? "" : " (hidden)")}");
				continue;
			}

			bool shared = viewport == root && Engine_CanvasLean.SharedFrom is { } from && layer.Name == from;
			(Dictionary<string, object?> entry, bool copy, long reads) = Entry(layer.Name, layer.GetPath(), layer.Layer, items, !shared);
			if (shared) entry["sharedInto"] = Engine_CanvasLean.SharedInto;
			if (layer.Transform != Transform2D.Identity) entry["transformed"] = true;
			if (layer.FollowViewportEnabled) entry["followViewport"] = true;
			Tally(entry, !shared, copy, reads);
		}

		long bytes = (passes + copies) * passBytes + textureBytes;
		var reply = new Dictionary<string, object?>
		{
			["viewport"] = viewport == root ? "root" : viewport.GetPath().ToString(),
			["size"] = new { width = size.X, height = size.Y },
			["bytesPerPixel"] = bytesPerPixel,
			["passes"] = passes,
			["screenCopies"] = copies,
			["estimatedMb"] = Math.Round(bytes / 1e6, 1),
			["estimatedMs"] = Math.Round(bytes / BytesPerMs, 2),
			["layers"] = visible,
			["empty"] = hidden,
		};
		if (viewport is SubViewport sub) reply["updateMode"] = sub.RenderTargetUpdateMode.ToString();
		return reply;
	}

	private static (Dictionary<string, object?> Entry, bool Copy, long Reads) Entry(string name, NodePath? path, int index,
		List<Item> items, bool pass)
	{
		bool copy = items.Any(i => i.ScreenTexture);
		long reads = 0;
		foreach (Item item in items)
		{
			if (item.Notes is { } notes && notes.TryGetValue("textureBytes", out object? b) && b is long n) reads += n;
		}

		var listed = items.OrderByDescending(i => i.Area ?? double.MaxValue).Take(ItemsListed).Select(i =>
		{
			var row = new Dictionary<string, object?> { ["path"] = i.Path, ["type"] = i.Type };
			if (i.Area is { } area) row["areaPx"] = Math.Round(area);
			if (i.Cover is { } cover) row["cover"] = Math.Round(cover, 3);
			if (i.Material is not null) row["material"] = i.Material;
			if (i.ScreenTexture) row["screenTexture"] = true;
			if (i.Notes is not null) foreach ((string key, object? value) in i.Notes) row[key] = value;
			return row;
		}).ToList();

		var entry = new Dictionary<string, object?>
		{
			["name"] = name,
			["layer"] = index,
			["visibleItems"] = items.Count,
			["fullScreenItems"] = items.Count(i => i.Cover >= 0.9),
			["areaPx"] = Math.Round(items.Sum(i => i.Area ?? 0)),
			["withMaterial"] = items.Count(i => i.Material is not null),
			["pass"] = pass,
			["items"] = listed,
		};
		if (path is not null) entry["path"] = path.ToString();
		if (copy) entry["screenTexture"] = true;
		return (entry, copy, reads);
	}

	/// <summary>The layer's canvas items: its children and those under its plain nodes, not a sub-viewport's or a nested layer's.</summary>
	private static void CollectChildren(Node node, Viewport viewport, Vector2I size, Node relativeTo, List<Item> into)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			switch (node.GetChild(i))
			{
				case CanvasItem item:
					if (item.Visible) Collect(item, viewport, size, relativeTo, into);
					break;
				case Viewport or CanvasLayer:
					break;
				case Node plain:
					CollectChildren(plain, viewport, size, relativeTo, into);
					break;
			}
		}
	}

	private static void Collect(CanvasItem item, Viewport viewport, Vector2I size, Node relativeTo, List<Item> into)
	{
		var row = new Item
		{
			Path = relativeTo.IsAncestorOf(item) ? relativeTo.GetPathTo(item).ToString() : item.Name.ToString(),
			Type = item.GetClass(),
		};
		Transform2D toTarget = viewport.GetFinalTransform() * item.GetGlobalTransformWithCanvas();
		if (item is Control control)
		{
			Vector2 s = control.Size;
			row.Area = Math.Abs(toTarget.Determinant()) * s.X * s.Y;
			Rect2 onTarget = toTarget * new Rect2(Vector2.Zero, s);
			Rect2 target = new(Vector2.Zero, size);
			double targetArea = (double)size.X * size.Y;
			row.Cover = targetArea > 0 && onTarget.Intersects(target) ? onTarget.Intersection(target).Area / targetArea : 0;
		}

		switch (item.Material)
		{
			case ShaderMaterial shaderMaterial:
				string code = shaderMaterial.Shader?.Code ?? string.Empty;
				row.Material = shaderMaterial.Shader?.ResourcePath is { Length: > 0 } shaderPath ? shaderPath : "ShaderMaterial";
				row.ScreenTexture = code.Contains("hint_screen_texture", StringComparison.Ordinal) ||
					code.Contains("SCREEN_TEXTURE", StringComparison.Ordinal);
				break;
			case CanvasItemMaterial canvasMaterial:
				row.Material = $"CanvasItemMaterial {canvasMaterial.BlendMode}";
				break;
			case Material other:
				row.Material = other.GetClass();
				break;
		}

		var notes = new Dictionary<string, object?>();
		float alpha = item.Modulate.A * item.SelfModulate.A;
		if (alpha < 1f) notes["modulateAlpha"] = Math.Round(alpha, 4);
		switch (item)
		{
			case ColorRect rect:
				notes["colorAlpha"] = Math.Round(rect.Color.A, 4);
				break;
			case SubViewportContainer container:
				foreach (Node child in container.GetChildren())
				{
					if (child is not SubViewport sub) continue;
					notes["subViewport"] = new { width = sub.Size.X, height = sub.Size.Y, update = sub.RenderTargetUpdateMode.ToString(), transparent = sub.TransparentBg };
					notes["textureBytes"] = (long)sub.Size.X * sub.Size.Y * (sub.UseHdr2D ? 8 : 4);
				}

				break;
			case TextureRect textureRect when textureRect.Texture is { } texture:
				notes["texture"] = new { width = texture.GetWidth(), height = texture.GetHeight() };
				break;
			case BackBufferCopy backBuffer:
				row.ScreenTexture = backBuffer.CopyMode != BackBufferCopy.CopyModeEnum.Disabled;
				notes["copyMode"] = backBuffer.CopyMode.ToString();
				break;
			case CanvasGroup:
				row.ScreenTexture = true;
				notes["canvasGroup"] = true;
				break;
		}

		if (item.ClipChildren != CanvasItem.ClipChildrenMode.Disabled)
		{
			row.ScreenTexture = true;
			notes["clipChildren"] = item.ClipChildren.ToString();
		}

		if (notes.Count > 0) row.Notes = notes;
		into.Add(row);
		CollectChildren(item, viewport, size, relativeTo, into);
	}
}

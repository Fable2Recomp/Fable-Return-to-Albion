using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Refii.Ui;

/// <summary>
/// A HUD PIECE HELD VISIBLE (<c>hud_hold</c>, <see cref="Engine_UiHudOverrides.SetHold"/>): the
/// piece's root element is found by name in the HUD document (an authored name, or a path by its
/// trailing segments), and every slide owner in its subtree is kept on screen — an owner with an
/// <c>in</c> slide is forced onto it (the piece's own "shown" state), any other owner keeps the
/// slide the guest states, at least the first — and that slide's entries are released from the
/// guest's stale attributes, as the HUD's played slides are. The <c>in</c> slide's scale commands
/// win over the guest's live transform for the elements they drive (<see cref="Transforms"/>), so
/// a piece the guest tweened closed opens at its authored size.
/// </summary>
public sealed class Engine_UiHudHold : IReadOnlyList<float?[]?>
{
	private readonly Engine_UiScreen _screen;
	private readonly List<(int Owner, int ForcedSlide, BgfSlide[] Slides)> _owners = new();
	private readonly Dictionary<int, float?[]> _scaleRows = new();
	private IReadOnlyList<float?[]?>? _transforms;

	/// <summary>The pieces this hold covers, as stated.</summary>
	public IReadOnlyList<string> Pieces { get; }

	/// <summary>The document the hold was built against.</summary>
	public Engine_UiScreen Screen => _screen;

	/// <summary>How many slide owners the hold found; 0 means no piece of those names is in the document.</summary>
	public int OwnerCount => _owners.Count;

	public Engine_UiHudHold(Engine_UiScreen screen, IReadOnlyList<string> pieces)
	{
		_screen = screen;
		Pieces = pieces;
		var paths = Paths(screen);
		var roots = new List<UiNode>();
		foreach (string piece in pieces)
		{
			foreach ((string path, UiNode node) in paths)
			{
				if (!Engine_UiHudOverrides.MatchesPath(path, piece) && !string.Equals(node.Name, piece, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				// The shallowest match: an inner element of the same name is inside it already.
				bool inside = false;
				for (UiNode? up = node.Parent; up is not null; up = up.Parent)
				{
					if (roots.Contains(up))
					{
						inside = true;
						break;
					}
				}

				if (!inside && !roots.Contains(node))
				{
					roots.Add(node);
				}
			}
		}

		foreach (UiNode root in roots)
		{
			Walk(root);
		}
	}

	/// <summary>Every authored path in the document beside its node, as the HUD's own slot lookups build them.</summary>
	public static List<(string Path, UiNode Node)> Paths(Engine_UiScreen screen)
	{
		var paths = new List<(string, UiNode)>();
		foreach (UiNode node in screen.Nodes)
		{
			var parts = new List<string>();
			for (UiNode? walk = node; walk is not null; walk = walk.Parent)
			{
				if (walk.HasAuthoredName)
				{
					parts.Add(walk.Name);
				}
			}

			parts.Reverse();
			paths.Add((string.Join('.', parts), node));
		}

		return paths;
	}

	private void Walk(UiNode node)
	{
		AddOwner(node);
		foreach (UiNode child in node.Children)
		{
			Walk(child);
		}
	}

	private void AddOwner(UiNode node)
	{
		BgfSlide[] slides = _screen.Document.Slides.Where(s => s.OwnerElement == node.Ordinal && !s.IsMaster).ToArray();
		if (slides.Length == 0)
		{
			return;
		}

		int shown = Array.FindIndex(slides, s => string.Equals(s.Name, "in", StringComparison.OrdinalIgnoreCase)) + 1;
		_owners.Add((node.Ordinal, shown, slides));
		if (shown == 0)
		{
			return;
		}

		foreach (var entry in slides[shown - 1].Entries)
		{
			if (entry.Commands.Any(c => c.Id == Engine_BgfEdit.ScaleXId || c.Id == Engine_BgfEdit.ScaleYId))
			{
				_scaleRows[entry.ElementOrdinal] = new float?[7];
			}
		}
	}

	/// <summary>Folds the hold into one resolve pass: the owners' slides, with each slide's entries released.</summary>
	public void Apply(int[] slides, bool[] active, float?[] opacity)
	{
		foreach ((int owner, int forced, BgfSlide[] own) in _owners)
		{
			if (owner >= slides.Length)
			{
				continue;
			}

			int slide = forced > 0 ? forced : Math.Max(slides[owner], 1);
			if (slide > own.Length)
			{
				continue;
			}

			slides[owner] = slide;
			Engine_UiElementResolve.ReleaseSlideEntries(_screen.Document, own[slide - 1], active, opacity);
		}
	}

	/// <summary>The live transforms with the forced slides' scale rows nulled, so the slide's commands apply.</summary>
	public IReadOnlyList<float?[]?>? Transforms(IReadOnlyList<float?[]?>? source)
	{
		_transforms = source;
		return source is null || _scaleRows.Count == 0 ? source : this;
	}

	public int Count => _transforms?.Count ?? 0;

	public float?[]? this[int index]
	{
		get
		{
			float?[]? source = _transforms![index];
			if (source is null || !_scaleRows.TryGetValue(index, out float?[]? row))
			{
				return source;
			}

			Array.Clear(row);
			Array.Copy(source, row, Math.Min(source.Length, row.Length));
			row[3] = row[4] = row[5] = null;
			return row;
		}
	}

	public IEnumerator<float?[]?> GetEnumerator()
	{
		for (int i = 0; i < Count; i++)
		{
			yield return this[i];
		}
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

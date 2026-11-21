namespace Refii.Titles.Fable2;

using System;
using System.Collections.Generic;
using System.Globalization;
using Refii.Core;
using Refii.Ui;

/// <summary>
/// The expression wheel's items as the guest STATES them: SetItem (TU1 `sub_826AC5F0`, disc
/// `sub_8258FD30`; hooks/gui_headless_presentation.cpp) with the ring's element path (r4,
/// "wheelmenu.icons.icons.menu.item" or "...submenu.item"), the item slot (r7), the title (the
/// guest's lookup of the r6 tag) and the texture (the hash its r5 name stores), posted as
/// `@ExpressionWheel.Item`. Keyed by ring and slot; the HUD document places slot N where the guest
/// resolves it: `Layer.&lt;path&gt;&lt;N&gt;` (`GuiElement_Resolve_p` TU1 0x824265A0 / disc 0x823F8A38, a
/// dotted path under the root element's child `Layer`), its `Title` child for the text and its `expressions` child for the
/// texture (0x826ac6e4 / 0x826ac69c), each step the parent's first child of that name hash.
/// </summary>
public sealed class Engine_UiWheelItems
{
	/// <summary>One stated item: ring path, slot, title text, texture hash, title tag.</summary>
	public readonly record struct Item(string Path, int Slot, string Title, uint Texture, string Tag);

	private readonly Dictionary<(string Path, int Slot), Item> _items = new();
	private readonly Action<string> _log;

	private Engine_BgfDocument? _document;
	private List<int>[] _children = Array.Empty<List<int>>();
	private readonly Dictionary<(string Path, int Slot), (int Title, int Icon)> _ordinals = new();
	private readonly HashSet<(string Path, int Slot)> _unresolved = new();
	private string?[]? _texts;

	public Engine_UiWheelItems(Action<string> log) => _log = log;

	/// <summary>Bumped by every statement.</summary>
	public int Generation { get; private set; }

	/// <summary>The items stated, each ring's latest per slot.</summary>
	public IReadOnlyCollection<Item> Items => _items.Values;

	/// <summary>`@ExpressionWheel.Item`: path, slot, title, texture hash (hex), tag.</summary>
	public void State(Engine_UiModel.GuiNativeCall call)
	{
		string path = call.Argument(0).Text ?? string.Empty;
		if (path.Length == 0 ||
			!int.TryParse(call.Argument(1).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot) ||
			!uint.TryParse(call.Argument(3).Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint texture))
		{
			return;
		}

		_items[(path, slot)] = new Item(path, slot, call.Argument(2).Text ?? string.Empty, texture,
			call.Argument(4).Text ?? string.Empty);
		++Generation;
	}

	/// <summary><paramref name="told"/> with each stated item's title on its `Title` element.</summary>
	public string?[] Texts(Engine_BgfDocument document, string?[] told)
	{
		if (_items.Count == 0)
		{
			return told;
		}

		if (_texts is null || _texts.Length != told.Length)
		{
			_texts = new string?[told.Length];
		}

		Array.Copy(told, _texts, told.Length);
		foreach (Item item in _items.Values)
		{
			int title = OrdinalsOf(document, item).Title;
			if (title >= 0 && title < _texts.Length)
			{
				_texts[title] = item.Title;
			}
		}

		return _texts;
	}

	/// <summary>Puts each stated item's texture on its `expressions` element of <paramref name="textures"/>.</summary>
	public void Textures(Engine_BgfDocument document, uint[] textures)
	{
		foreach (Item item in _items.Values)
		{
			int icon = OrdinalsOf(document, item).Icon;
			if (icon >= 0 && icon < textures.Length)
			{
				textures[icon] = item.Texture;
			}
		}
	}

	/// <summary>The `Title` and `expressions` ordinals of an item, -1 for one the file lacks.</summary>
	public (int Title, int Icon) OrdinalsOf(Engine_BgfDocument document, Item item)
	{
		if (!ReferenceEquals(document, _document))
		{
			_document = document;
			_ordinals.Clear();
			_unresolved.Clear();
			_children = new List<int>[document.Elements.Length];
			for (int i = 0; i < _children.Length; ++i)
			{
				_children[i] = new List<int>();
			}

			for (int i = 0; i < document.Elements.Length; ++i)
			{
				int parent = document.Elements[i].ParentOrdinal;
				if (parent >= 0 && parent < _children.Length)
				{
					_children[parent].Add(i);
				}
			}
		}

		var key = (item.Path, item.Slot);
		if (_ordinals.TryGetValue(key, out (int Title, int Icon) found))
		{
			return found;
		}

		int at = document.Elements.Length == 0 ? -1 : Resolve(document, 0, "Layer");
		foreach (string step in (item.Path + item.Slot.ToString(CultureInfo.InvariantCulture)).Split('.'))
		{
			at = at < 0 ? -1 : Resolve(document, at, step);
		}

		found = at < 0 ? (-1, -1) : (Resolve(document, at, "Title"), Resolve(document, at, "expressions"));
		_ordinals[key] = found;
		if ((found.Title < 0 || found.Icon < 0) && _unresolved.Add(key))
		{
			_log($"[wheel-items] {item.Path}{item.Slot} resolves to Title #{found.Title}, expressions " +
				$"#{found.Icon} in the HUD file; a missing one is not drawn");
		}

		return found;
	}

	/// <summary>
	/// The child of <paramref name="parent"/> named <paramref name="name"/>: its first, as
	/// child-by-name (element service vf +84) walks. `Layer` is the root element's child
	/// (`GuiElement_Resolve_p` 0x824265d0..0x824265f0).
	/// </summary>
	private int Resolve(Engine_BgfDocument document, int parent, string name)
	{
		uint hash = Engine_GameHash.Sdbm(name);
		foreach (int child in _children[parent])
		{
			if (document.Elements[child].NameHash == hash)
			{
				return child;
			}
		}

		return -1;
	}
}

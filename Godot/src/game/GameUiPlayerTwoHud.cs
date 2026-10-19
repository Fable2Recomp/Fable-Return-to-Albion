using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// PLAYER TWO'S TARGETED READOUT AND D-PAD on split screen. The guest's HUD holds one of each, for
/// the local hero; player two's half shows theirs from the same reads and the same builders, called
/// for hero two (host_bridge.cpp RefiiHostGetPlayerTwoTargetedQuery, guest_tick_pump.cpp
/// SamplePlayerTwoHud), stated over the shared feed the way player one's publisher states it
/// (<see cref="Engine_UiHudTargetedQuery"/>).
/// </summary>
public partial class GameUi
{
	/// <summary>Player two's targeted-readout slides over the shared ones (ordinal → slide).</summary>
	private readonly System.Collections.Generic.Dictionary<int, int> _hudTwoQuerySlides = new();

	private static readonly Engine_HudValues.Handle TargetedTitle = Engine_HudValues.Handle.For("TargetedQueryTitle");
	private static readonly Engine_HudValues.Handle TargetedGiftMeter = Engine_HudValues.Handle.For("TargetedGift");
	private static readonly Engine_HudValues.Handle TargetedOpinionMeter = Engine_HudValues.Handle.For("TargetedQueryOpinion");

	private static readonly string[] TargetedToggles =
	{
		"TargetedQuery", "TargetedGift", "TargetedQueryButton", "TargetedDivorce", "TargetedMarry",
	};

	/// <summary>
	/// The d-pad's four slot elements in the guest's slot order — up, left, down, right
	/// (`sub_823D34B8`: 0x83497C84, 0x83497C8C, 0x83497C88, 0x83497C90, named at static init).
	/// </summary>
	private static readonly string[] DpadSlotPaths =
	{
		"dpad.dpad.dpadmenu.group.up.up", "dpad.dpad.dpadmenu.group.left.left",
		"dpad.dpad.dpadmenu.group.down.down", "dpad.dpad.dpadmenu.group.right.right",
	};

	private int[][]? _dpadSlotOrdinals;
	private Engine_UiScreen? _dpadSlotScreen;
	private string _twoTitle = string.Empty;
	private float _twoGift;
	private bool _twoHudStated;

	private void DrivePlayerTwoHud()
	{
		var overlay = _hudTwo?.Values;
		bool split = Refii.Core.Engine_Views.Split && CoopSession.LocalCoop && overlay?.Under is not null &&
			_hud?.Bindings is not null;
		HostBridge.PlayerTwoHud? hud = split ? HostBridge.GetPlayerTwoHud() : null;
		if (hud is not { } stated || _hud?.Bindings is not { } bindings || overlay is null)
		{
			if (_twoHudStated)
			{
				_twoHudStated = false;
				_hudTwoQuerySlides.Clear();
				ClearDpadTwo();
				overlay?.Override(TargetedTitle, null);
				overlay?.OverrideMeter(TargetedGiftMeter, null);
				overlay?.OverrideMeter(TargetedOpinionMeter, null);
			}

			return;
		}

		_twoHudStated = true;
		HostBridge.TargetedQuery query = HostBridge.GetPlayerTwoTargetedQuery();
		bool up = query.Known && query.Present;

		// The toggles and the fader as `Engine_UiHudTargetedQuery` states them for player one: a
		// toggle folds to 2 on and 1 off, `TargetedQuery`'s SlideFader2 to 2 in and 3 out, the slide
		// set verbatim.
		void Toggle(string name, bool on)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideToggle, Refii.Core.Engine_GameHash.Fnv1(name)))
			{
				_hudTwoQuerySlides[ordinal] = on ? 2 : 1;
			}
		}

		foreach (string name in TargetedToggles)
		{
			Toggle(name, name switch
			{
				"TargetedQuery" => up,
				"TargetedGift" => up && query.Gift,
				"TargetedQueryButton" => up && query.Button,
				_ => false,
			});
		}

		uint queryHash = Refii.Core.Engine_GameHash.Fnv1("TargetedQuery");
		foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideFader2, queryHash))
		{
			_hudTwoQuerySlides[ordinal] = up ? 2 : 3;
		}

		if (up && query.Entity != 0u && query.SlideSet > 0)
		{
			foreach (int ordinal in bindings.Elements(Engine_UiBindings.Kind.SlideSet, queryHash))
			{
				_hudTwoQuerySlides[ordinal] = query.SlideSet;
			}
		}

		// The title and meters stay as last stated while the readout fades out, as the guest's do.
		if (up && stated.Target != 0u)
		{
			_twoTitle = stated.Title;
			if (query.Gift)
			{
				_twoGift = query.GiftValue * 100f;
			}

			overlay.OverrideMeter(TargetedOpinionMeter, query.Opinion * 100f);
		}

		overlay.Override(TargetedTitle, _twoTitle);
		overlay.OverrideMeter(TargetedGiftMeter, _twoGift);

		DriveDpadTwo(stated.Icons);
	}

	/// <summary>Player two's d-pad icons onto the four slot elements of their half.</summary>
	private void DriveDpadTwo(string[] icons)
	{
		int[][] slots = DpadSlotOrdinals();
		Refii.Textures.Engine_TextureNameTable? names = _presentation?.TextureNames;
		for (int i = 0; i < slots.Length && i < icons.Length; ++i)
		{
			uint texture = 0u;
			if (icons[i].Length != 0)
			{
				names?.Add(icons[i]);
				texture = Refii.Core.Engine_GameHash.ResourcePath(icons[i].ToLowerInvariant());
			}

			foreach (int ordinal in slots[i])
			{
				_hudTwoTextures[ordinal] = texture;
			}
		}
	}

	private void ClearDpadTwo()
	{
		foreach (int[] slot in _dpadSlotOrdinals ?? System.Array.Empty<int[]>())
		{
			foreach (int ordinal in slot)
			{
				_hudTwoTextures.Remove(ordinal);
			}
		}
	}

	/// <summary>
	/// The quads each d-pad slot element paints: the named element when it carries a quad, else the
	/// leaf quads beneath it. Matched by trailing path segments, as the owned books match keys
	/// (the presentation's own root name may not be in the document's path).
	/// </summary>
	private int[][] DpadSlotOrdinals()
	{
		if (_dpadSlotOrdinals is not null && ReferenceEquals(_dpadSlotScreen, _hudScreen))
		{
			return _dpadSlotOrdinals;
		}

		_dpadSlotScreen = _hudScreen;
		_dpadSlotOrdinals = new int[DpadSlotPaths.Length][];
		var paths = new System.Collections.Generic.List<(string Path, UiNode Node)>();
		foreach (UiNode node in _hudScreen?.Nodes ?? System.Array.Empty<UiNode>())
		{
			var parts = new System.Collections.Generic.List<string>();
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

		for (int i = 0; i < DpadSlotPaths.Length; ++i)
		{
			UiNode? found = null;
			string key = DpadSlotPaths[i];
			while (found is null && key.Length != 0)
			{
				foreach ((string path, UiNode node) in paths)
				{
					if (string.Equals(path, key, System.StringComparison.OrdinalIgnoreCase) ||
						path.EndsWith("." + key, System.StringComparison.OrdinalIgnoreCase))
					{
						found = node;
						break;
					}
				}

				int dot = key.IndexOf('.');
				key = found is null && dot >= 0 && key.IndexOf('.', dot + 1) >= 0 ? key[(dot + 1)..] : string.Empty;
			}

			var ordinals = new System.Collections.Generic.List<int>();
			if (found?.Quad is not null)
			{
				ordinals.Add(found.Ordinal);
			}
			else if (found is not null)
			{
				CollectLeafQuads(found, ordinals);
			}

			_dpadSlotOrdinals[i] = ordinals.ToArray();
			GD.Print($"[hud-two] d-pad slot {DpadSlotPaths[i]} → {(ordinals.Count == 0 ? "not found" : string.Join(",", ordinals))}");
		}

		return _dpadSlotOrdinals;
	}

	private static void CollectLeafQuads(UiNode node, System.Collections.Generic.List<int> into)
	{
		foreach (UiNode child in node.Children)
		{
			if (child.Children.Count == 0 && child.Quad is not null)
			{
				into.Add(child.Ordinal);
			}
			else
			{
				CollectLeafQuads(child, into);
			}
		}
	}
}

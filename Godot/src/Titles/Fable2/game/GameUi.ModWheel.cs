using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Mods;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// The controller's mod wheel (<see cref="ModWheel"/>) on the game's expression wheel: the guest's own
/// wheel opens, with its art, animation and stick cursor, and the eight wedges it fills with expression
/// groups show the mods' entries instead. Only what is drawn changes, in copies of the guest's stated
/// text and textures, so the wheel is the game's own again the moment it closes.
/// </summary>
public partial class GameUi
{
	/// <summary>Frames the guest may take to open the wheel before a refused open is given up on.</summary>
	private const int ModWheelOpenGrace = 30;

	private int _modWheelWaiting;
	private int _modWheelWaitingTwo;
	private string?[]? _modWheelTexts;
	private Engine_UiScreen? _modWheelDoc;
	private (int Title, int[] Icons)[]? _modWheelItems;
	private uint _modWheelBlank;
	private bool _modWheelBlanked;

	/// <summary>Whether the HUD's textures must be stated afresh for the wheel: while it stands, and once after.</summary>
	private bool ModWheelIconsStale => ModWheel.OpenPlayer != 0 || _modWheelBlanked;

	/// <summary>
	/// Local player two's buttons on their own mods' wheel, as player one's are below: A chooses for
	/// player two, B closes, LB and RB turn the page. The cursor is theirs (<c>_wheelSectorTwo</c>).
	/// </summary>
	private void FollowModWheelButtonsTwo()
	{
		_modWheelWaitingTwo = GuestExpressionWheelOpen() ? 0 : _modWheelWaitingTwo + 1;
		if (_modWheelWaitingTwo > ModWheelOpenGrace)
		{
			_modWheelWaitingTwo = 0;
			ModWheel.Close(2);
			return;
		}

		if (Fable2Input.WheelTwoAcceptPressed)
		{
			ModWheel.Choose(2, _wheelSectorTwo);
		}
		else if (Fable2Input.WheelTwoBackPressed)
		{
			ModWheel.Close(2);
		}
		else if (Fable2Input.WheelTwoNextPressed)
		{
			ModWheel.StepPage(2, 1);
		}
		else if (Fable2Input.WheelTwoPrevPressed)
		{
			ModWheel.StepPage(2, -1);
		}
	}

	/// <summary>Photo mode's picture on the wheel: our own art (tools/mod_wheel_icons.py), nothing of the game's.</summary>
	public const string PhotoModeWheelIcon = "res://assets/ui/wheel/photo_mode.png";

	/// <summary>
	/// Photo mode stands on the top wedge of every page, so a controller always reaches it from Back.
	/// Choosing it asks photo mode to open on its next frame; it is a controller's only way in
	/// (<see cref="GamePhotoMode.PadComboOpens"/>), and a refusal (co-op, a cutscene, a menu) is photo
	/// mode's own (<see cref="GamePhotoMode.OpenRefusal"/>).
	/// </summary>
	private static void PutPhotoModeOnModWheel() =>
		ModWheel.SetTop(new ModWheel.Fixed("Photo Mode", PhotoModeWheelIcon, GamePhotoMode.RequestOpen));

	/// <summary>The buttons of an open mod wheel. The wheel's own select never runs: nothing here is an expression.</summary>
	private void FollowModWheelButtons()
	{
		// The guest refuses a wheel for a busy hero, and closes one for its own reasons.
		_modWheelWaiting = GuestExpressionWheelOpen() ? 0 : _modWheelWaiting + 1;
		if (_modWheelWaiting > ModWheelOpenGrace)
		{
			_modWheelWaiting = 0;
			ModWheel.Close();
			return;
		}

		if (Fable2Input.MenuAcceptPressed)
		{
			ModWheel.Choose(_wheelSector);
		}
		else if (Fable2Input.MenuBackPressed)
		{
			ModWheel.Close();
		}
		else if (Fable2Input.MenuTabNextPressed)
		{
			ModWheel.StepPage(1);
		}
		else if (Fable2Input.MenuTabPrevPressed)
		{
			ModWheel.StepPage(-1);
		}
	}

	/// <summary>
	/// The guest's own open flag, as the pump's QuickWheel lever reads it: the quick menu
	/// `*(GUI+28)`, its byte +35 (+34 unpatched, `GuiOwnerField`). The HUD's drawn quads are no
	/// answer — the closed wheel's art stays drawn under the guest's element state.
	/// </summary>
	private static bool GuestExpressionWheelOpen()
	{
		System.Span<uint> word = stackalloc uint[1];
		uint at = HostBridge.GuestAddress(0x83496B08u);
		if (at == 0u || HostBridge.Peek(at, word) != 1 || word[0] < 0x10000u)
		{
			return false;
		}

		if (HostBridge.Peek(word[0] + 28u, word) != 1 || word[0] < 0x10000u)
		{
			return false;
		}

		uint field = HostBridge.PickByBuild(35u, 34u);
		if (HostBridge.Peek(word[0] + 32u, word) != 1)
		{
			return false;
		}

		// Guest words are big-endian: byte +32 is the word's top byte, +35 its bottom.
		return ((word[0] >> (int)((35u - field) * 8u)) & 0xFFu) != 0u;
	}

	/// <summary>The HUD's element text, with the mods' entries on the wedges while the mod wheel stands.</summary>
	private string?[] ModWheelTexts(Engine_UiScreen doc, string?[] told)
	{
		int player = ModWheel.OpenPlayer;
		if (player == 0 || ModWheelItems(doc) is not { Length: > 0 } items)
		{
			return told;
		}

		if (_modWheelTexts is null || _modWheelTexts.Length != told.Length)
		{
			_modWheelTexts = new string?[told.Length];
		}

		System.Array.Copy(told, _modWheelTexts, told.Length);
		List<ModInput.Action> entries = ModWheel.Entries();
		for (int sector = 0; sector < items.Length && sector < ModWheel.CloseSector; ++sector)
		{
			if (ModWheel.Top is { } top && sector == ModWheel.TopSector)
			{
				_modWheelTexts[items[sector].Title] = ModText.Game(top.Label);
				continue;
			}

			int entry = ModWheel.EntryAt(ModWheel.PageOf(player), sector, entries.Count, ModWheel.TopTaken);
			_modWheelTexts[items[sector].Title] = entry >= 0 ? ModText.Game(entries[entry].Wheel) : string.Empty;
		}

		return _modWheelTexts;
	}

	/// <summary>The HUD's textures, with the expression groups' icons off the wedges while the mod wheel stands.</summary>
	private uint[] ModWheelIcons(Engine_UiScreen doc, uint[] textures)
	{
		int player = ModWheel.OpenPlayer;
		_modWheelBlanked = player != 0;
		if (player == 0 || ModWheelItems(doc) is not { Length: > 0 } items)
		{
			return textures;
		}

		if (_modWheelBlank == 0u)
		{
			var clear = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
			_modWheelBlank = Engine_UiHostTextures.Register(ImageTexture.CreateFromImage(clear));
		}

		List<ModInput.Action> entries = ModWheel.Entries();
		for (int sector = 0; sector < items.Length && sector < ModWheel.CloseSector; ++sector)
		{
			uint shown;
			if (ModWheel.Top is { } top && sector == ModWheel.TopSector)
			{
				shown = ModWheelIcon(top.Icon);
			}
			else
			{
				int entry = ModWheel.EntryAt(ModWheel.PageOf(player), sector, entries.Count, ModWheel.TopTaken);
				shown = entry >= 0 ? ModWheelIcon(entries[entry].WheelIcon) : _modWheelBlank;
			}

			foreach (int icon in items[sector].Icons)
			{
				if (icon < textures.Length && textures[icon] != 0u)
				{
					textures[icon] = shown;
				}
			}
		}

		return textures;
	}

	private readonly Dictionary<string, uint> _modWheelIconKeys = new(System.StringComparer.Ordinal);

	/// <summary>The stated texture for an entry's picture; the blank one for none, or one that does not load.</summary>
	private uint ModWheelIcon(string path)
	{
		if (path.Length == 0)
		{
			return _modWheelBlank;
		}

		if (!_modWheelIconKeys.TryGetValue(path, out uint key))
		{
			key = Engine_HudModPieces.LoadAnyTexture(path) is { } texture ? Engine_UiHostTextures.Register(texture) : _modWheelBlank;
			_modWheelIconKeys[path] = key;
		}

		return key;
	}

	/// <summary>
	/// The wheel's eight wedges, in the guest's sector order: the first eight text elements under
	/// `wheelmenu` are their titles, and each wedge's quads are the elements its title stands among.
	/// </summary>
	private (int Title, int[] Icons)[]? ModWheelItems(Engine_UiScreen doc)
	{
		if (ReferenceEquals(_modWheelDoc, doc))
		{
			return _modWheelItems;
		}

		_modWheelDoc = doc;
		_modWheelItems = null;
		UiNode? wheel = null;
		foreach (UiNode node in doc.Nodes)
		{
			if (node.Name == Fable2HudLayout.ExpressionWheelName)
			{
				wheel = node;
				break;
			}
		}

		if (wheel is null)
		{
			GD.PushWarning("[mod-wheel] hud has no wheelmenu; the wheel keeps the game's entries");
			return null;
		}

		var titles = new List<UiNode>();
		CollectTitles(wheel, titles);
		if (titles.Count < ModWheel.CloseSector + 1)
		{
			GD.PushWarning($"[mod-wheel] wheelmenu has {titles.Count} text element(s), not the eight wedges");
			return null;
		}

		titles.RemoveRange(ModWheel.CloseSector + 1, titles.Count - (ModWheel.CloseSector + 1));
		var items = new (int, int[])[titles.Count];
		for (int i = 0; i < titles.Count; ++i)
		{
			UiNode wedge = titles[i];
			while (wedge.Parent is { } parent && !ReferenceEquals(parent, wheel) && TitlesUnder(parent, titles) == 1)
			{
				wedge = parent;
			}

			var quads = new List<int>();
			CollectQuads(wedge, quads);
			items[i] = (titles[i].Ordinal, quads.ToArray());
		}

		_modWheelItems = items;
		return items;
	}

	private static void CollectTitles(UiNode node, List<UiNode> into)
	{
		foreach (UiNode child in node.Children)
		{
			if (child.Text is not null)
			{
				into.Add(child);
			}

			CollectTitles(child, into);
		}
	}

	private static int TitlesUnder(UiNode node, List<UiNode> titles)
	{
		int count = titles.Contains(node) ? 1 : 0;
		foreach (UiNode child in node.Children)
		{
			count += TitlesUnder(child, titles);
		}

		return count;
	}

	private static void CollectQuads(UiNode node, List<int> into)
	{
		if (node.Quad is not null)
		{
			into.Add(node.Ordinal);
		}

		foreach (UiNode child in node.Children)
		{
			CollectQuads(child, into);
		}
	}
}

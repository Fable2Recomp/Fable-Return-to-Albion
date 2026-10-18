using System.Collections.Generic;

using Refii.Ui;

namespace Refii.Host;

/// <summary>
/// <see cref="Engine_UiSource"/> answered by the live guest through <see cref="HostBridge"/>.
/// Pure pass-through: no caching, no gating.
/// </summary>
public sealed class HostUiSource : Engine_UiSource
{
	/// <summary>Makes the live guest the UI's source for the rest of the session.</summary>
	public static void Install() => Current = new HostUiSource();

	public override bool IsLive => true;

	public override List<Engine_UiModel.Presentation> GetPresentations() =>
		HostBridge.GetPresentations();

	public override List<Engine_UiModel.UiElement>? GetUiElements(uint presentation = 0) =>
		HostBridge.GetUiElements(presentation);

	public override uint GetUiElementCount(uint presentation = 0) =>
		HostBridge.GetUiElementCount(presentation);

	public override uint[]? GetUiTextureHashes(uint presentation, int elementCount) =>
		HostBridge.GetUiTextureHashes(presentation, elementCount);

	public override Engine_UiModel.LiveAttribute[,]? GetUiAttributes(
		uint[] ids, uint presentation = 0) => HostBridge.GetUiAttributes(ids, presentation);

	public override Engine_UiModel.ScreenFade GetScreenFade() => HostBridge.GetScreenFade();

	public override Engine_UiModel.InGameScreens? GetInGameScreens() =>
		HostBridge.GetInGameScreens();

	public override List<Engine_UiModel.MenuRow> GetMenuRows(out ulong generation) =>
		HostBridge.GetMenuRows(out generation);

	public override uint MenuScreenId() => HostBridge.GetMenuScreenId();

	public override Engine_UiModel.MenuBoxState? GetMenuBox() => HostBridge.GetMenuBox();

	public override bool DisplayQueryUp() =>
		HostBridge.GetDisplayBox() is { Vtable: HostBridge.DisplayQueryBoxVtable };

	public override bool DisplayAdjusterUp() =>
		HostBridge.GetDisplayBox() is { Vtable: HostBridge.DisplayAdjusterVtable };

	/// <summary>
	/// The guest's own mapping: the class's type (vtable slot 7, `li r3,N`) through
	/// `sub_823D0B90` — 0 → 3, 2 → 19, 3 → 4, 4 → 5, 5 → 6, 6..8 → 7.
	/// </summary>
	/// <summary>
	/// `F = *(*(*(0x83496AC0)+8)+0x50)` (0x8230FF44..0x8230FF64, gated on `byte_83496AC4`); the
	/// main-menu root `*(F+0xB8)` (vt 0x820D0934) and the title `*(F+0xB4)` (vt 0x820D09C4).
	/// Either sets `+57` when it raises the dialog (`sub_826E1C50`, `sub_826E3580`); `+20` is its
	/// presentation; the root takes the answer in mode `+24 == 6` (set on DIALOG_ACTIVE,
	/// `sub_826E1040`), the title in mode 12; `+206` is the root's cancel flag.
	/// </summary>
	private static readonly (uint Offset, uint Vtable, uint Accepting)[] DialogOwners =
		{ (0xB8u, 0x820D0934u, 6u), (0xB4u, 0x820D09C4u, 12u) };

	public override FrontEndDialogState? GetFrontEndDialog()
	{
		// The pointer and its gate byte are read SEPARATELY. In TU1 the gate (`byte_83496AC4`)
		// follows the pointer; without the title update it sits three bytes BEFORE it
		// (`byte_83496925`, pointer at 0x83496928), so one two-word read is only right in TU1.
		uint[] app = HostBridge.Peek(HostBridge.GuestAddress(0x83496AC0u), 1);
		uint[] gate = HostBridge.Peek(HostBridge.GuestAddress(0x83496AC4u), 1);
		if (app.Length != 1 || gate.Length != 1 || app[0] < 0x10000u || (gate[0] >> 24) == 0u)
		{
			return null;
		}

		uint[] shell = HostBridge.Peek(app[0] + 8u, 1);
		uint[] front = shell.Length == 1 && shell[0] >= 0x10000u
			? HostBridge.Peek(shell[0] + 0x50u, 1)
			: System.Array.Empty<uint>();
		if (front.Length != 1 || front[0] < 0x10000u)
		{
			return null;
		}

		foreach ((uint offset, uint vtable, uint accepting) in DialogOwners)
		{
			uint[] owner = HostBridge.Peek(front[0] + offset, 1);
			if (owner.Length != 1 || owner[0] < 0x10000u)
			{
				continue;
			}

			// +0 vtable, +20 presentation, +24 mode, +56..+59 holds the +57 flag, +204..+207
			// the +206 cancel flag.
			uint[] words = HostBridge.Peek(owner[0], 52);
			if (words.Length != 52 || HostBridge.GuestAddressToTu1(words[0]) != vtable || ((words[14] >> 16) & 0xFFu) == 0u)
			{
				continue;
			}

			// The root's cancel flag is at +206 in TU1 and +194 without the title update: every
			// root field from +72 up sits 12 bytes lower there (`lbz` 206 against 194 at the same
			// place in the two builds' root action handlers).
			uint cancelWord = HostBridge.PickByBuild(words[51], words[48]);
			bool cancel = vtable == 0x820D0934u && ((cancelWord >> 8) & 0xFFu) != 0u;
			return new FrontEndDialogState(owner[0], words[5], words[6] == accepting, cancel);
		}

		return null;
	}

	public override uint DisplayBoxScreen() => HostBridge.GetDisplayBox()?.Vtable switch
	{
		0x820AE668u => 3u,   // MessageBox
		0x820AE688u => 19u,  // QueryBox
		0x820AE6A8u => 4u,   // NumericAdjuster
		0x820AE6C8u => 5u,   // QuestBoxParams — questrepeatable
		0x820EBFA8u => 6u,   // Intermission
		0x820EBF3Cu or 0x820028F4u or 0x820EBF1Cu => 7u,  // Acceptance, Button, InfoParams
		_ => uint.MaxValue,
	};

	public override Engine_UiModel.PauseState? GetPauseState() => HostBridge.GetPauseState();

	public override List<Engine_UiModel.MapMarker> GetMapMarkers() => HostBridge.GetMapMarkers();

	public override Engine_UiModel.SpellAssignState GetSpellAssign() => HostBridge.GetSpellAssign();

	public override Engine_UiModel.SpellWheelState GetSpellWheel(int player) => HostBridge.GetSpellWheel(player);

	public override Engine_UiModel.SpellWheelState GetSpellBlade() => HostBridge.GetSpellBlade();

	public override Dictionary<uint, float> GetHudMeters() => HostBridge.GetHudMeters();

	public override Dictionary<uint, float> GetHudPulsers() => HostBridge.GetHudPulsers();

	public override Dictionary<uint, string> GetHudTexts() => HostBridge.GetHudTexts();

	public override Dictionary<uint, uint> GetHudScriptTexts() => HostBridge.GetHudScriptTexts();

	public override Dictionary<uint, float> GetHudScriptMeters() =>
		HostBridge.GetHudScriptMeters();

	public override Engine_UiModel.SaveState GetSaveState() => HostBridge.GetSaveState();

	public override List<Engine_UiModel.Language> GetLanguages() => HostBridge.GetLanguages();

	public override Engine_UiModel.AchievementUnlock[] GetAchievementUnlocks(
		ulong afterSequence, int max = 16) =>
		HostBridge.GetAchievementUnlocks(afterSequence, max);

	public override byte[]? GetTitleXdbf() => HostBridge.GetTitleXdbf();

	public override void SetAchievementUnlocks(uint[] ids, ulong[] unlockFiletimes) =>
		HostBridge.SetAchievementUnlocks(ids, unlockFiletimes);

	public override uint GetAchievementsUiRequests() => HostBridge.GetAchievementsUiRequests();

	public override List<Engine_UiModel.GuiNativeCall> TakeGuiNativeCalls() =>
		HostBridge.TakeGuiNativeCalls();

	public override (ulong Intercepted, uint Pending, uint Named) GuiNativeCounters() =>
		HostBridge.GuiNativeCounters();

	public override List<Engine_UiModel.MenuChunk> TakeMenuChunks() =>
		HostBridge.TakeMenuChunks();

	public override List<Engine_UiModel.UiElementChange> TakeUiElementChanges() =>
		HostBridge.TakeUiElementChanges();

	public override (ulong Recorded, uint Pending, ulong Dropped) UiElementCounters() =>
		HostBridge.UiElementCounters();

	public override bool MenuPopulating() => HostBridge.MenuPopulating();

	public override void SetPredicate(string name, bool value) =>
		HostBridge.SetGuiPredicate(name, value);

	public override void SetPredicate(string name, float value) =>
		HostBridge.SetGuiPredicate(name, value);

	public override void SuppressNative(string name) => HostBridge.SuppressGuiNative(name);

	public override uint PostFrontEndAction(string name) => HostBridge.PostFrontEndAction(name);

	public override uint ScreenAction(uint code, bool atRoot = false) =>
		HostBridge.ScreenAction(code, atRoot);

	public override uint MenuEvent(string name, int eventType, uint id) =>
		HostBridge.MenuEvent(name, eventType, id);

	public override uint MenuDiscard(string name, int eventType, uint id) =>
		HostBridge.MenuDiscard(name, eventType, id);

	public override uint MenuSelect() => HostBridge.MenuSelect();

	public override uint MenuScroll(Engine_UiModel.MenuStick direction, bool held) =>
		HostBridge.MenuScroll(direction, held);

	public override bool PauseTabSelect(uint tab) => HostBridge.PauseTabSelect(tab);

	public override uint AssignSpell(int slot, int spell, int player = 1) => HostBridge.AssignSpell(slot, spell, player);
}

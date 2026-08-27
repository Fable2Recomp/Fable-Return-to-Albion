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

	public override Engine_UiModel.MenuBoxState? GetMenuBox() => HostBridge.GetMenuBox();

	public override Engine_UiModel.PauseState? GetPauseState() => HostBridge.GetPauseState();

	public override List<Engine_UiModel.MapMarker> GetMapMarkers() => HostBridge.GetMapMarkers();

	public override Engine_UiModel.SpellAssignState GetSpellAssign() => HostBridge.GetSpellAssign();

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

	public override Engine_UiModel.AchievementDefinition? GetAchievementDefinition(
		uint achievementId) => HostBridge.GetAchievementDefinition(achievementId);

	public override byte[]? GetAchievementIconPng(uint achievementId) =>
		HostBridge.GetAchievementIconPng(achievementId);

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

	public override uint MenuSelect() => HostBridge.MenuSelect();

	public override uint MenuScroll(Engine_UiModel.MenuStick direction, bool held) =>
		HostBridge.MenuScroll(direction, held);

	public override bool PauseTabSelect(uint tab) => HostBridge.PauseTabSelect(tab);

	public override uint AssignSpell(int slot, int spell) => HostBridge.AssignSpell(slot, spell);
}

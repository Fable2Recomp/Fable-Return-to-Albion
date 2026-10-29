using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Refii.Titles.Fable2;

using Refii.Save;

public static class Engine_NewGameScript
{
    public static string Build(NewGameSelection selection, Engine_SaveHero.Hero? hero, Func<uint, string?> recordName, bool live = false,
        Func<uint, IReadOnlyList<Engine_SaveHero.Augmentation>>? initialAugments = null,
        Func<Engine_SaveHero.Augmentation, string?>? augmentationName = null)
    {
        if (selection.Start == NewGameStart.Childhood) return string.Empty;
        if (selection.Posters is null) throw new InvalidOperationException("The wanted-poster choice is required.");
        var apply = new StringBuilder();
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (selection.Start == NewGameStart.NewGamePlus)
        {
            if (hero is null || !hero.CompletedQuests.Contains("QC240_Endgame"))
                throw new InvalidOperationException("The source save has not completed the main story.");
            if (hero.HasInventoryInstanceData && hero.InventoryInstances.Count == 0 ||
                hero.InventoryInstances.Any(p => p.Key < 0 || p.Key >= hero.Inventory.Count || p.Value.Unsupported))
                throw new InvalidOperationException("This save contains an unsupported item customization. Your save has not been changed.");
            if (hero.Inventory.Count > 10000 || hero.Inventory.Any(i => i.Count <= 0 || i.Count > 100000) ||
                hero.Inventory.Sum(i => (long)i.Count) > 100000)
                throw new InvalidOperationException("The saved inventory is not valid for carryover.");
            apply.AppendLine("local h = GetPlayerHero()");
            var ordinary = new Dictionary<uint, int>();
            var customized = new StringBuilder();
            for (int index = 0; index < hero.Inventory.Count; index++)
            {
                var item = hero.Inventory[index];
                string name = recordName(item.Record) ?? string.Empty;
                if (string.IsNullOrEmpty(name))
                    throw new InvalidOperationException($"The installed game cannot resolve inventory item {item.Record:X8}. Install the source save's DLC and mods first.");
                string quoted = Quote(name);
                required.Add(name);
                hero.InventoryInstances.TryGetValue(index, out var instance);
                var extra = new List<string>();
                if (instance?.Augmentations is { } augments)
                {
                    var initial = initialAugments?.Invoke(item.Record) ?? throw new InvalidOperationException("The installed augmentation definitions are unavailable.");
                    if (augments.Count > 32 || initial.Count > augments.Count || !initial.SequenceEqual(augments.Take(initial.Count)))
                        throw new InvalidOperationException($"Item {item.Record:X8} has augmentations that differ from the installed game definitions.");
                    foreach (var augment in augments.Skip(initial.Count))
                    {
                        string augmentItem = augmentationName?.Invoke(augment) ?? throw new InvalidOperationException($"The installed game is missing augmentation {augment.Primary:X8}/{augment.Secondary:X8}.");
                        extra.Add(augmentItem);
                        required.Add(augmentItem);
                    }
                }
                if (extra.Count == 0 && instance?.Dyes is null)
                {
                    ordinary[item.Record] = ordinary.GetValueOrDefault(item.Record) + item.Count;
                    continue;
                }
                customized.AppendLine($"for i = 1, {item.Count} do");
                if (extra.Count == 0) customized.AppendLine($"local item = Inventory.AddItemOfType(h, {quoted})");
                else
                {
                    customized.AppendLine($"local entity = Debug.CreateEntityAtPosition({quoted}, \"RefiiCarryover\", h:GetPosition())");
                    customized.AppendLine("assert(entity and entity:IsAlive() and Augmentable.IsAvailable(entity), \"Cannot create the saved augmented item\")");
                    foreach (string augment in extra) customized.AppendLine($"Augmentable.AddAugmentationOfType(entity, {Quote(augment)})");
                    customized.AppendLine("local item = Inventory.AddEntity(h, entity, true, true)");
                }
                if (instance?.Dyes is { } dyes)
                {
                    if (dyes.Length != 2) throw new InvalidOperationException("The saved item dye data is invalid.");
                    for (int slot = 0; slot < dyes.Length; slot++)
                    {
                        var dye = dyes[slot];
                        if (dye.Red == 0 && dye.Green == 0 && dye.Blue == 0 && dye.Brightness == 0) continue;
                        customized.AppendLine($"AppearanceModifierManager.SetClothingItemDyeColour(h, item, {(slot == 0 ? "true" : "false")}, {dye.Red}, {dye.Green}, {dye.Blue}, {Number(dye.Brightness)})");
                    }
                }
                customized.AppendLine("end");
            }
            foreach (var item in ordinary)
                apply.AppendLine($"do local n = Inventory.GetNumberOfItemsOfType(h, {Quote(recordName(item.Key)!)}) for i = n + 1, {item.Value} do Inventory.AddItemOfType(h, {Quote(recordName(item.Key)!)}) end end");
            apply.Append(customized);
            int gold = Math.Max(0, hero.Money);
            apply.AppendLine($"Money.Set(h, {gold & ~0xFFFFFF})");
            apply.AppendLine($"Money.Modify(h, {gold & 0xFFFFFF})");
            for (int i = 1; i < hero.AbilityLevels.Length; i++)
            {
                int level = hero.AbilityLevels[i];
                if (level < 0 || level > 5) throw new InvalidOperationException("The saved ability levels are invalid.");
                apply.AppendLine($"Stats.SetHeroAbilityLevel(h, {i}, {level})");
            }
            for (int i = 0; i < 4; i++)
            {
                float xp = hero.Experience[i], total = hero.TotalExperience[i];
                if (!float.IsFinite(xp) || !float.IsFinite(total) || xp < 0 || total < xp)
                    throw new InvalidOperationException("The saved experience values are invalid.");
                apply.AppendLine($"do local t = {Number(total)} - Experience.GetTotalEarned(h, {i}) if t > 0 then Experience.Modify(h, {i}, t, false) end Experience.Modify(h, {i}, {Number(xp)} - Experience.Get(h, {i}), false) end");
            }
            apply.AppendLine($"Stats.ModifyMorality(h, {Number(hero.Morality)} - Stats.GetMorality(h))");
            apply.AppendLine($"Stats.ModifyPurity(h, {Number(hero.Purity)} - Stats.GetPurity(h))");
            apply.AppendLine($"Stats.ModifyRenown(h, {Math.Max(0, hero.Renown)} - Stats.GetRenown(h))");
            apply.AppendLine($"Stats.ModifyFat(h, {Number(hero.Fat)} - Stats.GetFat(h))");
            foreach (uint expression in hero.UnlockedExpressions.Distinct())
            {
                string name = recordName(expression) ?? string.Empty;
                if (name.Length == 0) throw new InvalidOperationException("A saved expression needs unavailable game content.");
                required.Add(name);
                apply.AppendLine($"ExpressionPerformer.SetExpressionAsAvailable(h, {Quote(name)}, true)");
            }
            foreach (var spell in hero.AssignedSpells)
            {
                if (spell.Direction is < 0 or > 1 || spell.Slot is < 0 or > 4 || spell.Spell is < -1 or > 7)
                    throw new InvalidOperationException("The saved spell assignments are invalid.");
                if (spell.Spell >= 0)
                    apply.AppendLine($"SpellManager.SetEntitySpellTableItem(h, {spell.Direction}, {spell.Slot}, {spell.Spell})");
            }
            foreach (string title in hero.UnlockedTitles.Distinct())
                apply.AppendLine($"Stats.AddUnlockedTitle(h, {Quote(title)})");
            foreach (string title in hero.FreeTitles.Distinct())
                apply.AppendLine($"Stats.SetTitleAsFreeToBuy(h, {Quote(title)})");
            if (hero.Title != 0)
            {
                string name = recordName(hero.Title) ?? string.Empty;
                if (name.Length == 0) throw new InvalidOperationException("The saved hero title needs unavailable game content.");
                required.Add(name);
                apply.AppendLine($"Stats.SetTitleRecord(h, {Quote(name)})");
            }
        }
        string evil = selection.Posters == WantedPosterRecipient.Arfur ? "true" : "false";
        string prepare = live ? $$"""
            local childhood = Gameflow.Childhood
            assert(childhood and not childhood.Terminated and Gameflow.PositionInGameflow == ScriptEnum.DebugQC060,
                "Golden Cock is only available during the original childhood quest")
            assert(not RefiiNewGameStarted, "The adulthood transition has already started")
            assert(Gameflow.ChildhoodVars and childhood.ClearScriptRules and childhood.Terminate,
                "Unsupported childhood quest")
            assert(Gameflow.GameflowMode, "The childhood transition requires the story gameflow")
            local male = Gameflow.ChildhoodVars.Male
            childhood:ClearLookAtCamera()
            childhood:SetDefaultCamera()
            childhood:ClearScriptRules()
            childhood.MissionSucceeded = true
            childhood:Terminate()
            if childhood.StopTimeKey then Timing.SetTimeAsStopped(false, childhood.StopTimeKey) end
            Gameflow.ChildhoodVars.Male = male
            Player.ChangePlayerEntityType(GetPlayerHero(), male and "CreatureHero" or "CreatureHeroFemale")
            QuestManager.HeroEntity = GetPlayerHero()
            local h = QuestManager.HeroEntity
            Inventory.AddInitialItems(h)
            AppearanceModifierManager.AddInitialItems(h)
            Gameflow.ChildhoodVars.SetTargetableAllBeds(true)
            Player.SetFastTravelStopActivationAsEnabled(h, true)
            ExperienceOrb.SetAsEnabled(true)
            Player.SetExperienceSpendingAsEnabled(h, true)
            Player.SetMapScreenAsEnabled(h, true)
            Player.SetPauseMenuInfoPageAsEnabled(h, true)
            AmbientPopulationManager.SetCrimeEnabled(true)
            Vaulting.SetAsEnabled(h, true)
            Player.SetSafetyModeSuggestable(h, true)
            Health.SetAsInvulnerable(h, false)
            GUI.SetDPadEnabled(true)
            SoundTools.SetMusicAndAtmosToStartOnNextLevelLoad(false)
            QuestTracker.SetAsCompleted(h, "QC010_Childhood", true)
            if QuestManager.DogEntity == nil then
                ScriptFunction.CreateDog()
                QuestManager.DogEntity = GetDog()
            end
            Layers.ActivateLayer({{(selection.Posters == WantedPosterRecipient.Arfur ? "\"QC010_RoseDiaryLayer_Slums\"" : "\"QC010_RoseDiaryLayer_Posh\"")}})
            """ : string.Empty;
        return $$"""
            do
                if {{(live ? "true" : "not IsLoadedFromSaveGame() and not RefiiNewGameStarted")}} then
                    assert(Gameflow and Gameflow.SetGameflowPosition and ScriptEnum.DebugQC060, "Refii: unsupported gameflow")
                    {{string.Join("\n", required.Select(name => $"assert(GDB.RecordExists({Quote(name)}), {Quote("Missing carryover record: " + name)})"))}}
                    {{prepare}}
                    RefiiNewGameStarted = true
                    Gameflow.ChildhoodResolutionEvil = {{evil}}
                    {{(live ? "Gameflow.SkipToPositionInGameflow = ScriptEnum.DebugQC060\n        Gameflow.SkipToNextPositionInGameflow = false\n        Gameflow.SkippingThroughGameflowFlag = false" : "Gameflow:SetGameflowPosition(ScriptEnum.DebugQC060)")}}
                    {{(live ? """
                    local activateScenario = Layers.ActivateScenario
                    local pendingScenario
                    Layers.ActivateScenario = function(world, level, scenario, ...)
                        if world == "Albion" and level == "BWSSlums" and IsLevelLoaded("BWSSlums")
                            and (scenario == "Chapter2Slums" or scenario == "Chapter2Posh") then
                            pendingScenario = {world, level, scenario, ...}
                            return
                        end
                        return activateScenario(world, level, scenario, ...)
                    end
                    """ : string.Empty)}}
                    local save = AutoSaveGame
                    local load = LoadScriptSave
                    LoadScriptSave = function(...)
                        AutoSaveGame = save
                        LoadScriptSave = load
                        {{(live ? "Layers.ActivateScenario = activateScenario" : string.Empty)}}
                        return load(...)
                    end
                    AutoSaveGame = function(...)
                        if Gameflow.NewBeginning and IsLevelLoaded("BowerLake") then
                            AutoSaveGame = save
                            LoadScriptSave = load
                            {{(live ? "Layers.ActivateScenario = activateScenario\n                if pendingScenario then activateScenario(unpack(pendingScenario)) end" : string.Empty)}}
                            {{apply}}
                            Gameflow.RefiiNewGamePlus = {{(hero is not null ? "true" : "false")}}
                            print("[refii-new-game] adulthood prepared; posters: {{selection.Posters}}")
                        end
                        return save(...)
                    end
                end
            end
            """;
    }

    private static string Number(float value)
    {
        if (!float.IsFinite(value)) throw new InvalidOperationException("The save contains invalid character stats.");
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Quote(string value) => "\"" + string.Concat(Encoding.UTF8.GetBytes(value)
        .Select(b => b is >= 32 and <= 126 && b != 34 && b != 92 ? ((char)b).ToString() : "\\" + b.ToString("D3"))) + "\"";
}

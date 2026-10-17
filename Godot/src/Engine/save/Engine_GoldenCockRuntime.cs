using System;
using System.Linq;
using System.Text;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Gdb;
using Refii.Mods;

namespace Refii.Save;

public static class Engine_GoldenCockRuntime
{
    public const string EntityName = "TheGoldenCock";
    public const string ActionName = "OnActionUseGoldenCock";
    public static readonly uint Record = Engine_GameHash.Fnv1(EntityName);
    public const uint TemplateRecord = 0x3E58634F;

    public static byte[] BuildDatabase(byte[] source, byte[]? update = null)
    {
        var multi = GdbMulti.Build(new[] { source, update });
        if (!multi.TryLookup(TemplateRecord, out var template))
            throw new InvalidOperationException("The installed game has no supported readable-object template.");
        var patch = new GdbPatchDatabase();
        GdbPatchEntry Copy(GdbCursor from, string name)
        {
            uint hash = Engine_GameHash.Fnv1(name);
            var entry = new GdbPatchEntry(hash, from.View.RecordSchemaFlags(from.Record));
            foreach (var field in multi.InheritedFields(from))
            {
                entry.Properties.Add(new GdbPatchProperty(field.Hash, field.Type, field.Raw));
                if (field.Type == GdbFieldType.String && multi.NameOf(field.Raw) is { Length: > 0 } text)
                    patch.Names[field.Raw] = text;
            }
            patch.Entries.Add(entry);
            patch.Names[hash] = name;
            patch.Aliases.Add((hash, hash));
            return entry;
        }
        void Set(GdbPatchEntry entry, string name, GdbFieldType type, uint value)
        {
            uint hash = Engine_GameHash.Fnv1(name);
            entry.Properties.RemoveAll(p => p.NameHash == hash);
            entry.Properties.Add(new GdbPatchProperty(hash, type, value));
            patch.Names[hash] = name;
        }
        var entity = Copy(template, EntityName);
        if (!multi.TryLookup(Engine_GameHash.Fnv1("PhysicsSimpleComponent"), out var placement))
            throw new InvalidOperationException("The installed game has no simple placement component.");
        var physics = Copy(placement, EntityName + "PhysicsSimpleComponent");
        entity.Properties.RemoveAll(p => p.NameHash == Engine_GameHash.Fnv1("PhysicsSimulationStaticComponent"));
        Set(entity, "PhysicsSimpleComponent", GdbFieldType.Reference, physics.Id);
        GdbPatchEntry Component(string name)
        {
            uint hash = Engine_GameHash.Fnv1(name);
            var field = entity.Properties.Single(p => p.NameHash == hash);
            if (!multi.TryLookup(field.Value, out var original))
                throw new InvalidOperationException("The Golden Cock template is missing " + name);
            var copy = Copy(original, EntityName + name);
            field.Value = copy.Id;
            return copy;
        }
        var action = Component("OnActionUseComponent");
        uint script = Engine_GameHash.Fnv1(ActionName);
        patch.Names[script] = ActionName;
        Set(action, "ScriptToRun", GdbFieldType.String, script);
        Set(action, "DestroyAfterUse", GdbFieldType.Bool, 0);
        Set(action, "HighlightWhenTargeted", GdbFieldType.Bool, 1);
        var target = Component("TargetedComponent");
        Set(target, "InteractionDistance", GdbFieldType.Float, BitConverter.SingleToUInt32Bits(2.75f));
        Set(target, "TargetRadius", GdbFieldType.Float, BitConverter.SingleToUInt32Bits(0.8f));
        Set(target, "TargetableByDefault", GdbFieldType.Bool, 1);
        return GdbPatchBuilder.ApplyOverlayToBaseGdb(update ?? GdbWriter.Serialize(new GdbPatchDatabase()), GdbWriter.Serialize(patch));
    }

    public static string BuildScript(Engine_GoldenCockPlacement placement)
    {
        if (!placement.Enabled || !placement.Pose.IsFinite()) return string.Empty;
        string level = placement.LevelName;
        if (level.Length == 0 || level.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new InvalidOperationException("The Golden Cock level name is invalid.");
        Vector3 p = placement.Pose.Origin;
        string position = FormattableString.Invariant($"CVector3({p.X:R},{p.Z:R},{p.Y:R})");
        return $$"""
            if not RefiiGoldenCockInstalled then
                RefiiGoldenCockInstalled = true
                rawset(_G, "RefiiGoldenCockInteraction", 0)
                OnActionUseGoldenCock = OnActionUseBase:new()
                function OnActionUseGoldenCock:Update()
                    if self.ItemBeingUsed:IsAlive() and self.ItemBeingUsed:GetName() == "TheGoldenCock"
                        and self.EntityUsingItem == GetPlayerHero() and Gameflow and Gameflow.Childhood
                        and not Gameflow.Childhood.Terminated and not RefiiNewGameStarted then
                        RefiiGoldenCockInteraction = (RefiiGoldenCockInteraction + 1) % 1000000
                    end
                end
                local update = QuestManager.Update
                local entity
                local frame = -1
                local failed = false
                local function tick()
                    local now = Timing.GetWorldFrame()
                    if now ~= frame then
                        frame = now
                        if IsLevelLoaded("{{level}}") and Gameflow and Gameflow.Childhood and not Gameflow.Childhood.Terminated
                            and Gameflow.ChildhoodVars and not RefiiNewGameStarted then
                            local hero = GetPlayerHero()
                            if not hero or not hero:IsAlive() then return end
                            if not entity or not entity:IsAlive() then
                                local search = SearchTools.StartNewSearch("object")
                                SearchTools.FilterWithName(search, "TheGoldenCock")
                                entity = SearchTools.GetSearchResults(search)[1]
                                if not entity then
                                    entity = Debug.CreateEntityAtPosition("TheGoldenCock", "TheGoldenCock", {{position}})
                                end
                                if entity and entity:IsAlive() then
                                    entity:SetPosition({{position}})
                                    Targeted.SetAsTargetable(entity, true)
                                end
                            end
                        elseif entity then
                            if entity:IsAlive() then entity:Destroy() end
                            entity = nil
                        end
                    end
                end
                QuestManager.Update = function(...)
                    if not failed then
                        local ok, err = pcall(tick)
                        if not ok then
                            failed = true
                            print("[refii-golden-cock] " .. tostring(err))
                        end
                    end
                    return update(...)
                end
                SetQuestUpdateFunction(QuestManager.Update)
                if PlutoPermanentsSaveTable then
                    AddToPermanentsTables(QuestManager.Update, "RefiiGoldenCockQuestUpdate")
                    AddFunctionsInTableToPermanentsTables(OnActionUseGoldenCock, "RefiiGoldenCockUse")
                end
            end
            """;
    }

    public static void Attach(BnkFileSystem mounted, Engine_GoldenCockPlacement? placement)
    {
        if (placement is not { Enabled: true }) return;
        const string path = "globals/globals.gdb";
        if (!mounted.TryGetFile(path, out var original))
            throw new InvalidOperationException("The Golden Cock could not find the installed globals database.");
        byte[]? update = mounted.TryGetFile(path + ".tu", out var tu) ? tu.ReadAllBytes() : null;
        byte[] database = BuildDatabase(original.ReadAllBytes(), update);
        string script = BuildScript(placement);
        if (Refii.Lua.Engine_LuaDecompiler.Available &&
            !Refii.Lua.Engine_LuaDecompiler.TryCheckSource(script, "@refii/golden-cock", out string error))
            throw new InvalidOperationException(error);
        string previous = mounted.TryGetFile(ModLua.InitKey, out var init) ? Encoding.UTF8.GetString(init.ReadAllBytes()) : string.Empty;
        byte[] bootstrap = Encoding.UTF8.GetBytes(previous + "\n" + script);
        mounted.AddVirtualFile(path + ".tu", path + ".tu", database.Length, () => database, "Refii");
        mounted.AddVirtualFile(ModLua.InitKey, ModLua.InitKey, bootstrap.Length, () => bootstrap, "Refii");
    }
}

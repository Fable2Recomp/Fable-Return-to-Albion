using System;
using System.Linq;
using System.Text;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Gdb;
using Refii.Mods;
using Refii.Models;
using Refii.Save;

namespace Refii.Hero;

public static class Engine_MorphMirrorRuntime
{
    public const string EntityName = "RefiiMorphMirror";
    public const string ActionName = "OnActionUseRefiiMorphMirror";
    public const string ActiveKey = "refii.morph_mirror.active";
    public const string RotationKey = "refii.morph_mirror.rotation";
    public const string ModelPath = "art/refii/morph_mirror/refii_morph_mirror.mdl";
    public const string TargetMarker = "Prop.Focal.CharacterLookat";
    public const float InteractionDistance = 4f;
    public const float TargetRadius = 1.2f;
    public static readonly uint Record = Engine_GameHash.Fnv1(EntityName);

    public static string BuildScript(Engine_MorphMirrorPlacement placement)
    {
        if (!placement.Enabled) return string.Empty;
        if (!placement.Pose.IsFinite() || !placement.HeroPose.IsFinite())
            throw new InvalidOperationException("The MorphMirror placement is invalid.");
        string level = placement.LevelPath.Length > 0 ? placement.LevelPath : placement.LevelName;
        return ScriptFor(level, Vector(placement.Pose.Origin), Vector(placement.HeroPose.Origin),
            Vector(-placement.HeroPose.Basis.Z.Normalized()), Vector(-placement.Pose.Basis.Z.Normalized()),
            placement.Pose.Basis.Scale.Abs().Y);
    }

    private static string Vector(Vector3 v) => FormattableString.Invariant($"CVector3({v.X:R},{v.Z:R},{v.Y:R})");

    public static string ScriptFor(string level, string position, string stand, string facing,
        string mirrorFacing = "CVector3(0,1,0)", float mirrorScale = 1f)
    {
        level = level.Replace('/', '\\');
        if (level.Split('\\').Any(part => part.Length == 0 || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            throw new InvalidOperationException("The MorphMirror level path is invalid.");
        level = level.Replace("\\", "\\\\");
        if (!float.IsFinite(mirrorScale) || mirrorScale <= 0)
            throw new InvalidOperationException("The MorphMirror scale is invalid.");
        string scale = FormattableString.Invariant($"{mirrorScale:R}");
        return $$"""
        if not RefiiMorphMirror then
            RefiiMorphMirror = {}
            local mirror
            local hero
            local originalPosition
            local originalFacing
            local idleAction
            local defaultFacing = {{facing}}
            local previewFacing = defaultFacing
            local rotation = 0
            local touched = 0
            local frame = -1
            local retry = 0
            local entered = false
            local active = false
            Refii.publish("{{ActiveKey}}", 0)
            function RefiiMorphMirror.End()
                if hero and hero:IsAlive() then
                    if idleAction and Action.IsActionPluggedIn(hero, idleAction) then Action.FinishAllActions(hero) end
                    if IsLevelLoaded("{{level}}") then
                        if originalPosition then hero:SetPosition(originalPosition) end
                        if originalFacing then Physics.SetFacingVector(hero, originalFacing) end
                    end
                end
                hero = nil
                originalPosition = nil
                originalFacing = nil
                idleAction = nil
                active = false
                Refii.publish("{{RotationKey}}", 0)
                Refii.publish("{{ActiveKey}}", 0)
            end
            function RefiiMorphMirror.Touch()
                touched = Timing.GetWorldFrame()
            end
            function RefiiMorphMirror.HoldIdle()
                if idleAction and Action.IsActionPluggedIn(hero, idleAction) then return true end
                idleAction = Action.SetCurrentAction(hero, {
                    Type = EScriptableAction.LOOP,
                    Priority = EActionPriority.PRIORITY_INTERACTION,
                    CanBeOverridenBySamePriority = false,
                    OverrideLooking = true,
                    LoopAction = { Type = EScriptableAction.PLAY_ANIMATION, Anim = "Idle", HasMovement = false, OverrideLooking = true },
                    NumLoops = 0
                })
                return idleAction and Action.IsActionPluggedIn(hero, idleAction)
            end
            OnActionUseRefiiMorphMirror = OnActionUseBase:new()
            function OnActionUseRefiiMorphMirror:Update()
                if active or not self.ItemBeingUsed:IsAlive() or self.ItemBeingUsed:GetName() ~= "{{EntityName}}"
                    or self.EntityUsingItem ~= GetPlayerHero() or not IsLevelLoaded("{{level}}") then return end
                hero = GetPlayerHero()
                if not hero or not hero:IsAlive() then return end
                originalPosition = hero:GetPosition()
                originalFacing = Physics.GetFacingVector(hero)
                rotation = 0
                previewFacing = defaultFacing
                Refii.publish("{{RotationKey}}", 0)
                hero:SetPosition({{stand}})
                Physics.SetFacingVector(hero, previewFacing)
                if not RefiiMorphMirror.HoldIdle() then RefiiMorphMirror.End(); return end
                active = true
                RefiiMorphMirror.Touch()
                Refii.publish("{{ActiveKey}}", 1)
            end
            local update = QuestManager.Update
            local failed = false
            local function tick()
                local now = Timing.GetWorldFrame()
                if now == frame then return end
                frame = now
                if not IsLevelLoaded("{{level}}") then
                    if active then RefiiMorphMirror.End() end
                    mirror = nil
                    retry = 0
                    entered = false
                    return
                end
                if not entered then
                    entered = true
                    retry = now + 30
                end
                if not mirror or not mirror:IsAlive() then
                    local player = GetPlayerHero()
                    if not player or not player:IsAlive() or now < retry then return end
                    retry = now + 30
                    local search = SearchTools.StartNewSearch("object")
                    SearchTools.FilterWithName(search, "{{EntityName}}")
                    mirror = SearchTools.GetSearchResults(search)[1]
                    if not mirror or not mirror:IsAlive() then
                        mirror = Debug.CreateEntityAtPosition("{{EntityName}}", "{{EntityName}}", {{position}})
                    end
                    if mirror and mirror:IsAlive() then
                        mirror:SetPosition({{position}})
                        Physics.SetFacingVector(mirror, {{mirrorFacing}})
                        GraphicAppearance.SetScale(mirror, {{scale}})
                        Targeted.SetAsTargetable(mirror, true)
                    end
                end
                if active then
                    if not hero or not hero:IsAlive() or now < touched or now - touched > 150 then
                        RefiiMorphMirror.End()
                    else
                        if not RefiiMorphMirror.HoldIdle() then RefiiMorphMirror.End(); return end
                        local yaw = Refii.value("{{RotationKey}}") or 0
                        if type(yaw) ~= "number" or yaw ~= yaw or yaw == math.huge or yaw == -math.huge then yaw = 0 end
                        if yaw ~= rotation then
                            rotation = yaw
                            local c, s = math.cos(yaw), math.sin(yaw)
                            local x, y = defaultFacing:GetX(), defaultFacing:GetY()
                            previewFacing = CVector3(x * c - y * s, x * s + y * c, 0)
                        end
                        hero:SetPosition({{stand}})
                        Physics.SetFacingVector(hero, previewFacing)
                    end
                end
            end
            QuestManager.Update = function(...)
                if not failed then
                    local ok, err = pcall(tick)
                    if not ok then
                        failed = true
                        pcall(RefiiMorphMirror.End)
                        Refii.publish("{{ActiveKey}}", 0)
                        print("[refii-morph-mirror] " .. tostring(err))
                    end
                end
                return update(...)
            end
            SetQuestUpdateFunction(QuestManager.Update)
            if PlutoPermanentsSaveTable then
                AddToPermanentsTables(QuestManager.Update, "RefiiMorphMirrorQuestUpdate")
                AddFunctionsInTableToPermanentsTables(OnActionUseRefiiMorphMirror, "RefiiMorphMirrorUse")
                AddFunctionsInTableToPermanentsTables(RefiiMorphMirror, "RefiiMorphMirrorFunctions")
            end
        end
        """;
    }

    public static byte[] BuildDatabase(byte[] source, byte[]? update, string model, Transform3D pose)
    {
        if (!pose.IsFinite() || Math.Abs(pose.Basis.Determinant()) < 0.000001f)
            throw new InvalidOperationException("The MorphMirror placement is invalid.");
        byte[] database = Engine_GoldenCockRuntime.BuildDatabase(source, update,
            EntityName, ActionName, InteractionDistance, TargetRadius, InteractionDistance, model);
        var patch = GdbPatchBuilder.LoadOrCreatePatchDatabase(database);
        var physics = patch.Entries.Single(entry => entry.Id == Engine_GameHash.Fnv1(EntityName + "PhysicsSimpleComponent"));
        GdbTransformMath.ExtractGdbTransform(pose, out float x, out float y, out float z,
            out float rx, out float ry, out float rz, out float scale);
        void Set(GdbPatchEntry entry, string name, GdbFieldType type, uint value)
        {
            uint hash = Engine_GameHash.Fnv1(name);
            entry.Properties.RemoveAll(property => property.NameHash == hash);
            entry.Properties.Add(new GdbPatchProperty(hash, type, value));
            patch.Names[hash] = name;
        }
        void VectorRecord(string field, float a, float b, float c)
        {
            string name = EntityName + field;
            uint hash = Engine_GameHash.Fnv1(name);
            var vector = new GdbPatchEntry(hash, 1);
            Set(vector, "X", GdbFieldType.Float, BitConverter.SingleToUInt32Bits(a));
            Set(vector, "Y", GdbFieldType.Float, BitConverter.SingleToUInt32Bits(b));
            Set(vector, "Z", GdbFieldType.Float, BitConverter.SingleToUInt32Bits(c));
            patch.Entries.RemoveAll(entry => entry.Id == hash);
            patch.Entries.Add(vector);
            patch.Names[hash] = name;
            Set(physics, field, GdbFieldType.Reference, hash);
        }
        VectorRecord("Position", x, y, z);
        VectorRecord("Rotation", rx, ry, rz);
        Set(physics, "Scale", GdbFieldType.Float, BitConverter.SingleToUInt32Bits(scale));
        return GdbWriter.Serialize(patch);
    }

    public static byte[] BuildLevelSave(byte[] source)
    {
        var patch = new SaveMapPatch();
        patch.UpsertEntity(EntityName, Record, EntityName);
        patch.GetOrCreateLayer(EntityName).LoadOverride = GdbSaveLoadState.AlwaysOn;
        return SaveMapCompositor.ApplyPatchesToSaveBytes(source, new[] { patch });
    }

    public static byte[] BuildModelMetadata(byte[] source)
    {
        var metadata = Engine_GmdSkeleton.TryParse(source)
            ?? throw new InvalidOperationException("MorphMirror could not read its installed model metadata.");
        var attachments = metadata.Attachments.Where(attachment =>
            !attachment.Name.Equals(TargetMarker, StringComparison.OrdinalIgnoreCase)).ToList();
        attachments.Add(new GmdAttachment(TargetMarker,
            new Transform3D(Basis.Identity, new Vector3(0, 0.85f, -0.5f)), -1));
        return Engine_GmdWriter.Write(metadata, attachments);
    }

    public static void AttachModel(BnkFileSystem mounted, string model)
    {
        if (!mounted.TryGetFile(model, out var original) || !mounted.TryGetFile(model + ".gmd", out var sidecar))
            throw new InvalidOperationException("MorphMirror could not find its installed model and metadata.");
        byte[] geometry = original.ReadAllBytes();
        byte[] metadata = BuildModelMetadata(sidecar.ReadAllBytes());
        mounted.AddVirtualFile(ModelPath, ModelPath, geometry.Length, () => geometry, "Refii");
        mounted.AddVirtualFile(ModelPath + ".gmd", ModelPath + ".gmd", metadata.Length, () => metadata, "Refii");
    }

    public static void Attach(BnkFileSystem mounted, Engine_MorphMirrorPlacement? placement)
    {
        if (placement is not { Enabled: true }) return;
        const string path = "globals/globals.gdb";
        if (!mounted.TryGetFile(path, out var original)) throw new InvalidOperationException("MorphMirror could not find globals.gdb.");
        string model = placement.Models.FirstOrDefault(Engine_MorphMirrorPlacements.IsMirror)
            ?? throw new InvalidOperationException("MorphMirror has no installed mirror model.");
        AttachModel(mounted, model);
        byte[]? update = mounted.TryGetFile(path + ".tu", out var tu) ? tu.ReadAllBytes() : null;
        byte[] database = BuildDatabase(original.ReadAllBytes(), update, ModelPath, placement.Pose);
        string scenario = placement.Scenario.ToLowerInvariant();
        string levelSave = $"worlds/{placement.World}/{placement.LevelPath}/{scenario}/{scenario}.save".Replace('\\', '/').ToLowerInvariant();
        if (!mounted.TryGetFile(levelSave, out var originalSave))
            throw new InvalidOperationException("MorphMirror could not find its installed level placement list.");
        byte[] save = BuildLevelSave(originalSave.ReadAllBytes());
        string script = BuildScript(placement);
        if (Refii.Lua.Engine_LuaDecompiler.Available &&
            !Refii.Lua.Engine_LuaDecompiler.TryCheckSource(script, "@refii/morph-mirror", out string error))
            throw new InvalidOperationException(error);
        string previous = mounted.TryGetFile(ModLua.InitKey, out var init) ? Encoding.UTF8.GetString(init.ReadAllBytes()) : string.Empty;
        byte[] bootstrap = Encoding.UTF8.GetBytes(previous + "\n" + script);
        mounted.AddVirtualFile(path + ".tu", path + ".tu", database.Length, () => database, "Refii");
        mounted.AddVirtualFile(levelSave, levelSave, save.Length, () => save, "Refii");
        mounted.AddVirtualFile(ModLua.InitKey, ModLua.InitKey, bootstrap.Length, () => bootstrap, "Refii");
    }
}

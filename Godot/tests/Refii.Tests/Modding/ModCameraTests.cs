namespace Refii.Tests.Modding;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Entities;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// The per-player camera (C.11) and hidden parts: camera_mode / camera_changed stated from the game
/// VM, the camera_override hold through the game's own CameraManager.SetCameraOverride kept out of
/// saves, fov, and Godot's hide_part — the pure rules here, and every surface named. No Godot runtime.
/// </summary>
[TestSuite]
public class ModCameraTests
{
    private static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    private static string Read(string root, params string[] path) => File.ReadAllText(Path.Combine(new[] { root }.Concat(path).ToArray()));

    [TestCase]
    public void TheCameraEventIsInTheCatalog()
    {
        ModApiEvent changed = ModApi.Find("camera_changed")!;
        AssertThat(changed.Kind).IsEqual(112);
        AssertThat(changed.Name).IsEqual("CameraChanged");
        AssertThat(string.Join(",", changed.Fields.Select(f => f.Name))).IsEqual("player,mode,previous,hero");
        AssertThat(changed.Cancellable).IsFalse();
        AssertThat(changed.Fields.Any(f => f.Writable)).IsFalse();
        AssertThat(RefiiHost.SignalNames.Contains("camera_changed")).IsTrue();

        // The game's ECameraMode (miscellaneous/cameramodeenum.lua).
        AssertThat(ModApi.Constants.Single(c => c.Name == "CAMERA_GAME").Value).IsEqual(2);
        AssertThat(ModApi.Constants.Single(c => c.Name == "CAMERA_ICS").Value).IsEqual(7);
        AssertThat(ModApi.Constants.Single(c => c.Name == "CAMERA_FIRST_PERSON").Value).IsEqual(8);
        AssertThat(ModApi.Constants.Single(c => c.Name == "CAMERA_CANNED_ACTION").Value).IsEqual(10);
        AssertThat(ModApi.Constants.Single(c => c.Name == "CAMERA_FREEZE").Value).IsEqual(14);

        foreach (string signature in new[] { "refii_mod_camera_mode(", "refii_mod_camera_override(", "refii_mod_camera_fov(", "refii_mod_hide_part(", "refii_mod_hide_part_for(" })
        {
            AssertThat(ModApi.Helpers.Any(h => h.Signature.Contains(signature, StringComparison.Ordinal))).OverrideFailureMessage(signature).IsTrue();
        }

        foreach (string signature in new[] { "Refii.camera_mode(", "Refii.camera_override(", "Refii.fov(", "Refii.hide_part(e, part, on [, p])" })
        {
            AssertThat(ModApi.LuaHelpers.Any(h => h.Signature.Contains(signature, StringComparison.Ordinal))).OverrideFailureMessage(signature).IsTrue();
        }

        foreach (string signature in new[] { "camera_mode(player)", "camera_override(player", "fov(player)", "hide_part(handle, part, on, player = 0)" })
        {
            AssertThat(ModApi.GodotHelpers.Any(h => h.Signature.Contains(signature, StringComparison.Ordinal))).OverrideFailureMessage(signature).IsTrue();
        }
    }

    [TestCase]
    public void TheCameraIsOnEverySurface()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string events = Read(root, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h");
        AssertThat(events.Contains("REFII_EVENT_CAMERA_CHANGED = 112,", StringComparison.Ordinal)).IsTrue();
        AssertThat(events.Contains("struct CameraChangedEvent : Event {", StringComparison.Ordinal)).IsTrue();
        string bus = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_events.cpp");
        AssertThat(bus.Contains("\"player=%d,mode=%d,previous=%d,hero=%u\"", StringComparison.Ordinal)).IsTrue();

        string header = Read(root, "Fable2", "src", "vfs", "include", "core", "refii_mod.h");
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_camera_mode(int32_t player);", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("#define REFII_CAMERA_FIRST_PERSON 8", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("#define REFII_CAMERA_HOLD_AIM 1", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" float refii_mod_camera_fov(uint8_t* base, int32_t player);", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_hide_part(uint32_t entity, const char* part, int32_t on);", StringComparison.Ordinal)).IsTrue();
        AssertThat(header.Contains("extern \"C\" int32_t refii_mod_hide_part_for(uint32_t entity, const char* part, int32_t on, int32_t player);", StringComparison.Ordinal)).IsTrue();

        string natives = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_natives.cpp");
        foreach (string entry in new[]
                 {
                     "{\"camera_mode\", &NativeCameraMode, 0}", "{\"_camera_override\", &NativeCameraOverride, 0}",
                     "{\"fov\", &NativeCameraFov, 0}", "{\"_camera_stated\", &NativeCameraStated, 0}",
                     "{\"_camera_hold\", &NativeCameraHold, 0}", "{\"_camera_held\", &NativeCameraHeld, 0}",
                     "{\"hide_part\", &NativeHidePart, 0}",
                 })
        {
            AssertThat(natives.Contains(entry, StringComparison.Ordinal)).OverrideFailureMessage(entry).IsTrue();
        }

        string commands = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_commands.cpp");
        AssertThat(commands.Contains("op == \"camera_override\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Contains("what == \"camera_mode\"", StringComparison.Ordinal)).IsTrue();
        // The live block's FOV: `lfs f0,0x30(r3); stfs f0,0xC0(r31)`, manager+0x90 +0x30.
        AssertThat(commands.Contains("manager + 0xC0u", StringComparison.Ordinal)).IsTrue();

        // A held LookAt half is a mod's camera, not a scene's.
        string api = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_api.cpp");
        AssertThat(api.Contains("(refii::vfs::ModCameraHeldBits(player) & 2) == 0", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("op='hide_part',entity=", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("\",player=\" + std::to_string(player)", StringComparison.Ordinal)).IsTrue();
        AssertThat(api.Contains("return refii_mod_hide_part_for(entity, part, on, 0);", StringComparison.Ordinal)).IsTrue();
        AssertThat(natives.Contains("refii_mod_hide_part_for(EntityArgument(lua, 1), part, lua.Boolean(3) ? 1 : 0, lua.Int(4))", StringComparison.Ordinal)).IsTrue();

        // Holds go with the VM: cleared before a session's chunks run.
        string hooks = Read(root, "Fable2", "src", "vfs", "src", "hooks", "mod_lua_hooks.cpp");
        int clear = hooks.IndexOf("refii::vfs::ModCameraClearHolds();", StringComparison.Ordinal);
        int init = hooks.IndexOf("RunComposedChunk(work, base, state, kInitKey);", StringComparison.Ordinal);
        AssertThat(clear >= 0 && init > clear).IsTrue();

        string preamble = Read(root, "Godot", "src", "Engine", "mods", "Engine_ModLua.cs");
        AssertThat(preamble.Contains("function Refii.camera_override(p, mode, o)", StringComparison.Ordinal)).IsTrue();
        AssertThat(ModLua.InfrastructureRuntime().Contains("Refii.CAMERA_FIRST_PERSON = 8\n", StringComparison.Ordinal)).IsTrue();

        string host = Read(root, "Godot", "src", "Engine", "mods", "Engine_RefiiHost.cs");
        foreach (string member in new[]
                 {
                     "public int camera_mode(long player)", "public void camera_override(int player, int mode, Godot.Collections.Dictionary options)",
                     "public float fov(int player)", "public bool hide_part(long handle, string part, bool on)",
                     "public bool hide_part(long handle, string part, bool on, int player)",
                     "Refii.Entities.Engine_HiddenParts.Clear();", "query(\"camera_mode\"",
                 })
        {
            AssertThat(host.Contains(member, StringComparison.Ordinal)).OverrideFailureMessage(member).IsTrue();
        }

        string reference = Read(root, "Godot", "docs", "modding", "reference-events.md");
        AssertThat(reference.Contains("| 112 | CameraChanged / camera_changed |", StringComparison.Ordinal)).IsTrue();
        string chapter = Read(root, "Godot", "docs", "modding", "cutscenes.md");
        AssertThat(chapter.Contains("## The camera", StringComparison.Ordinal)).IsTrue();
        AssertThat(chapter.Contains("## Hidden parts", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void TheCoreCameraChunkStatesTheModeAndHoldsThroughTheGamesOverride()
    {
        string? root = RepoRoot();
        if (root == null)
        {
            return;
        }

        string core = Read(root, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs");
        // Around every CameraManager:FrameUpdate(hero): the hold before, the statement after, on both paths.
        AssertThat(core.Contains("pcall(C.CameraHold, hero)", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Split("pcall(C.CameraStated, hero, self.CurrentCamera)").Length - 1).IsEqual(2);
        // The mode is the override set's key for the active camera.
        AssertThat(core.Contains("set.OverridesCameraValidity and type(set.CameraModes) == \"table\"", StringComparison.Ordinal)).IsTrue();
        // The game's own per-player hold, with the data its cameras read.
        AssertThat(core.Contains("CameraManager.SetCameraOverride(hero, hold.Mode, C.CameraScope(claim), hold.Data)", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Contains("local data = { SupercedesCombat = true }", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Contains("data.BoundAngleVert = 0", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Contains("CannedActionCamera.Active", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Contains("RangedCombatCamera.IsValid(hero)", StringComparison.Ordinal)).IsTrue();
        // Never saved: the core save wrapper rewrites the written copy of CameraSave.Overrides.
        AssertThat(core.Contains("pcall(C.CameraSaveTable, t)", StringComparison.Ordinal)).IsTrue();
        AssertThat(core.Contains("save.Overrides = copy", StringComparison.Ordinal)).IsTrue();
        int strip = core.IndexOf("pcall(C.CameraSaveTable, t)", StringComparison.Ordinal);
        int wrapper = core.IndexOf("GetSaveTable = function(...)", StringComparison.Ordinal);
        AssertThat(wrapper >= 0 && strip > wrapper).IsTrue();
    }

    [TestCase]
    public void HiddenPartsAreKeptPerHandle()
    {
        Engine_HiddenParts.Clear();
        int before = Engine_HiddenParts.Revision;
        AssertThat(Engine_HiddenParts.Set(0, "Shadow_Neck", true)).IsFalse();
        AssertThat(Engine_HiddenParts.Set(7, "  ", true)).IsFalse();
        AssertThat(Engine_HiddenParts.Count).IsEqual(0);

        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true)).IsTrue();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true)).IsTrue();
        AssertThat(Engine_HiddenParts.Set(9, "Shadow_Head", true)).IsTrue();
        AssertThat(Engine_HiddenParts.Count).IsEqual(2);
        AssertThat(Engine_HiddenParts.Of(7)).ContainsExactly("Shadow_Neck");
        AssertThat(Engine_HiddenParts.Handles().OrderBy(h => h).ToArray()).ContainsExactly(7u, 9u);
        AssertThat(Engine_HiddenParts.Revision > before).IsTrue();

        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", false)).IsTrue();
        AssertThat(Engine_HiddenParts.Of(7).Length).IsEqual(0);
        AssertThat(Engine_HiddenParts.Count).IsEqual(1);

        Engine_HiddenParts.Clear();
        AssertThat(Engine_HiddenParts.Count).IsEqual(0);
        AssertThat(Engine_HiddenParts.Of(9).Length).IsEqual(0);
    }

    [TestCase]
    public void HiddenPartsAreKeptPerPlayerView()
    {
        Engine_HiddenParts.Clear();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true, 3)).IsFalse();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true, -1)).IsFalse();
        AssertThat(Engine_HiddenParts.Count).IsEqual(0);

        // Player one's view alone: not an every-view part.
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Head", true, 1)).IsTrue();
        AssertThat(Engine_HiddenParts.Count).IsEqual(1);
        AssertThat(Engine_HiddenParts.Of(7).Length).IsEqual(0);
        AssertThat(Engine_HiddenParts.Of(7, 1)).ContainsExactly("Shadow_Head");
        AssertThat(Engine_HiddenParts.Of(7, 2).Length).IsEqual(0);

        // Each part and player is its own statement: the every-view hide outlives player one's show.
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true)).IsTrue();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", true, 2)).IsTrue();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", false, 1)).IsTrue();
        AssertThat(Engine_HiddenParts.Of(7)).ContainsExactly("Shadow_Neck");
        AssertThat(Engine_HiddenParts.Of(7, 0)).ContainsExactly("Shadow_Neck");
        AssertThat(Engine_HiddenParts.Of(7, 2)).ContainsExactly("Shadow_Neck");
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", false)).IsTrue();
        AssertThat(Engine_HiddenParts.Of(7).Length).IsEqual(0);
        AssertThat(Engine_HiddenParts.Of(7, 2)).ContainsExactly("Shadow_Neck");

        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Neck", false, 2)).IsTrue();
        AssertThat(Engine_HiddenParts.Set(7, "Shadow_Head", false, 1)).IsTrue();
        AssertThat(Engine_HiddenParts.Count).IsEqual(0);
        AssertThat(Engine_HiddenParts.Handles().Length).IsEqual(0);
        Engine_HiddenParts.Clear();
    }

    [TestCase]
    public void AHidePartRequestCrossesTheUiKind()
    {
        Engine_UiModRequest.Request? request = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=4097,part='Shadow_Neck',on=1"));
        AssertThat(request.HasValue).IsTrue();
        AssertThat(request!.Value.Op).IsEqual(Engine_UiModRequest.Op.HidePart);
        AssertThat(request.Value.Entity).IsEqual(4097u);
        AssertThat(request.Value.Name).IsEqual("Shadow_Neck");
        AssertThat(request.Value.On).IsTrue();
        AssertThat(request.Value.Player).IsEqual(0);

        // The player whose view loses it; 0 is every view.
        Engine_UiModRequest.Request? mine = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=4097,part='Shadow_Neck',on=1,player=2"));
        AssertThat(mine!.Value.Player).IsEqual(2);
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=4097,part='Shadow_Neck',on=1,player=0"))!.Value.Player).IsEqual(0);

        Engine_UiModRequest.Request? off = Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=4097,part='Shadow_Neck',on=0"));
        AssertThat(off!.Value.On).IsFalse();
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=0,part='Shadow_Neck',on=1")).HasValue).IsFalse();
        AssertThat(Engine_UiModRequest.Parse(RefiiHost.ParseFieldsPlain("op='hide_part',entity=5,part='',on=1")).HasValue).IsFalse();
    }
}

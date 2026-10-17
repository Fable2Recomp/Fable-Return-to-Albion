using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Refii.Bnk;
using Refii.Core;
using Refii.Gdb;
using Refii.Launcher.Core;
using Refii.Mods;
using Refii.Host;

namespace Refii.Save;

public static class Engine_NewGameRuntime
{
    private static BnkFileSystem? _mount;
    private static byte[] _modInit = Array.Empty<byte>();
    private static Engine_NewGameInventory? _inventory;

    public static IReadOnlyList<string> SaveRoots()
    {
        string? liveRoot = HostBridge.GetSaveRoot();
        if (!string.IsNullOrWhiteSpace(liveRoot)) return Engine_NewGameCatalog.DefaultRoots(string.Empty, null, string.Empty, liveRoot);
        var values = RuntimeConfig.ReadValues(Engine_AppRoot.ConfigPath);
        values.TryGetValue("user_data_root", out string? configured);
        if (!string.IsNullOrWhiteSpace(configured) && !Path.IsPathRooted(configured))
            configured = Path.GetFullPath(configured, Engine_AppRoot.Path);
        return Engine_NewGameCatalog.DefaultRoots(Engine_AppRoot.SaveDirectory, configured,
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments));
    }

    public static bool PrepareLive(NewGameSelection selection, out string error)
    {
        error = string.Empty;
        try
        {
            if (_mount is null) throw new InvalidOperationException("The installed game is not mounted yet.");
            if (selection.Start == NewGameStart.Childhood) throw new InvalidOperationException("Choose a skip option.");
            var source = Engine_NewGameCatalog.Validate(Engine_NewGameCatalog.Scan(SaveRoots()), selection);
            string script = BuildScript(selection, source?.Hero, live: true);
            if (Refii.Lua.Engine_LuaDecompiler.Available &&
                !Refii.Lua.Engine_LuaDecompiler.TryCheckSource(script, "@refii/golden-cock", out error))
                throw new InvalidOperationException("The transition could not be checked: " + error);
            byte[] bytes = Encoding.UTF8.GetBytes(script);
            const string key = "refii/new_game.lua";
            _mount.AddVirtualFile(key, key, bytes.Length, () => bytes, "Refii");
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            error = e.Message;
            return false;
        }
    }

    public static void Attach(BnkFileSystem mounted)
    {
        _mount = mounted;
        _inventory = null;
        var blobs = Engine_GameGdb.LoadSupplementalBlobs(path => mounted.TryGetFile(path, out var file) ? file.ReadAllBytes() : null);
        var database = GdbMulti.Build(blobs);
        if (database.RecordCount != 0)
        {
            _inventory = new Engine_NewGameInventory(database);
            const string path = "globals/globals.gdb.tu";
            byte[] patch = GdbWriter.Serialize(_inventory.Aliases);
            if (mounted.TryGetFile(path, out var previous)) patch = GdbPatchBuilder.ApplyOverlayToBaseGdb(previous.ReadAllBytes(), patch);
            mounted.AddVirtualFile(path, path, patch.Length, () => patch, "Refii");
        }
        _modInit = mounted.TryGetFile(ModLua.InitKey, out var file) ? file.ReadAllBytes() : Array.Empty<byte>();
    }

    public static bool Prepare(NewGameSelection selection, out bool? female, out string error)
    {
        female = null;
        error = string.Empty;
        try
        {
            if (_mount is null) throw new InvalidOperationException("The installed game is not mounted yet.");
            NewGameSaveScan scan = Engine_NewGameCatalog.Scan(SaveRoots());
            NewGameSave? source = Engine_NewGameCatalog.Validate(scan, selection);
            string script = BuildScript(selection, source?.Hero);
            if (script.Length != 0 && Refii.Lua.Engine_LuaDecompiler.Available &&
                !Refii.Lua.Engine_LuaDecompiler.TryCheckSource(script, "@refii/new-game", out error))
                throw new InvalidOperationException("The new-game script could not be checked: " + error);
            byte[] bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_modInit) + "\n" + script);
            _mount.AddVirtualFile(ModLua.InitKey, ModLua.InitKey, bytes.Length, () => bytes, "Refii");
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            error = e.Message;
            return false;
        }
    }

    private static string BuildScript(NewGameSelection selection, Engine_SaveHero.Hero? hero, bool live = false) =>
        Engine_NewGameScript.Build(selection, hero, ResolveRecord, live,
            _inventory is null ? null : _inventory.InitialAugments, _inventory is null ? null : _inventory.AugmentationName);

    private static string? ResolveRecord(uint record) => _inventory?.RecordName(record) ??
        (Engine_GameGdb.TryLookupHash(record, out var cursor) ? cursor.View.RecordName(cursor.Record) : null);
}

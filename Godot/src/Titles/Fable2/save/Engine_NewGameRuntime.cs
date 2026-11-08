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

namespace Refii.Titles.Fable2;

using Refii.Save;

public static class Engine_NewGameRuntime
{
    private static BnkFileSystem? _mount;
    private static byte[] _modInit = Array.Empty<byte>();
    private static Engine_NewGameInventory? _inventory;
    private static Func<GdbMulti>? _buildDatabase;

    /// <summary>The carry names and items the New Game script reads, built when it first asks: a start
    /// that finds the patch on disk (<see cref="Attach"/>) has no reason to build them.</summary>
    private static Engine_NewGameInventory? Inventory
    {
        get
        {
            if (_inventory is null && _buildDatabase is { } build && build() is { RecordCount: > 0 } database)
            {
                _inventory = new Engine_NewGameInventory(database, build);
                _inventory.Aliases.Entries.Clear(); // the patch is already mounted
            }

            return _inventory;
        }
    }

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
        _modInit = mounted.TryGetFile(ModLua.InitKey, out var file) ? file.ReadAllBytes() : Array.Empty<byte>();
        byte[]? Read(string path) => mounted.TryGetFile(path, out var file) ? file.ReadAllBytes() : null;
        GdbMulti BuildDatabase() => GdbMulti.Build(Engine_GameGdb.LoadSupplementalBlobs(Read));
        _buildDatabase = BuildDatabase;
        const string path = "globals/globals.gdb.tu";

        // THE PATCH IS KEPT ON DISK. Every start merged the same 42,000 carry names into the same
        // globals.gdb.tu, about 1.3 s of the mount on a phone; it changes only with the data under
        // it (the blobs include that file as the decorate catalog left it) or the build.
        List<byte[]?> blobs = Engine_GameGdb.LoadSupplementalBlobs(Read);
        byte[] key = Engine_KeyedFileCache.Key(CacheFormat, typeof(Engine_NewGameRuntime), blobs);
        string cache = Path.Combine(Engine_Title.Fable2.CacheDirectory, "newgame", "carry.bin");
        if (!Engine_KeyedFileCache.TryRead(cache, key, out byte[] patch))
        {
            var database = GdbMulti.Build(blobs);
            if (database.RecordCount == 0) return;
            _inventory = new Engine_NewGameInventory(database, BuildDatabase);
            patch = GdbWriter.Serialize(_inventory.Aliases);
            _inventory.Aliases.Entries.Clear(); // written; about 4 MB not to keep
            if (mounted.TryGetFile(path, out var previous)) patch = GdbPatchBuilder.ApplyOverlayToBaseGdb(previous.ReadAllBytes(), patch);
            Engine_KeyedFileCache.Write(cache, key, patch);
        }

        mounted.AddVirtualFile(path, path, patch.Length, () => patch, "Refii");
    }

    /// <summary>Raise when the patch changes in a way the build stamp would not show.</summary>
    private const int CacheFormat = 1;

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
            Inventory is null ? null : Inventory.InitialAugments, Inventory is null ? null : Inventory.AugmentationName);

    private static string? ResolveRecord(uint record) => Inventory?.RecordName(record) ??
        (Engine_GameGdb.TryLookupHash(record, out var cursor) ? cursor.View.RecordName(cursor.Record) : null);
}

#if TOOLS
using System;
using System.IO;
using System.Collections.Generic;
using Godot;
using Refii.Assets;
using Refii.Bnk;
using Refii.Core;
using Refii.Mods;
using Refii.Models;
using Refii.Textures;

namespace Refii.EditorTools;

/// <summary>
/// Helper utilities for exporting game assets from the Godot editor directly into active mod workspaces.
/// </summary>
/// <remarks>
/// Automatically constructs the destination file hierarchy matching the virtual file system (VFS)
/// under <c>assets/mods/&lt;ActiveMod&gt;/files/...</c> so that exported assets can be modified in external
/// DCC tools (e.g. Blender, Substance 3D, Photoshop) and immediately discovered by the mod overlay engine.
/// </remarks>
public static class EditorModExporter
{
    private const string ActiveModConfigFilename = "active_mod.txt";

    /// <summary>
    /// Event triggered when the active mod workspace changes, notifying any open docks or workspaces.
    /// </summary>
    public static event Action<string>? ActiveModChanged;

    /// <summary>
    /// Event triggered whenever mod content (GDB patches, Babel strings, creator assets, metadata) is modified.
    /// Passes the name of the modified mod.
    /// </summary>
    public static event Action<string>? ModContentChanged;

    /// <summary>
    /// Notifies all active editor panels and docks that the specified mod's content was updated.
    /// </summary>
    /// <param name="modName">Name of the modified mod, or null/empty to indicate current active mod.</param>
    public static void NotifyModContentChanged(string? modName = null)
    {
        string target = !string.IsNullOrWhiteSpace(modName) ? modName : ActiveModName;
        ModContentChanged?.Invoke(target);
    }

    /// <summary>
    /// Gets or sets the folder name of the active mod workspace (stored under <c>assets/mods/</c>).
    /// </summary>
    public static string ActiveModName
    {
        get => GetActiveModName();
        set => SetActiveModName(value);
    }

    /// <summary>
    /// Ensures that the directory corresponding to the given virtual path exists in the active mod's files directory.
    /// </summary>
    /// <param name="virtualPath">The virtual asset path (e.g. <c>art/models/props/table.mdl</c>).</param>
    /// <returns>The absolute path of the target directory on disk.</returns>
    public static string EnsureModAssetDirectory(string virtualPath)
    {
        string filesRoot = EnsureActiveModWorkspace();
        string normalized = virtualPath.Replace('\\', '/').TrimStart('/');
        string targetPath = Path.Combine(filesRoot, normalized);
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            return dir;
        }

        return filesRoot;
    }

    private static bool IsTestHost =>
        AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
        System.Environment.CommandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase);

    private static string GetActiveModConfigPath()
    {
        if (IsTestHost)
        {
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Godot", "app_userdata", "refii", ActiveModConfigFilename);
        }

        try
        {
            return ProjectSettings.GlobalizePath("user://" + ActiveModConfigFilename);
        }
        catch
        {
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Godot", "app_userdata", "refii", ActiveModConfigFilename);
        }
    }

    /// <summary>
    /// Retrieves the current active mod folder name from user configuration, validating that
    /// the mod actually exists on disk. Returns empty string if no mod exists or is active.
    /// </summary>
    /// <param name="customModsRoot">Optional override directory for mods root.</param>
    public static string GetActiveModName(string? customModsRoot = null)
    {
        string modsRoot = customModsRoot ?? EditorModValueService.ModsRootOverride ?? Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory);
        }

        string path = GetActiveModConfigPath();
        string saved = string.Empty;
        if (File.Exists(path))
        {
            try
            {
                saved = File.ReadAllText(path).Trim();
            }
            catch
            {
            }
        }

        // Validate that saved mod exists on disk as a .f2mod package or directory:
        if (!string.IsNullOrEmpty(saved) && ModWorkspace.IsSafeModFolderName(saved))
        {
            string pkg = Path.Combine(modsRoot, saved + ModWorkspace.PackageExtension);
            string loose = Path.Combine(modsRoot, saved);
            if (File.Exists(pkg) || Directory.Exists(loose))
            {
                return saved;
            }
        }

        // Saved mod does not exist on disk! Check if any other mods exist:
        if (Directory.Exists(modsRoot))
        {
            var discovered = ModWorkspace.DiscoverModInstalls(modsRoot);
            if (discovered.Count > 0)
            {
                string first = discovered[0].Name;
                SetActiveModName(first);
                return first;
            }
        }

        // Zero mods exist on disk
        return string.Empty;
    }

    /// <summary>
    /// Persists the active mod folder name into user configuration.
    /// </summary>
    /// <param name="name">The valid mod directory name.</param>
    public static void SetActiveModName(string name)
    {
        string path = GetActiveModConfigPath();
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
            ActiveModChanged?.Invoke(string.Empty);
            return;
        }

        if (!ModWorkspace.IsSafeModFolderName(name))
        {
            return;
        }

        string trimmed = name.Trim();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, trimmed);
        }
        catch
        {
        }
        ActiveModChanged?.Invoke(trimmed);
    }

    /// <summary>
    /// Discovers all existing mod packages physically residing under the mods root.
    /// Returns an empty list if zero mods exist on disk.
    /// </summary>
    /// <returns>A list of discovered mod folder names.</returns>
    public static List<string> DiscoverModNames()
    {
        var names = new List<string>();
        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory);
        }

        if (Directory.Exists(modsRoot))
        {
            foreach (ModInstall mod in ModWorkspace.DiscoverModInstalls(modsRoot))
            {
                if (!names.Contains(mod.Name))
                {
                    names.Add(mod.Name);
                }
            }
        }

        string current = GetActiveModName();
        if (!string.IsNullOrEmpty(current) && names.Contains(current))
        {
            names.Remove(current);
            names.Insert(0, current);
        }

        return names;
    }

    /// <summary>
    /// Returns the absolute filesystem path for the specified mod's creator folder under <c>res://creator/&lt;ModName&gt;</c>.
    /// </summary>
    /// <param name="modName">Mod name, or null/empty to use the current active mod.</param>
    /// <returns>Absolute filesystem path, or empty string if no mod name is available.</returns>
    public static string GetCreatorDirectory(string? modName = null)
    {
        string target = !string.IsNullOrWhiteSpace(modName) ? modName.Trim() : GetActiveModName();
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        string projectDir = Engine_AppRoot.SafeGetProjectDir();
        return Path.Combine(projectDir, "creator", target);
    }

    /// <summary>
    /// Returns the Godot <c>res://</c> path for the specified mod's creator folder (<c>res://creator/&lt;ModName&gt;/</c>).
    /// </summary>
    /// <param name="modName">Mod name, or null/empty to use the current active mod.</param>
    /// <returns>Godot resource path with trailing slash, or <c>res://creator/</c> if no mod is active.</returns>
    public static string GetCreatorResPath(string? modName = null)
    {
        string target = !string.IsNullOrWhiteSpace(modName) ? modName.Trim() : GetActiveModName();
        if (string.IsNullOrWhiteSpace(target))
        {
            return "res://creator/";
        }

        return $"res://creator/{target}/";
    }

    /// <summary>
    /// Ensures that the active mod package (.f2mod) exists under assets/mods/ and returns
    /// the creators workbench working directory under <c>res://creator/&lt;ActiveMod&gt;/</c>.
    /// </summary>
    /// <returns>The absolute path to the creator workbench working directory, or empty if no mod is active.</returns>
    public static string EnsureActiveModWorkspace()
    {
        string modsRoot = EditorModValueService.ModsRootOverride ?? Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory);
        }

        string activeName = GetActiveModName(modsRoot);
        if (string.IsNullOrEmpty(activeName))
        {
            return string.Empty;
        }

        // 1. Ensure assets/mods/<activeName>.f2mod exists:
        string pkgPath = Path.Combine(modsRoot, activeName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            Directory.CreateDirectory(modsRoot);
            ModWorkspace.CreateModPackage(modsRoot, activeName, out _, out _);
        }

        // 2. Ensure creator workbench working directory exists under res://creator/<activeName>/
        string creatorsDir = GetCreatorDirectory(activeName);
        if (!string.IsNullOrEmpty(creatorsDir))
        {
            Directory.CreateDirectory(creatorsDir);
        }

        // 3. Ensure enabled in loadorder.toml:
        try
        {
            var order = ModLoadOrder.Read(modsRoot);
            order.RegisterMod(activeName, enabled: true);
            order.Write(modsRoot, out _);
        }
        catch
        {
        }

        return creatorsDir;
    }

    /// <summary>
    /// Permanently deletes a mod by name, removing its .f2mod package, loose directory,
    /// and creator workbench folder under <c>res://creator/&lt;modName&gt;/</c> (and legacy <c>assets/creator/&lt;modName&gt;/</c>).
    /// Also unregisters the mod from loadorder.toml and clears ActiveModName if it matches.
    /// </summary>
    /// <param name="modName">Name of the mod to delete.</param>
    /// <param name="error">Error description if deletion failed.</param>
    /// <param name="customModsRoot">Optional override directory for mods root (useful for tests).</param>
    /// <returns>True on success; false otherwise.</returns>
    public static bool DeleteMod(string modName, out string error, string? customModsRoot = null)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(modName))
        {
            error = "Mod name cannot be empty.";
            return false;
        }

        string modsRoot = customModsRoot ?? EditorModValueService.ModsRootOverride ?? Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory);
        }

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        string loosePath = Path.Combine(modsRoot, modName);
        string resCreatorDir = GetCreatorDirectory(modName);
        string creatorDir = Path.Combine(Engine_AppRoot.AssetsDirectory, "creator", modName);
        string legacyCreatorDir = Path.Combine(Engine_AppRoot.AssetsDirectory, "creators", modName);

        bool success = true;
        try
        {
            if (File.Exists(pkgPath))
            {
                File.Delete(pkgPath);
            }

            if (Directory.Exists(loosePath))
            {
                Directory.Delete(loosePath, recursive: true);
            }

            if (!string.IsNullOrEmpty(resCreatorDir) && Directory.Exists(resCreatorDir))
            {
                Directory.Delete(resCreatorDir, recursive: true);
            }

            if (Directory.Exists(creatorDir))
            {
                Directory.Delete(creatorDir, recursive: true);
            }

            if (Directory.Exists(legacyCreatorDir))
            {
                Directory.Delete(legacyCreatorDir, recursive: true);
            }

            try
            {
                var order = ModLoadOrder.Read(modsRoot);
                bool changed = order.Enabled.Remove(modName);
                if (order.Order.Remove(modName))
                {
                    changed = true;
                }

                if (changed)
                {
                    order.Write(modsRoot, out _);
                }
            }
            catch
            {
            }

            if (modName.Equals(GetActiveModName(modsRoot), StringComparison.OrdinalIgnoreCase))
            {
                ActiveModName = string.Empty;
            }

            if (!IsTestHost)
            {
                GD.Print($"[mod_manager] Permanently deleted mod '{modName}' (package and creator folder)");
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (!IsTestHost)
            {
                GD.PushWarning($"[mod_manager] Failed to delete mod '{modName}': {ex.Message}");
            }
            success = false;
        }

        return success;
    }

    /// <summary>
    /// Exports a 3D model into the active mod directory as a standard glTF 2.0 binary file (<c>.glb</c>).
    /// </summary>
    /// <remarks>
    /// Saves the file as <c>&lt;virtualPath&gt;.glb</c> (e.g. <c>art/models/table.mdl.glb</c>). The mod overlay system
    /// (<see cref="ModMeshOverlays"/>) inspects this extension to substitute the custom glTF geometry over the original game model.
    /// </remarks>
    /// <param name="virtualPath">The virtual model path (e.g. <c>art/models/furniture/chair.mdl</c>).</param>
    /// <param name="exportedPath">The absolute filesystem path where the GLB was written.</param>
    /// <param name="error">Error description if the export failed.</param>
    /// <returns>True on success; false otherwise.</returns>
    public static bool ExportModel(
        string virtualPath, out string exportedPath, out string error)
    {
        exportedPath = string.Empty;
        error = string.Empty;

        string filesRoot = EnsureActiveModWorkspace();
        string normalized = virtualPath.Replace('\\', '/').TrimStart('/');
        string targetGlb = Path.Combine(filesRoot, normalized + ".glb");

        string? dir = Path.GetDirectoryName(targetGlb);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        ArrayMesh? mesh = ResourceLoader.Load<ArrayMesh>(
            "game://" + normalized, "ArrayMesh", ResourceLoader.CacheMode.Ignore);

        if (mesh is null || mesh.GetSurfaceCount() == 0)
        {
            error = $"Model 'game://{normalized}' could not be loaded as an ArrayMesh or has 0 surfaces.";
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(normalized);
        var root = new Node3D { Name = stem };
        var instance = new MeshInstance3D { Mesh = mesh, Name = "Mesh" };
        root.AddChild(instance);
        instance.Owner = root;

        // Convert surfaces to standard glTF PBR materials with uncompressed textures
        // so Blender, Maya, Substance, and other DCC tools load with materials and textures intact.
        int surfaceCount = mesh.GetSurfaceCount();
        for (int i = 0; i < surfaceCount; i++)
        {
            Material? surfaceMat = mesh.SurfaceGetMaterial(i);
            BaseMaterial3D pbrMat = ConvertToPbrMaterial(surfaceMat, stem, i);
            instance.SetSurfaceOverrideMaterial(i, pbrMat);
        }

        byte[]? gmdBytes = EditorModValueService.ReadVirtualAssetBytes(normalized + ".gmd");
        var gmdSkeleton = gmdBytes is not null && gmdBytes.Length > 0
            ? Engine_GmdSkeleton.TryParse(gmdBytes)
            : null;
        if (gmdSkeleton is not null && gmdSkeleton.Attachments.Length > 0)
        {
            Engine_GmdWriter.AppendGmdDummiesToScene(root, gmdSkeleton);
        }

        var doc = new GltfDocument();
        var state = new GltfState();
        Error packErr = doc.AppendFromScene(root, state);
        root.QueueFree();

        if (packErr != Error.Ok)
        {
            error = $"Failed to serialize mesh into glTF document: {packErr}";
            return false;
        }

        Error writeErr = doc.WriteToFilesystem(state, targetGlb);
        if (writeErr != Error.Ok)
        {
            error = $"Failed to write GLB to {targetGlb}: {writeErr}";
            return false;
        }

        if (gmdSkeleton is not null && gmdSkeleton.Attachments.Length > 0)
        {
            Engine_GmdWriter.PatchGlbGmdDummyNamesAndExtras(targetGlb, gmdSkeleton);
        }

        exportedPath = targetGlb;
        NotifyModContentChanged(GetActiveModName());
        return true;
    }

    /// <summary>
    /// Exports a 3D model (.mdl) and all of its referenced surface textures into a clean subfolder
    /// inside the active mod's creator directory (<c>res://creator/&lt;ActiveMod&gt;/&lt;subFolderSlug&gt;/</c>).
    /// </summary>
    /// <param name="virtualPath">Virtual model path (e.g. <c>art/models/weapons/ranged/pistol_clockwork/weapon_pistol_clockwork.mdl</c>).</param>
    /// <param name="subFolderSlug">Optional record/asset subfolder name inside the creator folder (e.g. <c>toy_gun</c>).</param>
    /// <param name="filesystem">Optional mounted bank filesystem.</param>
    /// <param name="exportedGlbPath">Absolute filesystem path where the <c>.glb</c> file was written.</param>
    /// <param name="exportedTextures">List of absolute filesystem paths of exported surface <c>.png</c> textures.</param>
    /// <param name="error">Error description if the export failed.</param>
    /// <returns>True on success; false otherwise.</returns>
    public static bool ExportModelToCreatorDir(
        string virtualPath,
        string? subFolderSlug,
        BnkFileSystem? filesystem,
        out string exportedGlbPath,
        out List<string> exportedTextures,
        out string error,
        bool exportTextures = false)
    {
        exportedGlbPath = string.Empty;
        exportedTextures = new List<string>();
        error = string.Empty;

        string creatorRoot = EnsureActiveModWorkspace();
        if (string.IsNullOrEmpty(creatorRoot))
        {
            error = "No active mod is selected.";
            return false;
        }

        string normalized = virtualPath.Replace('\\', '/').TrimStart('/');
        string targetDir = string.IsNullOrWhiteSpace(subFolderSlug)
            ? creatorRoot
            : Path.Combine(creatorRoot, SanitizeCreatorSubFolder(subFolderSlug));
        Directory.CreateDirectory(targetDir);

        string stem = Path.GetFileNameWithoutExtension(normalized);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "model";
        }

        string targetGlb = Path.Combine(targetDir, stem + ".glb");

        ArrayMesh? mesh = null;
        try
        {
            if (ResourceLoader.Exists("game://" + normalized))
            {
                mesh = ResourceLoader.Load<ArrayMesh>(
                    "game://" + normalized, "ArrayMesh", ResourceLoader.CacheMode.Ignore);
            }
        }
        catch
        {
        }

        if (mesh is null || mesh.GetSurfaceCount() == 0)
        {
            byte[]? mdlBytes = EditorModValueService.ReadVirtualAssetBytes(normalized, filesystem);
            if (mdlBytes is not null && mdlBytes.Length > 0)
            {
                mesh = Engine_MdlMesh.TryCreateMesh(mdlBytes, normalized, out string meshErr);
                if (mesh is null || mesh.GetSurfaceCount() == 0)
                {
                    error = $"Model '{normalized}' could not be decoded: {meshErr}";
                    return false;
                }
            }
            else
            {
                error = $"Model 'game://{normalized}' could not be found in VFS or active mod package.";
                return false;
            }
        }

        var root = new Node3D { Name = stem };
        var instance = new MeshInstance3D { Mesh = mesh, Name = "Mesh" };
        root.AddChild(instance);
        instance.Owner = root;

        int surfaceCount = mesh.GetSurfaceCount();
        for (int i = 0; i < surfaceCount; i++)
        {
            Material? surfaceMat = mesh.SurfaceGetMaterial(i);
            BaseMaterial3D pbrMat = ConvertToPbrMaterial(surfaceMat, stem, i);
            instance.SetSurfaceOverrideMaterial(i, pbrMat);
        }

        byte[]? gmdBytes = EditorModValueService.ReadVirtualAssetBytes(normalized + ".gmd", filesystem);
        var gmdSkeleton = gmdBytes is not null && gmdBytes.Length > 0
            ? Engine_GmdSkeleton.TryParse(gmdBytes)
            : null;
        if (gmdSkeleton is not null && gmdSkeleton.Attachments.Length > 0)
        {
            Engine_GmdWriter.AppendGmdDummiesToScene(root, gmdSkeleton);
        }

        var doc = new GltfDocument();
        var state = new GltfState();
        Error packErr = doc.AppendFromScene(root, state);
        root.Free();

        if (packErr != Error.Ok)
        {
            error = $"Failed to serialize mesh into glTF document: {packErr}";
            return false;
        }

        Error writeErr = doc.WriteToFilesystem(state, targetGlb);
        if (writeErr != Error.Ok)
        {
            error = $"Failed to write GLB to {targetGlb}: {writeErr}";
            return false;
        }

        if (gmdSkeleton is not null && gmdSkeleton.Attachments.Length > 0)
        {
            Engine_GmdWriter.PatchGlbGmdDummyNamesAndExtras(targetGlb, gmdSkeleton);
        }

        exportedGlbPath = targetGlb;

        // Model export only writes the .glb (and GMD sockets). Textures are only exported
        // if explicitly requested per-texture in the UI, keeping creator directories clean.
        if (exportTextures)
        {
            foreach (string texPath in GetModelTexturePaths(normalized, filesystem))
            {
                if (ExportTextureToCreatorDir(texPath, subFolderSlug, filesystem, exportPbrTemplate: false, out string texOut, out _))
                {
                    exportedTextures.Add(texOut);
                }
            }
        }

        NotifyModContentChanged(GetActiveModName());
        return true;
    }

    /// <summary>
    /// Exports a virtual texture (<c>.tex</c>) as an uncompressed RGBA8 <c>.png</c> (and optional <c>_orm.png</c> template)
    /// into a clean subfolder inside the active mod's creator directory (<c>res://creator/&lt;ActiveMod&gt;/&lt;subFolderSlug&gt;/</c>).
    /// </summary>
    public static bool ExportTextureToCreatorDir(
        string virtualPath,
        string? subFolderSlug,
        BnkFileSystem? filesystem,
        bool exportPbrTemplate,
        out string exportedPngPath,
        out string error)
    {
        exportedPngPath = string.Empty;
        error = string.Empty;

        string creatorRoot = EnsureActiveModWorkspace();
        if (string.IsNullOrEmpty(creatorRoot))
        {
            error = "No active mod is selected.";
            return false;
        }

        string normalized = virtualPath.Replace('\\', '/').TrimStart('/');
        byte[]? bytes = EditorModValueService.ReadVirtualAssetBytes(normalized, filesystem);
        if ((bytes is null || bytes.Length == 0) &&
            !normalized.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            bytes = EditorModValueService.ReadVirtualAssetBytes(normalized + ".tex", filesystem);
            if (bytes is not null && bytes.Length > 0)
            {
                normalized += ".tex";
            }
        }

        if (bytes is null || bytes.Length == 0)
        {
            error = $"Texture not found in VFS or active mod package: {virtualPath}";
            return false;
        }

        string targetDir = string.IsNullOrWhiteSpace(subFolderSlug)
            ? creatorRoot
            : Path.Combine(creatorRoot, SanitizeCreatorSubFolder(subFolderSlug));
        Directory.CreateDirectory(targetDir);

        string fileName = Path.GetFileName(normalized);
        if (fileName.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }
        if (fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "texture";
        }

        string targetPng = Path.Combine(targetDir, fileName + ".png");

        Image? image = Engine_TexImage.TryCreateImage(bytes, out _, out string decodeErr);
        if (image is null)
        {
            error = $"Could not decode texture '{virtualPath}': {decodeErr}";
            return false;
        }

        if (image.IsCompressed())
        {
            image.Decompress();
        }

        image.Convert(Image.Format.Rgba8);
        Error saveErr = image.SavePng(targetPng);
        if (saveErr != Error.Ok)
        {
            error = $"Failed to save PNG to {targetPng}: {saveErr}";
            return false;
        }

        if (exportPbrTemplate)
        {
            string ormPath = Path.Combine(targetDir, fileName + "_orm.png");
            if (!File.Exists(ormPath))
            {
                var orm = Image.CreateEmpty(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8);
                orm.Fill(new Color(1.0f, 0.75f, 0.0f, 0.5f));
                orm.SavePng(ormPath);
            }
        }

        exportedPngPath = targetPng;
        NotifyModContentChanged(GetActiveModName());
        return true;
    }

    private static string SanitizeCreatorSubFolder(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "exported";
        }

        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c == ' ' || c == '.')
            {
                if (sb.Length > 0 && sb[^1] != '_')
                {
                    sb.Append('_');
                }
            }
        }

        string res = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(res) ? "exported" : res;
    }

    /// <summary>
    /// Resolves all texture virtual paths referenced by materials in the specified .mdl file.
    /// </summary>
    /// <param name="mdlVirtualPath">Virtual path of the model (e.g. <c>art/models/weapon.mdl</c>).</param>
    /// <param name="filesystem">Optional mounted bank filesystem.</param>
    /// <returns>A list of distinct texture virtual paths.</returns>
    public static List<string> GetModelTexturePaths(string mdlVirtualPath, BnkFileSystem? filesystem = null)
    {
        var textures = new List<string>();
        string normalized = mdlVirtualPath.Replace('\\', '/').TrimStart('/');

        byte[]? bytes = EditorModValueService.ReadVirtualAssetBytes(normalized, filesystem);

        if (bytes is null || bytes.Length == 0)
        {
            return textures;
        }

        if (Engine_MdlData.TryParseInfo(bytes, normalized, out MdlInfo info, out _))
        {
            foreach (MdlMesh m in info.Meshes)
            {
                foreach (MdlMaterial mat in m.Materials)
                {
                    void Add(string? path)
                    {
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            string n = path.Replace('\\', '/').TrimStart('/');
                            if (!textures.Contains(n))
                            {
                                textures.Add(n);
                            }
                        }
                    }

                    Add(mat.Diffuse);
                    Add(mat.Normal);
                    Add(mat.Specular);
                    Add(mat.EnvironmentMap);
                    Add(mat.SelfIllumination);
                }
            }
        }

        return textures;
    }

    private static BaseMaterial3D ConvertToPbrMaterial(Material? material, string modelStem, int surfaceIndex)
    {
        BaseMaterial3D pbr;

        if (material is ShaderMaterial shader)
        {
            if (shader.GetShaderParameter("is_pbr").AsBool() &&
                shader.GetShaderParameter("has_orm").AsBool() &&
                shader.GetShaderParameter("orm_tex").As<Texture2D>() is Texture2D orm)
            {
                var ormMat = new OrmMaterial3D();
                ormMat.OrmTexture = PrepareGltfTexture(orm);
                pbr = ormMat;
            }
            else
            {
                pbr = new StandardMaterial3D
                {
                    Roughness = 0.7f,
                    Metallic = 0.0f
                };
            }

            if (shader.GetShaderParameter("albedo_tex").As<Texture2D>() is Texture2D albedo)
            {
                pbr.AlbedoTexture = PrepareGltfTexture(albedo);
                if (shader.GetShaderParameter("alpha_scissor_on").AsBool())
                {
                    pbr.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                    pbr.AlphaScissorThreshold = 0.5f;
                }
            }

            if (shader.GetShaderParameter("has_normal").AsBool() &&
                shader.GetShaderParameter("normal_tex").As<Texture2D>() is Texture2D normal)
            {
                pbr.NormalEnabled = true;
                pbr.NormalTexture = PrepareGltfTexture(normal, isNormalMap: true);
            }

            if (shader.GetShaderParameter("has_emission").AsBool() &&
                shader.GetShaderParameter("emission_tex").As<Texture2D>() is Texture2D emission)
            {
                pbr.EmissionEnabled = true;
                pbr.EmissionTexture = PrepareGltfTexture(emission);
            }
        }
        else if (material is BaseMaterial3D baseMat)
        {
            pbr = (BaseMaterial3D)baseMat.Duplicate();
        }
        else
        {
            pbr = new StandardMaterial3D
            {
                Roughness = 0.7f,
                Metallic = 0.0f
            };
        }

        pbr.ResourceName = $"{modelStem}_Mat_{surfaceIndex}";
        return pbr;
    }

    private static Texture2D? PrepareGltfTexture(Texture2D? source, bool isNormalMap = false)
    {
        if (source is null)
        {
            return null;
        }

        Image? img = source.GetImage();
        if (img is null)
        {
            return source;
        }

        Image copy = (Image)img.Duplicate();
        if (copy.IsCompressed())
        {
            copy.Decompress();
        }

        copy.Convert(Image.Format.Rgba8);

        if (isNormalMap)
        {
            byte[] data = copy.GetData();
            int w = copy.GetWidth();
            int h = copy.GetHeight();
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                float nx = (data[i] / 255f) * 2f - 1f;
                float ny = (data[i + 1] / 255f) * 2f - 1f;
                float nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));
                data[i + 2] = (byte)Math.Clamp((int)MathF.Round((nz * 0.5f + 0.5f) * 255f), 0, 255);
                data[i + 3] = 255;
            }
            copy = Image.CreateFromData(w, h, false, Image.Format.Rgba8, data);
        }

        return ImageTexture.CreateFromImage(copy);
    }

    /// <summary>
    /// Exports a game texture (.tex) to standard uncompressed RGBA8 PNG, optionally emitting an authored ORM companion template.
    /// </summary>
    /// <remarks>
    /// Decodes Xbox 360 tiled texture data into standard PNG at <c>&lt;virtualPath&gt;.png</c> (e.g. <c>art/textures/table.tex.png</c>).
    /// If <paramref name="exportPbrTemplate"/> is true, also emits a starter <c>&lt;stem&gt;_orm.png</c> with neutral PBR defaults
    /// (R=1.0 AO, G=0.75 Roughness, B=0.0 Metallic).
    /// </remarks>
    /// <param name="virtualPath">The virtual texture path (e.g. <c>art/textures/wall.tex</c>).</param>
    /// <param name="filesystem">The mounted bank filesystem containing the asset.</param>
    /// <param name="exportPbrTemplate">If true, generates a companion <c>_orm.png</c> template.</param>
    /// <param name="exportedPath">The absolute path where the primary PNG was saved.</param>
    /// <param name="error">Error description if export fails.</param>
    /// <returns>True on success; false otherwise.</returns>
    public static bool ExportTexture(
        string virtualPath, BnkFileSystem? filesystem, bool exportPbrTemplate,
        out string exportedPath, out string error)
    {
        exportedPath = string.Empty;
        error = string.Empty;

        if (filesystem is null)
        {
            error = "Mounted bank filesystem is unavailable.";
            return false;
        }

        string normalized = virtualPath.Replace('\\', '/').TrimStart('/');
        if (!filesystem.TryGetFile(normalized, out BnkFile file))
        {
            if (!normalized.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) &&
                filesystem.TryGetFile(normalized + ".tex", out BnkFile texFile))
            {
                file = texFile;
                normalized += ".tex";
            }
            else
            {
                error = $"File not found in VFS: {virtualPath}";
                return false;
            }
        }

        string filesRoot = EnsureActiveModWorkspace();
        string targetPng = normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(filesRoot, normalized)
            : Path.Combine(filesRoot, normalized + ".png");

        string? dir = Path.GetDirectoryName(targetPng);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        byte[] bytes;
        try
        {
            bytes = file.ReadAllBytes();
        }
        catch (Exception ex)
        {
            error = $"Could not read bytes for {virtualPath}: {ex.Message}";
            return false;
        }

        Image? image = Engine_TexImage.TryCreateImage(bytes, out _, out string decodeErr);
        if (image is null)
        {
            error = $"Could not decode texture: {decodeErr}";
            return false;
        }

        if (image.IsCompressed())
        {
            image.Decompress();
        }

        image.Convert(Image.Format.Rgba8);
        Error saveErr = image.SavePng(targetPng);
        if (saveErr != Error.Ok)
        {
            error = $"Failed to save PNG to {targetPng}: {saveErr}";
            return false;
        }

        if (exportPbrTemplate)
        {
            string stem = targetPng;
            if (stem.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^4];
            }

            if (stem.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^4];
            }

            string ormPath = stem + "_orm.png";
            if (!File.Exists(ormPath))
            {
                var orm = Image.CreateEmpty(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8);
                orm.Fill(new Color(1.0f, 0.75f, 0.0f, 0.5f));
                orm.SavePng(ormPath);
            }
        }

        exportedPath = targetPng;
        return true;
    }

    /// <summary>
    /// Exports raw bytes of any virtual file directly to its corresponding path in the active mod directory.
    /// </summary>
    /// <param name="virtualPath">The virtual file path.</param>
    /// <param name="filesystem">The mounted bank filesystem.</param>
    /// <param name="exportedPath">The absolute path where the file was written.</param>
    /// <param name="error">Error description if export fails.</param>
    /// <returns>True on success; false otherwise.</returns>
    public static bool ExportRawFile(
        string virtualPath, BnkFileSystem? filesystem, out string exportedPath, out string error)
    {
        exportedPath = string.Empty;
        error = string.Empty;

        if (filesystem is null || !filesystem.TryGetFile(virtualPath, out BnkFile file))
        {
            error = $"File not found in VFS: {virtualPath}";
            return false;
        }

        string filesRoot = EnsureActiveModWorkspace();
        string normalized = virtualPath.Replace('\\', '/').TrimStart('/');
        string targetPath = Path.Combine(filesRoot, normalized);

        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            File.WriteAllBytes(targetPath, file.ReadAllBytes());
            exportedPath = targetPath;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Opens Windows Explorer or native file browser highlighting the exported file or directory.
    /// </summary>
    /// <param name="path">Absolute file or directory path.</param>
    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        string? dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            OS.ShellOpen(dir);
        }
    }
}
#endif

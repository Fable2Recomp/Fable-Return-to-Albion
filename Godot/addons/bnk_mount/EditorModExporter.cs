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

    /// <summary>
    /// Retrieves the current active mod folder name from user configuration, defaulting to "MyMod".
    /// </summary>
    public static string GetActiveModName()
    {
        string path = ProjectSettings.GlobalizePath("user://" + ActiveModConfigFilename);
        if (File.Exists(path))
        {
            try
            {
                string saved = File.ReadAllText(path).Trim();
                if (saved.Length > 0 && ModWorkspace.IsSafeModFolderName(saved))
                {
                    return saved;
                }
            }
            catch
            {
            }
        }

        return "MyMod";
    }

    /// <summary>
    /// Persists the active mod folder name into user configuration.
    /// </summary>
    /// <param name="name">The valid mod directory name.</param>
    public static void SetActiveModName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !ModWorkspace.IsSafeModFolderName(name))
        {
            return;
        }

        string path = ProjectSettings.GlobalizePath("user://" + ActiveModConfigFilename);
        try
        {
            File.WriteAllText(path, name.Trim());
        }
        catch
        {
        }
    }

    /// <summary>
    /// Discovers all existing mod directories under the mods root and includes the active mod.
    /// </summary>
    /// <returns>A list of discovered mod folder names.</returns>
    public static List<string> DiscoverModNames()
    {
        var names = new List<string>();
        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot) || !Directory.Exists(modsRoot))
        {
            names.Add(GetActiveModName());
            return names;
        }

        foreach (ModInstall mod in ModWorkspace.DiscoverModInstalls(modsRoot))
        {
            names.Add(mod.Name);
        }

        string current = GetActiveModName();
        if (!names.Contains(current))
        {
            names.Insert(0, current);
        }

        return names;
    }

    /// <summary>
    /// Ensures that the active mod folder, its <c>files/</c> directory, and its initial <c>mod.toml</c> manifest exist on disk.
    /// </summary>
    /// <returns>The absolute path to the active mod's <c>files/</c> directory.</returns>
    public static string EnsureActiveModWorkspace()
    {
        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory);
        }

        string activeName = GetActiveModName();
        string modDir = Path.Combine(modsRoot, activeName);
        Directory.CreateDirectory(modDir);

        string filesDir = Path.Combine(modDir, ModWorkspace.ModFilesFolderName);
        Directory.CreateDirectory(filesDir);

        string toml = Path.Combine(modDir, ModWorkspace.ModMetadataFilename);
        if (!File.Exists(toml))
        {
            try
            {
                File.WriteAllText(
                    toml,
                    $"name = \"{activeName}\"\nversion = \"1.0.0\"\nauthor = \"Modder\"\ndescription = \"Custom mod for Fable 2\"\n");
            }
            catch
            {
            }
        }

        try
        {
            var order = ModLoadOrder.Read(modsRoot);
            if (!order.Enabled.Contains(activeName))
            {
                order.Enabled.Insert(0, activeName);
                order.Write(modsRoot, out _);
            }
        }
        catch
        {
        }

        return filesDir;
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

        exportedPath = targetGlb;
        return true;
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

        byte[]? bytes = null;
        if (filesystem is not null && filesystem.TryGetFile(normalized, out BnkFile file))
        {
            bytes = file.ReadAllBytes();
        }
        else
        {
            bytes = Engine_BnkMeshLoader.ReadBytes(normalized);
        }

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
            if (shader.GetShaderParameter("has_orm").AsBool() &&
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
                pbr.NormalTexture = PrepareGltfTexture(normal);
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

    private static Texture2D? PrepareGltfTexture(Texture2D? source)
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

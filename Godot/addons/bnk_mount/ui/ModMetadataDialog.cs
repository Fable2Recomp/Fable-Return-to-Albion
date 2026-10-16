#if TOOLS
using System;
using System.IO;
using Godot;
using Refii.Core;
using Refii.EditorTools;
using Refii.Mods;

namespace Refii.Assets.UI;

/// <summary>
/// Modal dialog allowing modders to inspect and edit embedded TOML metadata
/// (Title, Author, Version, Description) directly in the active .f2mod container header.
/// </summary>
public partial class ModMetadataDialog : ConfirmationDialog
{
    private string _modName = string.Empty;
    private LineEdit? _authorEdit;
    private LineEdit? _versionEdit;
    private TextEdit? _descEdit;

    /// <summary>
    /// Event raised when mod metadata has been saved successfully.
    /// </summary>
    public event Action? MetadataUpdated;

    public ModMetadataDialog()
    {
        Title = "Edit Mod Metadata";
        Size = new Vector2I(520, 360);
        MinSize = new Vector2I(450, 300);
        OkButtonText = "Save Metadata";

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);

        var authorBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        authorBox.AddChild(new Label { Text = "Author:" });
        _authorEdit = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        authorBox.AddChild(_authorEdit);
        row.AddChild(authorBox);

        var versionBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        versionBox.AddChild(new Label { Text = "Version:" });
        _versionEdit = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        versionBox.AddChild(_versionEdit);
        row.AddChild(versionBox);

        root.AddChild(row);

        root.AddChild(new Label { Text = "Description / Patch Notes:" });
        _descEdit = new TextEdit
        {
            CustomMinimumSize = new Vector2(0, 120),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        root.AddChild(_descEdit);

        AddChild(root);
        Confirmed += OnConfirmed;
    }

    /// <summary>
    /// Opens the dialog populated with current metadata from the specified mod package.
    /// </summary>
    /// <param name="modName">Name of the active mod (without .f2mod extension).</param>
    public void OpenForMod(string modName)
    {
        _modName = modName;
        Title = $"Edit Metadata: {modName}";

        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory);
        }

        string pkgPath = Path.Combine(modsRoot, modName + ModWorkspace.PackageExtension);
        ModMetadata meta = new ModMetadata();

        if (File.Exists(pkgPath))
        {
            F2ModPackage.TryReadMetadata(pkgPath, out meta, out _);
        }

        if (_authorEdit is not null) _authorEdit.Text = meta.Author;
        if (_versionEdit is not null) _versionEdit.Text = !string.IsNullOrEmpty(meta.Version) ? meta.Version : "1.0.0";
        if (_descEdit is not null) _descEdit.Text = meta.Description;

        PopupCentered();
    }

    private void OnConfirmed()
    {
        if (string.IsNullOrEmpty(_modName))
        {
            return;
        }

        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory);
        }

        string pkgPath = Path.Combine(modsRoot, _modName + ModWorkspace.PackageExtension);
        if (!File.Exists(pkgPath))
        {
            return;
        }

        var meta = new ModMetadata
        {
            Title = _modName,
            Author = _authorEdit?.Text.Trim() ?? string.Empty,
            Version = _versionEdit?.Text.Trim() ?? "1.0.0",
            Description = _descEdit?.Text.Trim() ?? string.Empty
        };

        bool ok = F2ModPackage.IncrementalSync(
            pkgPath,
            updates: Array.Empty<(string Path, byte[] Bytes)>(),
            deletes: null,
            updatedMeta: meta);

        if (ok)
        {
            MetadataUpdated?.Invoke();
            EditorModExporter.NotifyModContentChanged(_modName);
        }
        else
        {
            GD.PushWarning($"[mod_meta] Failed to update metadata for {_modName}");
        }
    }
}
#endif

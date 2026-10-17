#if TOOLS
using System;
using System.IO;
using Godot;
using Refii.Core;
using Refii.EditorTools;
using Refii.Mods;

namespace Refii.Assets.UI;

/// <summary>
/// Modal dialog allowing modders to initialize a brand-new .f2mod package and creator workspace.
/// </summary>
public partial class CreateModDialog : ConfirmationDialog
{
    private LineEdit? _nameEdit;
    private LineEdit? _authorEdit;
    private TextEdit? _descEdit;
    private Label? _errorLabel;

    /// <summary>
    /// Event raised when a new mod package has been created successfully.
    /// Passes the newly created mod name.
    /// </summary>
    public event Action<string>? ModCreated;

    public CreateModDialog()
    {
        Title = "Create New Mod";
        Size = new Vector2I(520, 380);
        MinSize = new Vector2I(450, 320);
        OkButtonText = "Create Mod";

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);

        root.AddChild(new Label { Text = "Mod Name (Letters, numbers, underscores):" });
        _nameEdit = new LineEdit
        {
            PlaceholderText = "e.g. DragonWeapons",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _nameEdit.TextChanged += OnFieldsChanged;
        root.AddChild(_nameEdit);

        root.AddChild(new Label { Text = "Author:" });
        _authorEdit = new LineEdit
        {
            PlaceholderText = "e.g. HeroOfOakvale",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        root.AddChild(_authorEdit);

        root.AddChild(new Label { Text = "Description:" });
        _descEdit = new TextEdit
        {
            PlaceholderText = "Describe what your mod does...",
            CustomMinimumSize = new Vector2(0, 90),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        root.AddChild(_descEdit);

        _errorLabel = new Label
        {
            Text = string.Empty,
            Modulate = new Color(1.0f, 0.4f, 0.4f),
            Visible = false
        };
        root.AddChild(_errorLabel);

        AddChild(root);
        Confirmed += OnConfirmed;
    }

    /// <summary>
    /// Opens the dialog with empty fields and default author name.
    /// </summary>
    public void Open()
    {
        if (_nameEdit is not null) _nameEdit.Text = string.Empty;
        if (_authorEdit is not null) _authorEdit.Text = System.Environment.UserName;
        if (_descEdit is not null) _descEdit.Text = string.Empty;
        if (_errorLabel is not null) _errorLabel.Visible = false;

        PopupCentered();
        _nameEdit?.GrabFocus();
    }

    private void OnFieldsChanged(string newText)
    {
        if (_errorLabel is null) return;

        string name = newText.Trim();
        if (name.Length > 0 && !ModWorkspace.IsSafeModFolderName(name))
        {
            _errorLabel.Text = "Identifier may only contain letters, digits, and underscores.";
            _errorLabel.Visible = true;
            GetOkButton().Disabled = true;
        }
        else
        {
            _errorLabel.Visible = false;
            GetOkButton().Disabled = name.Length == 0;
        }
    }

    private void OnConfirmed()
    {
        string name = _nameEdit?.Text.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) || !ModWorkspace.IsSafeModFolderName(name))
        {
            return;
        }

        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory);
        }

        Directory.CreateDirectory(modsRoot);

        var meta = new ModMetadata
        {
            Title = name,
            Author = _authorEdit?.Text.Trim() ?? string.Empty,
            Description = _descEdit?.Text.Trim() ?? string.Empty,
            Version = "1.0.0"
        };

        bool ok = ModWorkspace.CreateModPackage(modsRoot, name, out string pkgPath, out string error);
        if (!ok)
        {
            GD.PushWarning($"[create_mod] Failed to create package '{name}': {error}");
            return;
        }

        F2ModPackage.IncrementalSync(
            pkgPath,
            updates: Array.Empty<(string Path, byte[] Bytes)>(),
            deletes: null,
            updatedMeta: meta);

        // Ensure active in loadorder.toml
        try
        {
            var order = ModLoadOrder.Read(modsRoot);
            order.RegisterMod(name, enabled: true);
            order.Write(modsRoot, out _);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[create_mod] Failed to update load order: {ex.Message}");
        }

        // Set as active mod
        EditorModExporter.ActiveModName = name;

        // Ensure creator folder exists under res://creator/<name>/
        string creatorDir = EditorModExporter.GetCreatorDirectory(name);
        if (!string.IsNullOrEmpty(creatorDir))
        {
            Directory.CreateDirectory(creatorDir);
        }
        BnkMountPlugin.Instance?.SyncActiveModCreatorFavorite(name);

        ModCreated?.Invoke(name);
    }
}
#endif

#if TOOLS
using System;
using Godot;
using Refii.EditorTools;
using Refii.Mods;

namespace Refii.Assets.UI;

/// <summary>
/// "New mod from template": a name, a title, an author and one of <see cref="ModTemplates"/>,
/// written through <see cref="RefiiModApi.CreateModFromTemplate"/> and enabled in the load
/// order, so the package is a working mod the moment the dialog closes.
/// </summary>
public partial class NewModFromTemplateDialog : ConfirmationDialog
{
    private LineEdit? _nameEdit;
    private LineEdit? _titleEdit;
    private LineEdit? _authorEdit;
    private OptionButton? _templateSelect;
    private Label? _descriptionLabel;
    private Label? _errorLabel;

    /// <summary>Raised with the mod's name once its package is written and enabled.</summary>
    public event Action<string>? ModCreated;

    public NewModFromTemplateDialog()
    {
        Title = "New Mod from Template";
        Size = new Vector2I(520, 360);
        MinSize = new Vector2I(450, 300);
        OkButtonText = "Create Mod";

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);

        root.AddChild(new Label { Text = "Mod Name (letters, digits, underscores):" });
        _nameEdit = new LineEdit
        {
            PlaceholderText = "e.g. frost_mage",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _nameEdit.TextChanged += OnNameChanged;
        root.AddChild(_nameEdit);

        root.AddChild(new Label { Text = "Title:" });
        _titleEdit = new LineEdit
        {
            PlaceholderText = "e.g. Frost Mage",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        root.AddChild(_titleEdit);

        root.AddChild(new Label { Text = "Author:" });
        _authorEdit = new LineEdit
        {
            PlaceholderText = "e.g. HeroOfOakvale",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        root.AddChild(_authorEdit);

        root.AddChild(new Label { Text = "Template:" });
        _templateSelect = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (ModTemplate template in ModTemplates.All)
        {
            _templateSelect.AddItem(template.Title);
            _templateSelect.SetItemMetadata(_templateSelect.ItemCount - 1, template.Id);
        }
        _templateSelect.ItemSelected += _ => RefreshDescription();
        root.AddChild(_templateSelect);

        _descriptionLabel = new Label
        {
            Modulate = new Color(0.72f, 0.75f, 0.80f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 40)
        };
        _descriptionLabel.AddThemeFontSizeOverride("font_size", 12);
        root.AddChild(_descriptionLabel);

        _errorLabel = new Label
        {
            Modulate = new Color(1.0f, 0.4f, 0.4f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Visible = false
        };
        root.AddChild(_errorLabel);

        AddChild(root);
        Confirmed += OnConfirmed;
    }

    public void Open()
    {
        if (_nameEdit is not null) _nameEdit.Text = string.Empty;
        if (_titleEdit is not null) _titleEdit.Text = string.Empty;
        if (_authorEdit is not null) _authorEdit.Text = System.Environment.UserName;
        if (_errorLabel is not null) _errorLabel.Visible = false;
        _templateSelect?.Select(0);
        RefreshDescription();
        GetOkButton().Disabled = true;

        PopupCentered();
        _nameEdit?.GrabFocus();
    }

    private void RefreshDescription()
    {
        if (_descriptionLabel is null || _templateSelect is null) return;
        _descriptionLabel.Text = ModTemplates.Find(SelectedTemplateId()) is { } t
            ? $"{t.Description}\nFiles: {string.Join(", ", System.Linq.Enumerable.Select(t.Render("mod", "Mod"), f => f.Path))}"
            : string.Empty;
    }

    private string SelectedTemplateId() =>
        _templateSelect is { Selected: >= 0 } select ? select.GetItemMetadata(select.Selected).AsString() : string.Empty;

    private void OnNameChanged(string newText)
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
        if (name.Length == 0 || !ModWorkspace.IsSafeModFolderName(name))
        {
            return;
        }

        var api = new RefiiModApi();
        System.IO.Directory.CreateDirectory(api.GetModsRoot());
        string error = api.CreateModFromTemplate(
            name, _titleEdit?.Text.Trim() ?? string.Empty, _authorEdit?.Text.Trim() ?? string.Empty, SelectedTemplateId());
        if (error.Length != 0)
        {
            GD.PushWarning($"[mod_studio] New mod from template '{name}' failed: {error}");
            Report("Could not create the mod", error);
            return;
        }

        // A template mod is meant to run as written: enabled in the load order, as "+ New Mod" does.
        try
        {
            ModLoadOrder order = ModLoadOrder.Read(api.GetModsRoot());
            order.RegisterMod(name, enabled: true);
            order.Write(api.GetModsRoot(), out _);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[mod_studio] Failed to update load order for '{name}': {ex.Message}");
        }

        string files = string.Join("\n", System.Linq.Enumerable.Select(api.ListFiles(), f => "  " + f));
        Report($"Created '{name}'", $"From the {ModTemplates.Find(SelectedTemplateId())?.Title ?? SelectedTemplateId()} template, enabled in the load order:\n{files}");
        ModCreated?.Invoke(name);
    }

    private void Report(string title, string text)
    {
        var dialog = new AcceptDialog { Title = title, DialogText = text };
        GetParent()?.AddChild(dialog);
        dialog.Confirmed += () => dialog.QueueFree();
        dialog.Canceled += () => dialog.QueueFree();
        dialog.PopupCentered();
    }
}
#endif

#if TOOLS
using Godot;
using Refii.Assets.UI;
using Refii.EditorTools;
using Refii.Fx;

namespace Refii.Assets;

/// <summary>
/// A <see cref="FableFx"/> selected in the editor gets a card in the inspector: save it as an
/// effect of the active mod (the effect it draws as a new one, with the node's scale and tint
/// carried as the effect's own — <see cref="RefiiModApi.SaveFableFxAsEffect"/>), or open it in the
/// FX Lab's effect editor to change its emitters.
/// </summary>
public partial class FableFxInspectorPlugin : EditorInspectorPlugin
{
    public override bool _CanHandle(GodotObject godotObject) => godotObject is FableFx;

    public override void _ParseBegin(GodotObject godotObject)
    {
        if (godotObject is not FableFx fx)
        {
            return;
        }

        var api = new RefiiModApi();
        var panel = new PanelContainer();
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        panel.AddChild(margin);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 6);
        margin.AddChild(root);

        string mod = api.GetActiveMod();
        var title = new Label { Text = $"FX — {fx.EffectName}  [{(mod.Length != 0 ? mod : "no active mod")}]" };
        title.AddThemeColorOverride("font_color", new Color(0.55f, 0.84f, 1.0f));
        root.AddChild(title);

        var row = new HBoxContainer();
        root.AddChild(row);
        var name = new LineEdit
        {
            Text = mod.Length != 0 ? $"{mod}_{fx.EffectName}".ToLowerInvariant() : fx.EffectName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "the new effect's name; it is written as fx/effects/<name>.json",
        };
        row.AddChild(name);

        var status = new Label { AutowrapMode = TextServer.AutowrapMode.Word };
        var save = new Button
        {
            Text = "💾 Save as mod effect",
            TooltipText = "The effect this node draws, as a new effect of the active mod, with the node's EffectScale and Tint as its own scale and tint",
        };
        save.Pressed += () =>
        {
            string error = api.SaveFableFxAsEffect(fx, name.Text.Trim());
            status.Text = error.Length != 0 ? error : $"Saved {Engine_FxModEffects.PathOf(name.Text.Trim())} into '{api.GetActiveMod()}'.";
        };
        row.AddChild(save);

        var edit = new Button { Text = "Edit in FX Lab", TooltipText = "Open this effect in the Mod Studio's FX Lab to edit its emitters" };
        edit.Pressed += () =>
        {
            try
            {
                EditorInterface.Singleton.SetMainScreenEditor("Mod Studio");
            }
            catch
            {
            }

            ModdingHubPanel.Instance?.OpenFxEffect(fx.EffectName);
        };
        root.AddChild(edit);
        root.AddChild(status);
        AddCustomControl(panel);
    }
}
#endif

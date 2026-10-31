using System;
using System.IO;
using Godot;
using Refii.AndroidRuntime;
using Refii.Core;
using Refii.Launcher.Core;

namespace Refii.Launcher;

/// <summary>Android backend for the same launcher scene, settings drawer and installation progress used on desktop.</summary>
public partial class LauncherApp
{
    private AndroidPhoneLauncher? _phone;
    private string _phoneStatus = "";

    /// <summary>The File menu on a phone: installation and save transfer instead of the install folder.</summary>
    private static readonly string[] AndroidFileMenuItems =
    {
        "Install from ISO", "Cancel installation", "", "Export logs and saves", "Export saves", "Import save", "",
        "Show Announcements", "Show Debug Panel", "Close",
    };

    /// <summary>Builds the original launcher with private Android settings and self-contained installation actions.</summary>
    private void ReadyAndroid()
    {
        AndroidNativeLog.StartPump();
        AndroidLastExit.RecordOnce();
        string user = OS.GetUserDataDir();
        System.Environment.SetEnvironmentVariable(Engine_AppRoot.EnvironmentVariable, Path.Combine(user, "runtime"));
        Engine_AppRoot.Invalidate();
        Engine_AppRoot.EnsureLayout();
        LauncherPaths.OverrideLauncherDirectory = Engine_AppRoot.Path;
        LoadLauncherConfig();
        LauncherTheme.Load();
        // The mods that ship in the APK, packed into the mods folder before the Mods panel
        // first lists it, as on desktop. Installed switched off; never over a copy someone has edited.
        // They are Fable 2's: installed while Fable 2 is still the active title, before the switch's.
        try
        {
            foreach (string line in AndroidBundledMods.Install(Engine_AppRoot.BundledModsDirectory,
                Refii.Assets.Engine_GameAssets.ModsRoot))
                GD.Print("[mods] bundled " + line);
        }
        catch (Exception error) { GD.PushError("[mods] bundled mods not installed: " + error.Message); }
        LoadActiveTitle();
        SeedAndroidConfig();
        _patches = new Patches();
        _launch = new LaunchController(_patches);
        _updates = new UpdateChecker();
        BindNodes();
        AddAndroidInstallation();
        _font = LoadLauncherFont();
        BuildStyles();
        ApplyStyles();
        WireMenus();
        WireTabs();
        _settingsTabs.CurrentTab = _phone.HasPlayableInstall ? 0 : 2;
        _infoTabs.AddTab("Installation");
        _infoTabs.CurrentTab = _infoTabs.TabCount - 1;
        _settingsTitle.Text = "Launch options";
        Resized += ApplyLayout;
        ApplyLayout();
        RefreshState();
        RefreshDebugDrawer();
        CallDeferred(MethodName.PlayEntrance);
        _primaryButton.GrabFocus();
        FetchGitHubData();
    }

    /// <summary>Writes the accepted Android defaults into the active title's config when it has none, before
    /// the shared settings model fills missing CVars. Existing user settings always keep their exact values.</summary>
    private static void SeedAndroidConfig()
    {
        Engine_AppRoot.EnsureLayout();
        if (!File.Exists(Engine_AppRoot.ConfigPath))
            File.WriteAllText(Engine_AppRoot.ConfigPath, "max_fps = 0\nsun_shadow_map = 1024\nsun_shadow_splits = 2\nshader_warmup_enabled = false\nmaterials_modernized = false\n");
    }

    /// <summary>Adds the active title's installation controls to the settings drawer and resolves its config's paths.</summary>
    private void AddAndroidInstallation()
    {
        _phone = new AndroidPhoneLauncher { Hosted = true, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill };
        _reinstallPanel.GetParent().AddChild(_phone);
        AndroidLauncherSettings.Prepare(Engine_AppRoot.Path, _phone.InstalledGameRoot);
    }

    /// <summary>Whether the phone's installer is running, which a title switch waits for.</summary>
    private bool AndroidInstalling => _phone?.IsInstalling ?? false;

    /// <summary>
    /// Follows a title switch (<see cref="SwitchTitle"/>): the installation controls are rebuilt for the
    /// new title's own installs and builds (AndroidRuntimeOptions.TitleDirectory), its config gets the
    /// Android defaults, and the Installation tab the title's info tabs replaced is added again.
    /// </summary>
    private void ReloadAndroidTitle()
    {
        if (_phone == null) return;
        SeedAndroidConfig();
        // Godot's thread model is the app's and fixed when it starts, so renderer.cfg states the next
        // launch's: the title just chosen's (AndroidRenderThread), not the last one played.
        var config = RuntimeConfig.ReadValues(Engine_AppRoot.ConfigPath);
        AndroidRenderThread.Apply(config);
        if (AndroidRenderThread.Configured(config) != AndroidRenderThread.Active)
            Log(Engine_Title.Active.Name + "'s render thread setting takes effect when the app next starts.");
        _phone.QueueFree();
        _phone.GetParent().RemoveChild(_phone);
        AddAndroidInstallation();
        _infoTabs.AddTab("Installation");
        _infoTabs.Visible = true;
        _settingsTabs.CurrentTab = _phone.HasPlayableInstall ? 0 : 2;
        _settingsPanel.ReloadForTitle();
        ApplyLayout();
        RefreshState();
        RefreshDebugDrawer();
    }

    /// <summary>Publishes worker progress on the original button and progress bar without desktop runtime checks.</summary>
    private void RefreshAndroidState()
    {
        if (_phone == null) return;
        bool running = _phone.IsInstalling;
        _primaryButton.Text = AndroidLauncherProgress.Label(running, _phone.HasPlayableInstall, _phone.Status);
        _primaryButton.Disabled = running;
        _progressTrack.Visible = running;
        _progressTrack.Position = _primaryButton.Position;
        _progressTrack.Size = _primaryButton.Size;
        float fraction = AndroidLauncherProgress.Fraction(_phone.Status);
        _progressClip.Visible = running && fraction > 0;
        _progressClip.Position = _primaryButton.Position;
        _progressClip.Size = new Vector2(_primaryButton.Size.X * fraction, _primaryButton.Size.Y);
        _progressFill.Size = _primaryButton.Size;
        _warningLabel.Text = _phone.Status;
        ApplyFont(_primaryButton, LauncherScale.Font(running ? LauncherTokens.BodySize : LauncherTokens.DisplaySize));
        RefreshInfoBody();
    }

    /// <summary>Routes Play to Android activation and Install to the original launcher's installation drawer.</summary>
    private void PrimaryAndroid()
    {
        if (_phone == null || _phone.IsInstalling) return;
        if (_phone.HasPlayableInstall) { _settingsPanel.SaveIfDirty(); _phone.PlayFromLauncher(); }
        else { _settingsTabs.CurrentTab = 2; RefreshDebugDrawer(); ShowSettingsDrawer(); }
    }

    /// <summary>Keeps the original art and progress view current while the independent service owns the worker.</summary>
    /// <param name="delta">Godot frame interval.</param>
    private void ProcessAndroid(double delta)
    {
        _backgrounds.Update((float)delta);
        UpdateArtCredit();
        if (_phone == null || _phoneStatus == _phone.Status) return;
        _phoneStatus = _phone.Status;
        if (_consoleLines.Count >= 80) _consoleLines.RemoveAt(0);
        _consoleLines.Add(_phoneStatus);
        RefreshAndroidState();
    }

    /// <summary>Handles the phone's own File menu entries (<see cref="AndroidFileMenuItems"/>), including worker cancellation.</summary>
    /// <param name="item">Selected visible menu label.</param>
    /// <returns>False for an entry the shared menu handles.</returns>
    private bool OnAndroidFileMenu(string item)
    {
        switch (item)
        {
            case "Install from ISO":
                _settingsTabs.CurrentTab = 2; RefreshDebugDrawer(); ShowSettingsDrawer(); return true;
            case "Cancel installation":
                _phone?.CancelInstallation(); return true;
            case "Export saves":
                _phone?.Export(AndroidLogBundle.Contents.Saves); return true;
            case "Export logs and saves":
                _phone?.Export(AndroidLogBundle.Contents.LogsAndSaves); return true;
            case "Import save":
                _phone?.ImportSave(); return true;
            default:
                return false;
        }
    }

    /// <summary>Shows shared launch settings or the phone's installation controls.</summary>
    /// <returns>False when there is no phone backend and the desktop drawer applies.</returns>
    private bool RefreshAndroidDrawer()
    {
        if (_phone == null) return false;
        _settingsPanel.Visible = _settingsTabs.CurrentTab == 0;
        _moddingPanel.Visible = _settingsTabs.CurrentTab == 1;
        _phone.Visible = _settingsTabs.CurrentTab == 2;
        _reinstallPanel.Hide();
        return true;
    }

    /// <summary>Shows the Android installation progress log in the info drawer's Installation tab.</summary>
    /// <returns>False when another tab, or the desktop, is showing.</returns>
    private bool ShowAndroidInstallationLog()
    {
        if (_phone == null || _infoTabs.CurrentTab != _infoTabs.TabCount - 1 || _infoTabs.GetTabTitle(_infoTabs.CurrentTab) != "Installation") return false;
        _infoBody.Text = string.Join("\n", _consoleLines).Replace("[", "[lb]", StringComparison.Ordinal);
        return true;
    }

    /// <summary>Scales the phone's installation controls in place of the desktop's install panel.</summary>
    /// <param name="body">Current responsive launcher body size.</param>
    /// <returns>False when there is no phone backend and the desktop install panel is configured.</returns>
    private bool ScaleAndroidInstallation(int body)
    {
        if (_phone == null) return false;
        StyleAndroidInstallation(_phone, body);
        return true;
    }

    /// <summary>Applies the shared drawer's readable text style to every hosted installation control.</summary>
    /// <param name="root">Installation control or one of its descendants.</param>
    /// <param name="fontSize">Current responsive launcher body size.</param>
    private void StyleAndroidInstallation(Control root, int fontSize)
    {
        root.AddThemeFontSizeOverride("font_size", fontSize);
        if (root is Label) root.AddThemeColorOverride("font_color", _infoStyle.Text);
        foreach (Node child in root.GetChildren())
            if (child is Control control) StyleAndroidInstallation(control, fontSize);
    }
}

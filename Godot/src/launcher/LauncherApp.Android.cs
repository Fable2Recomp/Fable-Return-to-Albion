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
        // Match the accepted Android defaults before the shared settings model fills
        // missing CVars. Existing user settings always keep their exact values.
        if (!File.Exists(Engine_AppRoot.ConfigPath))
            File.WriteAllText(Engine_AppRoot.ConfigPath, "max_fps = 0\nsun_shadow_map = 1024\nsun_shadow_splits = 2\nshader_warmup_enabled = false\nmaterials_modernized = false\n");
        LoadLauncherConfig();
        LauncherTheme.Load();
        // The mods that ship in the APK, packed into the mods folder before the Mods panel
        // first lists it, as on desktop. Installed switched off; never over a copy someone has edited.
        try
        {
            foreach (string line in AndroidBundledMods.Install(Engine_AppRoot.BundledModsDirectory,
                Refii.Assets.Engine_GameAssets.ModsRoot))
                GD.Print("[mods] bundled " + line);
        }
        catch (Exception error) { GD.PushError("[mods] bundled mods not installed: " + error.Message); }
        _patches = new Patches();
        _launch = new LaunchController(_patches);
        _updates = new UpdateChecker();
        BindNodes();
        _phone = new AndroidPhoneLauncher { Hosted = true, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill };
        _reinstallPanel.GetParent().AddChild(_phone);
        AndroidLauncherSettings.Prepare(Engine_AppRoot.Path, _phone.InstalledGameRoot);
        _font = LoadLauncherFont();
        BuildStyles();
        ApplyStyles();
        WireMenus();
        WireTabs();
        _settingsTabs.CurrentTab = _phone.HasPlayableInstall ? 0 : 2;
        _infoTabs.AddTab("Installation");
        _infoTabs.CurrentTab = 2;
        _settingsTitle.Text = "Launch options";
        Resized += ApplyLayout;
        ApplyLayout();
        RefreshState();
        RefreshDebugDrawer();
        CallDeferred(MethodName.PlayEntrance);
        _primaryButton.GrabFocus();
        FetchGitHubData();
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

namespace Refii.Tests.Launcher;

using System;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Game;
using Refii.Launcher.Core;
using Refii.Ui;

[TestSuite]
public class ControlsSettingsTests
{
    [TestCase]
    public void TestMouseSettingsExistInControlsCategory()
    {
        RuntimeCVarDefinition? mouseInput = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "mouse_input");
        AssertThat(mouseInput).IsNotNull();
        AssertThat(mouseInput!.Category).IsEqual(RuntimeCVarsGodot.ControlsCategory);
        AssertThat(mouseInput.Type).IsEqual(RuntimeCVarType.Bool);
        AssertThat(mouseInput.DefaultValue).IsEqual("true");

        RuntimeCVarDefinition? mouseLock = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "mouse_lock");
        AssertThat(mouseLock).IsNotNull();
        AssertThat(mouseLock!.Category).IsEqual(RuntimeCVarsGodot.ControlsCategory);
        AssertThat(mouseLock.Type).IsEqual(RuntimeCVarType.Bool);
        AssertThat(mouseLock.DefaultValue).IsEqual("true");
    }

    [TestCase]
    public void TestFormatDisplayNameForMouseSettings()
    {
        RuntimeCVarDefinition? mouseInput = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "mouse_input");
        AssertThat(mouseInput).IsNotNull();
        AssertThat(RuntimeSettingsModel.FormatDisplayName(mouseInput!)).IsEqual("Mouse input");

        RuntimeCVarDefinition? mouseLock = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "mouse_lock");
        AssertThat(mouseLock).IsNotNull();
        AssertThat(RuntimeSettingsModel.FormatDisplayName(mouseLock!)).IsEqual("Mouse lock");
    }

    [TestCase]
    public void TestMouseZoomSensitivityCVarAndScale()
    {
        RuntimeCVarDefinition? cvar = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "mouse_zoom_sensitivity");
        AssertThat(cvar).IsNotNull();
        AssertThat(cvar!.Category).IsEqual(RuntimeCVarsGodot.ControlsCategory);
        AssertThat(cvar.Type).IsEqual(RuntimeCVarType.Bool);
        AssertThat(cvar.DefaultValue).IsEqual("true");

        AssertThat(Refii.Game.GameInput.ZoomSensitivityScale(11.278f, enabled: false)).IsEqual(1f);
        float zoomed = Refii.Game.GameInput.ZoomSensitivityScale(11.278f, enabled: true);
        float wide = Refii.Game.GameInput.ZoomSensitivityScale(60f, enabled: true);
        AssertThat(zoomed).IsGreater(0f);
        AssertThat(zoomed).IsLess(wide);
    }
}

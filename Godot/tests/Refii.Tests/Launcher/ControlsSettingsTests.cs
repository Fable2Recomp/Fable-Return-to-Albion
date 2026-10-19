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
}

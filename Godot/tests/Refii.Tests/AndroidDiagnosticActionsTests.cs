using System;
using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks independent menu routing and scene ownership without requiring Godot GUI bindings.</summary>
[TestSuite]
public sealed class AndroidDiagnosticActionsTests
{
    /// <summary>F5 and F6 remain independent through repeated opening, closing and scene replacement.</summary>
    [TestCase]
    public void MenuSceneLifecycle()
    {
        bool perf = true, debug = false, keyboardCaptured = false;
        var actions = new AndroidDiagnosticActions(() => perf = !perf, () =>
        {
            debug = !debug;
            if (!debug) keyboardCaptured = false;
        });
        actions.ToggleDebug();
        keyboardCaptured = true;
        actions.TogglePerformance();
        AssertThat(debug).IsTrue();
        AssertThat(perf).IsFalse();
        actions.ToggleDebug();
        AssertThat(keyboardCaptured).IsFalse();
        AssertThat(debug).IsFalse();
        actions.Dispose();
        actions.ToggleDebug();
        actions.TogglePerformance();
        AssertThat(debug).IsFalse();
        AssertThat(perf).IsFalse();
        using var next = new AndroidDiagnosticActions(() => perf = !perf, () => debug = !debug);
        next.TogglePerformance();
        AssertThat(perf).IsTrue();
        actions.Dispose();
    }

    /// <summary>Rejects missing menu bindings before subscribing any UI callbacks.</summary>
    [TestCase]
    public void MissingBindingsRejected()
    {
        AssertThrown(() => new AndroidDiagnosticActions(null!, () => {})).IsInstanceOf<ArgumentNullException>();
        AssertThrown(() => new AndroidDiagnosticActions(() => {}, null!)).IsInstanceOf<ArgumentNullException>();
    }
}

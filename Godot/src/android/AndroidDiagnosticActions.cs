using System;

namespace Refii.AndroidRuntime;

/// <summary>Routes Android diagnostic taps directly to the existing menus without guest input.</summary>
public sealed class AndroidDiagnosticActions : IDisposable
{
    private Action? _performance;
    private Action? _debug;

    /// <summary>Binds the two menu actions for one game scene lifetime.</summary>
    /// <param name="performance">The existing F5 toggle.</param>
    /// <param name="debug">The existing F6 toggle.</param>
    public AndroidDiagnosticActions(Action performance, Action debug)
    {
        _performance = performance ?? throw new ArgumentNullException(nameof(performance));
        _debug = debug ?? throw new ArgumentNullException(nameof(debug));
    }

    /// <summary>Toggles F5 while this scene owns its menu bindings.</summary>
    public void TogglePerformance() => _performance?.Invoke();

    /// <summary>Toggles F6 while this scene owns its menu bindings.</summary>
    public void ToggleDebug() => _debug?.Invoke();

    /// <summary>Releases scene references and makes late UI callbacks harmless.</summary>
    public void Dispose() { _performance = null; _debug = null; }
}

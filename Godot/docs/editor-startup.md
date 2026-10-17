# Editor startup and shader imports

This project disables `editor/import/use_multiple_threads` in `project.godot`.
On Windows with Godot 4.7.1 .NET, the October 17, 2026 startup crash dump showed
an access violation on an import worker while `ResourceImporterShaderFile`
saved a compiled GLSL resource. Interrupted `.res*.tmp` files were present for
the changed post-processing shaders. Serial imports avoid that worker path;
this is a workaround, not a fix to Godot's native importer. It can increase
import time and does not change runtime rendering.

The bank-browser plugin also used to call `EditorFileSystem.Scan()` from
`_EnterTree()` after registering file extensions. During startup this re-entered
the first scan and plugin loading, producing duplicate progress tasks and
`thread.is_started()` errors. `EditorScanRequest` now coalesces that refresh and
dispatches it from a later editor frame only after scanning and importing stop.
Plugin unload cancels the request.

## Tests in editor builds

The runtime lighting lifecycle test needs a Godot .NET executable. Set
`GODOT_BIN` in the environment used to build, or copy `test.runsettings` to
`tests/Refii.Tests/test.local.runsettings` and add this section under
`RunSettings` (using the actual executable path):

```xml
<RunConfiguration>
  <EnvironmentVariables>
    <GODOT_BIN>C:\path\to\Godot_mono_win64.exe</GODOT_BIN>
  </EnvironmentVariables>
</RunConfiguration>
```

Keep the existing `GdUnit4` section so the runtime runs headlessly. The local
file is ignored by Git. An explicit `GODOT_BIN` takes precedence. The test
project selects these settings with MSBuild's `RunSettingsFilePath` property.
This permits builds launched from the editor to run the same tests as the CLI.

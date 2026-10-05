# Unit Testing & Automation Guide

This guide explains how to write, run, and maintain automated unit tests in the Refii C# codebase (`Godot/refii-launcher.sln`).

---

## 1. System Architecture & Overview

Refii does not follow traditional Godot game architecture. In our stack, Godot serves strictly as the **host engine**:
- **Host Engine (Godot / C#)**: Asset pipelines, GDB database management, VFS container mounting, audio streaming, Bink video decoding, CVars, settings, launcher UI, and modding tools.
- **Guest Engine (PPC Native)**: Gameplay loops, AI, physics, and world simulation.

Because host systems frequently interact with Godot types (`Vector2`, `Vector3`, `Color`, `Transform3D`, `Resource`, `RefCounted`, `Node`), standard .NET test runners (like vanilla xUnit) fail or throw unmanaged P/Invoke exceptions (`DllNotFoundException` on `godotsharp_string_new_with_utf16_chars`).

To provide universal testability across **all** host engine systems, Refii uses **GdUnit4Net** (`gdUnit4.api` + `gdUnit4.test.adapter`):
- **Universal**: Native support for pure C# logic AND Godot engine types.
- **Smart Runtime**: Pure logic tests run in milliseconds outside the engine; tests needing active Godot scenes or nodes run via `[RequireGodotRuntime]`.
- **Zero Vendor Blobs**: 100% managed via standard NuGet package references in `Godot/tests/Refii.Tests/Refii.Tests.csproj`. No third-party repository checkouts or vendor folders are committed to git.

---

## 2. Automated Build-Phase Execution

The test suite is wired directly into the **MSBuild compilation lifecycle**.

### How It Works:
1. Whenever you compile C# — whether by clicking the **Build Hammer** in the Godot Editor, pressing **Play (F5)**, or running `dotnet build` — MSBuild compiles `refii-launcher.csproj` and immediately runs the test suite.
2. In the Godot Editor, build diagnostics and test logs appear in the bottom panel under **MSBuild -> Output**.
3. When tests pass, the build succeeds in ~2.5s:
   ```
   ================= [RUNNING UNIT TESTS] =================
   Passed!  - Failed: 0, Passed: 21, Skipped: 0, Total: 21, Duration: 102 ms
   ========================================================
   Build succeeded.
   ```

### Strict Build Failure on Test Error:
- If any unit test fails, the build **aborts with an error** (`error MSB3073`).
- In the Godot Editor, the **MSBuild** panel immediately turns red and lists the failing test case, expected vs actual values, and source file line number under **Issues**, preventing broken code from running.
- In terminal CLI, .NET's Terminal Logger highlights the test failure in bright red with the stack trace.

### Temporary Bypass:
If you are doing rapid iteration and want to skip tests during compilation, set the environment variable:
```pwsh
$env:REFII_NO_TESTS = "1"
```
Or pass the property directly to MSBuild:
```pwsh
dotnet build Godot/refii-launcher.csproj -p:REFII_NO_TESTS=1
```

---

## 3. Running Tests from CLI & IDEs

### Running from Terminal:
```pwsh
# Run entire test suite:
dotnet test Godot/refii-launcher.sln

# Run test project directly with detailed output:
dotnet test Godot/tests/Refii.Tests/Refii.Tests.csproj --logger "console;verbosity=normal"

# Filter by test name or class:
dotnet test Godot/tests/Refii.Tests/Refii.Tests.csproj --filter "FullyQualifiedName~GdbSerialization"
```

### Running from IDE (Visual Studio, VS Code, JetBrains Rider):
Because `Refii.Tests.csproj` includes `gdUnit4.test.adapter`, your IDE's standard **Test Explorer** will automatically discover all test fixtures and allow you to run or debug individual tests with breakpoints.

---

## 4. Writing New Tests

All unit tests live in `Godot/tests/Refii.Tests/`. To create a new test fixture:

1. Create a `.cs` file in `Godot/tests/Refii.Tests/` (e.g., `AudioDecoderTests.cs`).
2. Add the `[TestSuite]` class attribute.
3. Add `[TestCase]` to test methods.
4. Use `using static GdUnit4.Assertions;` for fluent assertions.

### Example: Pure Logic & Host Systems
```csharp
namespace Refii.Tests;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Launcher.Core;

[TestSuite]
public class MyFeatureTests
{
    [TestCase]
    public void TestPathValidation()
    {
        bool safe = SafePath.IsSafeRelativePath("mods/mymod/files", out string error);
        AssertThat(safe).IsTrue();
        AssertThat(error).IsEqual(string.Empty);
    }

    [TestCase]
    public void TestVectorCalculations()
    {
        var v1 = new Vector2(10, 20);
        var v2 = new Vector2(5, 5);
        AssertThat(v1 + v2).IsEqual(new Vector2(15, 25));
    }
}
```

### Example: Testing with Godot Runtime (Nodes & Scenes)
If your test instantiates a Godot `Node`, requires scene tree signals, or loads Godot resources that depend on the active engine runtime, add the `[RequireGodotRuntime]` attribute:

```csharp
namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Godot;

[TestSuite]
[RequireGodotRuntime]
public class SceneComponentTests
{
    [TestCase]
    public void TestNodeHierarchy()
    {
        var parent = new Node3D();
        var child = new Node3D { Name = "CameraRig" };
        parent.AddChild(child);

        AssertThat(parent.GetChildCount()).IsEqual(1);
        AssertThat(parent.GetNode<Node3D>("CameraRig")).IsNotNull();

        parent.Free();
    }
}
```

### Common Assertions Reference:
- Equality: `AssertThat(val).IsEqual(expected);`
- Numeric approx: `AssertThat(floatVal).IsEqualApprox(12.0f, 0.001f);`
- Booleans: `AssertThat(flag).IsTrue();` / `AssertThat(flag).IsFalse();`
- Nullability: `AssertThat(obj).IsNotNull();` / `AssertThat(obj).IsNull();`
- Collections: `AssertThat(list.Count).IsEqual(3);` / `AssertThat(list).Contains(item);`

---

---

## 5. Directory Structure & Existing Test Suites

Tests are organized into subdirectories by subsystem under `Godot/tests/Refii.Tests/`:

```
Godot/tests/Refii.Tests/
├── Refii.Tests.csproj
├── Gdb/
│   ├── GdbSerializationTests.cs
│   ├── GdbLeafPromotionTests.cs
│   └── GdbMultiTests.cs
├── Modding/
│   ├── GdbCompositorTests.cs
│   ├── ModLoadOrderTests.cs
│   └── ModManifestTests.cs
└── Host/
    └── HostSystemsExampleTests.cs
```

| Subdirectory / Fixture | System Under Test | Description |
| :--- | :--- | :--- |
| **`Gdb/GdbSerializationTests.cs`** | `GdbWriter`, `GdbView`, `GdbPatchDatabase` | Validates `'GDB\0'` header magic, all 11 field types (`Int`, `Float`, `Bool`, `Reference`, `Enum`, etc.), authored string dictionaries, and FNV-1 alias tables. |
| **`Gdb/GdbLeafPromotionTests.cs`** | `GdbPatchBuilder`, `EditorModValueService` | Verifies sparse delta authoring (unmodified baseline fields are omitted), sibling record isolation (Toy Gun vs Standard Flintlock), and complete native synthesis. |
| **`Gdb/GdbMultiTests.cs`** | `GdbMulti` | Validates patch view overlay precedence, `TryFindInherited` fallback traversal, and `InheritedFields` cursor resolution. |
| **`Modding/GdbCompositorTests.cs`** | `GdbPatchCompositor` | Tests multi-mod priority merging, non-colliding property composition, disjoint record merging, and granular conflict detection. |
| **`Modding/ModLoadOrderTests.cs`** | `ModLoadOrder` | Validates reading/writing `loadorder.toml`, priority precedence, `[pins]` overrides, legacy `enabled.txt` migration, and path normalization. |
| **`Modding/ModManifestTests.cs`** | `ModManifest`, `ModWorkspace` | Validates asset enumeration (filtering out `.import` / metadata), `.gdb.tu` classification as `KindTu` (`tu`), and JSON manifest serialization round-trip. |
| **`Host/HostSystemsExampleTests.cs`** | `SafePath`, Godot math/types | Reference tests for path traversal protection and Godot vector / color operations. |

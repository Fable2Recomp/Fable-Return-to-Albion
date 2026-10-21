---
name: refii-testing
description: >-
  Use this skill when writing, maintaining, or running automated unit tests for Refii C#
  code under Godot/. Covers GdUnit4Net best practices, headless CLI execution, MSBuild
  integration, VFS/GDB mocking, and preventing unmanaged Godot access violations.
---

# Refii Automated C# Unit Testing Guide

This skill guides agents through writing and running unit tests in Refii using **GdUnit4Net** (`Godot/tests/Refii.Tests/`).

---

## 1. Mandatory Rule for C# Development

> [!IMPORTANT]
> **Every change or addition to C# code in `Godot/` must have automated tests.**
> Refii employs a **Two-Tier Testing Strategy**:
> 1. **Unit Tests**: Fast micro-tests for isolated classes, parsers, and serialization routines.
> 2. **Feature / Integration Tests**: End-to-end multi-subsystem tests (`ModdingFeatureTests.cs`) verifying full data lifecycles across mock VFS, multi-mod mounting, complete container synthesis (archetype inheritance), cache invalidation, and conflict reporting.

---

## 2. Test Architecture & Execution

### 2.1. MSBuild Build-Phase Integration
Tests are wired directly into MSBuild compilation:
- Building `refii-launcher.csproj` automatically compiles and executes all unit and feature tests via GdUnit4.
- **Fail on Test Error**: Any failing test will abort the build with `MSB3073`.
- In the Godot Editor, test diagnostics appear under the **MSBuild -> Output** bottom panel.
- In terminal CLI, run:
  ```pwsh
  dotnet test Godot/
  # or
  dotnet build Godot/refii-launcher.csproj
  ```

### 2.2. Fast Iteration Bypass
To temporarily skip test execution during rapid prototyping:
```pwsh
dotnet build Godot/refii-launcher.csproj -p:REFII_NO_TESTS=1
```

---

## 3. Writing Headless-Compatible Tests

Because `dotnet test` executes in a standalone .NET CLI process (`testhost.exe`) outside the Godot runtime engine:

### 3.1. Avoid Native Godot UI Controls
- **Never instantiate native Godot GUI controls** (`new Tree()`, `new Button()`, `new ItemList()`, `new Window()`) in test cases without `[RequireGodotRuntime]`.
- Attempting to instantiate them will fail with `System.AccessViolationException` or `NullReferenceException` inside unmanaged Godot C++ DLL bindings (`godotsharp_string_new_with_utf16_chars`).
- **Solution**: Decouple logic from UI widgets. Test against domain data models, state objects, or service classes instead of raw controls.

### 3.2. Guard Engine Logging in Library Code
When writing library methods called during tests:
- Do not call `GD.Print` or `GD.PushWarning` unconditionally.
- Use a safe log handler delegate or check for `testhost`:
  ```csharp
  public static Action<string>? LogHandler;

  private static void Log(string message)
  {
      if (LogHandler is not null)
      {
          LogHandler(message);
          return;
      }

      if (AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
          System.Environment.CommandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase))
      {
          return;
      }

      try { GD.Print(message); } catch { }
  }
  ```

---

## 4. Test Structure Patterns

### 4.1. Unit Test Pattern (Isolated Component)
```csharp
[TestSuite]
public class MyUnitTests
{
    [TestCase]
    public void TestSerialization()
    {
        var db = new GdbPatchDatabase();
        byte[] bytes = GdbWriter.Serialize(db);
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out _)).IsTrue();
        AssertThat(view.RecordCount).IsEqual(0);
    }
}
```

### 4.2. Feature Test Pattern (End-to-End Subsystem Pipeline)
```csharp
[TestSuite]
public class MyFeatureTests
{
    [TestCase]
    public void TestMultiModPipeline_E2E()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_feature_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Arrange: Mount base data into BnkFileSystem and bind resolver
            var fs = new BnkFileSystem();
            fs.AddVirtualFile("globals/globals.gdb", "base", baseGdb.Length, () => baseGdb);
            Engine_GameGdb.SetResolver(vpath => fs.TryGetFile(vpath, out var f) ? f.ReadAllBytes() : null);

            // 2. Author .f2mod packages
            F2ModPackage.Create(modAPath, metaA, filesA, out _);
            F2ModPackage.Create(modBPath, metaB, filesB, out _);

            // 3. Act: Apply compositors and overlays
            var installs = ModWorkspace.DiscoverModInstalls(tempDir);
            var overlays = ModIndex.ResolveOverlays(tempDir, order, installs);
            GdbPatchCompositor.Apply(fs, overlays, tempDir);
            ModRowOverlays.Apply(fs, overlays);

            // 4. Assert: Verify VFS, complete synthesis (parent/archetypes intact), and cache state
            AssertThat(fs.TryGetFile("globals/globals.gdb.tu", out BnkFile tuFile)).IsTrue();
        }
        finally
        {
            Engine_GameGdb.SetResolver(null);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
```

---

## 5. Verification Checklist

Before finishing any task:
1. Run `dotnet test Godot/`.
2. Confirm test suite outputs: `Passed! - Failed: 0, Passed: X, Skipped: 0`.
3. Ensure runtime execution speed is `< 1 second`.
4. Ensure cross-subsystem changes include an End-to-End Feature Test covering realistic lifecycle transitions.

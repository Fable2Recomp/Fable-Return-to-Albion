using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks diagnostic admission, registration and restore/reload/failure lifecycles without native nodes.</summary>
[TestSuite]
public sealed class ShaderCostDiagnosticTests
{
    /// <summary>Loads a resource from the checked-out Godot project.</summary>
    private static string Source(string relative, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative)))
            .Replace("\r\n", "\n");

    /// <summary>Exercises the same JSON policy as the live render channel.</summary>
    private static void Apply(Engine_ShaderCostDiagnostic owner, string json, Action<string, int> sink)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        owner.ApplyRequest(document.RootElement, sink);
    }

    /// <summary>Boot is normal; shader masks retain geometry and place coverage ahead of the model bypass.</summary>
    [TestCase]
    public void RegisteredDefaultsAndCoverageContract()
    {
        AssertThat(new Engine_ShaderCostDiagnostic().Mask).IsEqual(0);
        AssertThat(Source("project.godot").Contains("fable_shader_cost_diagnostic={\n\"type\": \"int\",\n\"value\": 0\n}")).IsTrue();
        string model = Source("src/Engine/material/standard_material_body.gdshaderinc");
        int modelDiagIdx = model.IndexOf("if (!should_discard && (fable_shader_cost_diagnostic & 2) == 0)");
        AssertThat(modelDiagIdx).IsGreaterEqual(0);
        AssertThat(model.IndexOf("should_discard = true;") < modelDiagIdx).IsTrue();
        AssertThat(model.Contains("global uniform int fable_shader_cost_diagnostic;")).IsFalse();
        AssertThat(Source("src/Engine/material/fable_lighting.gdshaderinc").Contains("global uniform int fable_shader_cost_diagnostic;")).IsTrue();
        AssertThat(Source("src/Engine/level/terrain/terrain_splat_body.gdshaderinc").Contains("if ((fable_shader_cost_diagnostic & 1) == 0)")).IsTrue();
        string canopy = Source("src/Engine/material/families/tree_canopy.gdshader");
        int canopyDiagIdx = canopy.IndexOf("if (!should_discard && (fable_shader_cost_diagnostic & 4) == 0)");
        AssertThat(canopyDiagIdx).IsGreaterEqual(0);
        AssertThat(canopy.IndexOf("bool should_discard =") < canopyDiagIdx).IsTrue();
        AssertThat(Source("src/Engine/material/fable_lighting.gdshaderinc").Contains("if ((fable_shader_cost_diagnostic & 8) != 0) { return vec4(0.0); }")).IsTrue();
    }

    /// <summary>Reads/reloads retain state, snapshots stay detached, exact restore and process restart reset it.</summary>
    [TestCase]
    public void ToggleReadRestoreAndNewProcessLifecycle()
    {
        var owner = new Engine_ShaderCostDiagnostic();
        var writes = new List<int>();
        Action<string, int> sink = (name, mask) => { AssertThat(name).IsEqual(Engine_ShaderCostDiagnostic.Global); writes.Add(mask); };
        foreach (int mask in new[] { 1, 2, 4, 8, 15, 3 }) Apply(owner, "{\"shaderCostDiagnostic\":" + mask + "}", sink);
        var snapshot = new { shaderCostDiagnostic = owner.Mask };
        Apply(owner, "{\"op\":\"render\"}", (_, _) => throw new Exception("read published"));
        AssertThat(owner.Mask).IsEqual(3);
        Apply(owner, "{\"shaderCostDiagnostic\":0}", sink);
        AssertThat(owner.Mask).IsEqual(0);
        AssertThat(snapshot.shaderCostDiagnostic).IsEqual(3);
        AssertThat(writes.Count).IsEqual(7);
        AssertThat(new Engine_ShaderCostDiagnostic().Mask).IsEqual(0);
    }

    /// <summary>Invalid values cannot cause a partial global write or change state.</summary>
    [TestCase]
    public void RejectMalformedBeforePublish()
    {
        foreach (string value in new[] { "null", "true", "\"1\"", "[]", "{}", "-1", "16", "1.5", "2147483648" })
        {
            var owner = new Engine_ShaderCostDiagnostic();
            bool rejected = false;
            int writes = 0;
            try { Apply(owner, "{\"shaderCostDiagnostic\":" + value + "}", (_, _) => writes++); }
            catch (JsonException) { rejected = true; }
            AssertThat(rejected).IsTrue();
            AssertThat(writes).IsEqual(0);
            AssertThat(owner.Mask).IsEqual(0);
        }
    }

    /// <summary>Publisher failure retains state and permits a successful retry and restoration.</summary>
    [TestCase]
    public void FailedPublisherCanRetry()
    {
        var owner = new Engine_ShaderCostDiagnostic();
        bool failed = false;
        try { Apply(owner, "{\"shaderCostDiagnostic\":2}", (_, _) => throw new InvalidOperationException()); }
        catch (InvalidOperationException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(owner.Mask).IsEqual(0);
        Apply(owner, "{\"shaderCostDiagnostic\":2}", (_, _) => { });
        AssertThat(owner.Mask).IsEqual(2);
        Apply(owner, "{\"shaderCostDiagnostic\":0}", (_, _) => { });
        AssertThat(owner.Mask).IsEqual(0);
    }
}

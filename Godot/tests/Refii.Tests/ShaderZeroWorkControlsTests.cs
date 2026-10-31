using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks startup registration, exact restoration and failed-publication lifecycles without Godot nodes.</summary>
[TestSuite]
public sealed class ShaderZeroWorkControlsTests
{
    /// <summary>Returns the checked-out project resource whose globals are loaded before any material.</summary>
    private static string Project([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../project.godot"));

    /// <summary>Applies a real JSON render request through the same pure policy used by the server.</summary>
    private static void Apply(Engine_ShaderZeroWorkControls controls, string json, Action<string, bool> sink)
    {
        using JsonDocument request = JsonDocument.Parse(json);
        controls.ApplyRequest(request.RootElement, sink);
    }

    /// <summary>All host defaults exactly match typed project globals and leave lighting debug at zero.</summary>
    [TestCase]
    public void RegisteredDefaultsMatchNewOwner()
    {
        var owner = new Engine_ShaderZeroWorkControls();
        AssertThat(owner.Specular).IsTrue();
        AssertThat(owner.Cliff).IsTrue();
        AssertThat(owner.NearFog).IsTrue();
        AssertThat(owner.SparseTerrain).IsTrue();
        string resource = File.ReadAllText(Project()).Replace("\r\n", "\n");
        foreach (string global in new[] { Engine_ShaderZeroWorkControls.SpecularGlobal,
            Engine_ShaderZeroWorkControls.CliffGlobal, Engine_ShaderZeroWorkControls.NearFogGlobal,
            Engine_ShaderZeroWorkControls.SparseTerrainGlobal })
        {
            string declaration = global + "={\n\"type\": \"bool\",\n\"value\": true\n}";
            AssertThat(resource.Contains(declaration, StringComparison.Ordinal)).IsTrue();
            AssertThat(resource.Split(global + "={", StringSplitOptions.None).Length).IsEqual(2);
        }
        AssertThat(resource.Contains("fable_light_debug={\n\"type\": \"int\",\n\"value\": 0\n}", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>False-to-true-to-false changes target only their own uniform and preserve a captured original.</summary>
    [TestCase]
    public void EachControlPublishesAndRestoresExactBoolean()
    {
        foreach ((string key, string global) in new[] {
            ("shaderZeroSpecular", Engine_ShaderZeroWorkControls.SpecularGlobal),
            ("shaderZeroCliff", Engine_ShaderZeroWorkControls.CliffGlobal),
            ("shaderNearFog", Engine_ShaderZeroWorkControls.NearFogGlobal),
            ("shaderSparseTerrain", Engine_ShaderZeroWorkControls.SparseTerrainGlobal) })
        {
            var owner = new Engine_ShaderZeroWorkControls();
            var writes = new List<(string, bool)>();
            bool original = true;
            foreach (bool value in new[] { false, true, false, original })
            {
                Apply(owner, JsonSerializer.Serialize(new Dictionary<string, object> { ["op"] = "render", [key] = value }),
                    (name, enabled) => writes.Add((name, enabled)));
                AssertThat(owner.Specular).IsEqual(key == "shaderZeroSpecular" ? value : true);
                AssertThat(owner.Cliff).IsEqual(key == "shaderZeroCliff" ? value : true);
                AssertThat(owner.NearFog).IsEqual(key == "shaderNearFog" ? value : true);
                AssertThat(owner.SparseTerrain).IsEqual(key == "shaderSparseTerrain" ? value : true);
                AssertThat(writes[^1] == (global, value)).IsTrue();
            }
            AssertThat(writes.Count).IsEqual(4);
        }
    }

    /// <summary>Ordinary report reads do not publish or reset overrides; a fresh process owner resets all defaults.</summary>
    [TestCase]
    public void ReadReloadAndNewProcessHaveDistinctLifetimes()
    {
        var owner = new Engine_ShaderZeroWorkControls();
        var globals = new Dictionary<string, bool>();
        Action<string, bool> sink = (name, value) => globals[name] = value;
        Apply(owner, "{\"shaderZeroCliff\":false}", sink);
        Apply(owner, "{\"shaderZeroSpecular\":false}", sink);
        Apply(owner, "{\"shaderNearFog\":false}", sink);
        Apply(owner, "{\"shaderSparseTerrain\":false}", sink);
        var captured = new { shaderZeroSpecular = owner.Specular, shaderZeroCliff = owner.Cliff, shaderNearFog = owner.NearFog, shaderSparseTerrain = owner.SparseTerrain };
        string snapshot = JsonSerializer.Serialize(captured);
        Apply(owner, "{\"op\":\"render\",\"shadowBudget\":0}", (_, _) => throw new Exception("read must not publish"));
        AssertThat(owner.Specular).IsFalse();
        AssertThat(owner.Cliff).IsFalse();
        AssertThat(owner.NearFog).IsFalse();
        AssertThat(owner.SparseTerrain).IsFalse();
        Apply(owner, "{\"shaderZeroCliff\":true}", sink);
        AssertThat(JsonSerializer.Serialize(captured)).IsEqual(snapshot);
        AssertThat(globals[Engine_ShaderZeroWorkControls.SpecularGlobal]).IsFalse();
        AssertThat(globals[Engine_ShaderZeroWorkControls.CliffGlobal]).IsTrue();
        AssertThat(globals[Engine_ShaderZeroWorkControls.NearFogGlobal]).IsFalse();
        AssertThat(globals[Engine_ShaderZeroWorkControls.SparseTerrainGlobal]).IsFalse();
        var restarted = new Engine_ShaderZeroWorkControls();
        AssertThat(restarted.Specular).IsTrue();
        AssertThat(restarted.Cliff).IsTrue();
        AssertThat(restarted.NearFog).IsTrue();
        AssertThat(restarted.SparseTerrain).IsTrue();
        AssertThat(owner.Specular).IsFalse();
    }

    /// <summary>Malformed/multiple controls cannot publish partially or change reported state.</summary>
    [TestCase]
    public void InvalidRequestsAreRejectedBeforeAnyWrite()
    {
        foreach (string json in new[] { "{\"shaderZeroSpecular\":null}", "{\"shaderZeroCliff\":1}",
            "{\"shaderZeroCliff\":\"false\"}", "{\"shaderZeroCliff\":[]}",
            "{\"shaderZeroSpecular\":false,\"shaderZeroCliff\":true}",
            "{\"shaderZeroSpecular\":false,\"shaderZeroCliff\":null}",
            "{\"shaderNearFog\":null}", "{\"shaderNearFog\":0}", "{\"shaderNearFog\":\"false\"}",
            "{\"shaderNearFog\":false,\"shaderZeroSpecular\":true}",
            "{\"shaderNearFog\":true,\"shaderZeroCliff\":false}",
            "{\"shaderNearFog\":false,\"shaderZeroSpecular\":false,\"shaderZeroCliff\":false}",
            "{\"shaderSparseTerrain\":null}", "{\"shaderSparseTerrain\":1}",
            "{\"shaderSparseTerrain\":\"false\"}", "{\"shaderSparseTerrain\":[]}",
            "{\"shaderSparseTerrain\":false,\"shaderZeroSpecular\":true}",
            "{\"shaderSparseTerrain\":true,\"shaderZeroCliff\":false}",
            "{\"shaderSparseTerrain\":false,\"shaderNearFog\":true}",
            "{\"shaderSparseTerrain\":false,\"shaderNearFog\":false,\"shaderZeroSpecular\":false,\"shaderZeroCliff\":false}" })
        {
            var owner = new Engine_ShaderZeroWorkControls();
            int writes = 0;
            bool rejected = false;
            try { Apply(owner, json, (_, _) => writes++); }
            catch (JsonException) { rejected = true; }
            AssertThat(rejected).IsTrue();
            AssertThat(writes).IsEqual(0);
            AssertThat(owner.Specular).IsTrue();
            AssertThat(owner.Cliff).IsTrue();
            AssertThat(owner.NearFog).IsTrue();
            AssertThat(owner.SparseTerrain).IsTrue();
        }
    }

    /// <summary>A failed publisher cannot claim a global changed; retry can complete the same request.</summary>
    [TestCase]
    public void PublisherFailurePreservesStateAndRetrySucceeds()
    {
        var owner = new Engine_ShaderZeroWorkControls();
        bool failed = false;
        try { Apply(owner, "{\"shaderZeroSpecular\":false}", (_, _) => throw new InvalidOperationException("sink")); }
        catch (InvalidOperationException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(owner.Specular).IsTrue();
        Apply(owner, "{\"shaderZeroSpecular\":false}", (_, _) => { });
        AssertThat(owner.Specular).IsFalse();
        failed = false;
        try { Apply(owner, "{\"shaderNearFog\":false}", (_, _) => throw new InvalidOperationException("sink")); }
        catch (InvalidOperationException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(owner.NearFog).IsTrue();
        Apply(owner, "{\"shaderNearFog\":false}", (_, _) => { });
        AssertThat(owner.NearFog).IsFalse();
        failed = false;
        try { Apply(owner, "{\"shaderSparseTerrain\":false}", (_, _) => throw new InvalidOperationException("sink")); }
        catch (InvalidOperationException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(owner.SparseTerrain).IsTrue();
        Apply(owner, "{\"shaderSparseTerrain\":false}", (_, _) => { });
        AssertThat(owner.SparseTerrain).IsFalse();
        bool missingSink = false;
        try { Apply(owner, "{}", null!); }
        catch (ArgumentNullException) { missingSink = true; }
        AssertThat(missingSink).IsTrue();
    }
}

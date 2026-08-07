using System.Collections.Generic;
using System.Globalization;

namespace Refii.Launcher.Core;

public sealed class Patch
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = "Patch loaded from config";
    public string Type { get; set; } = "bool";
    public bool Enabled { get; set; }
    public int IntValue { get; set; }
}

public sealed class Patches
{
    private readonly List<Patch> _patches = new();
    private bool _loaded;

    public IReadOnlyList<Patch> Items
    {
        get
        {
            EnsurePatchStateLoaded();
            return _patches;
        }
    }

    public bool HasPatches => Items.Count > 0;

    public void EnsurePatchStateLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        LoadPatchesFromIni();
    }

    public void Init(string? onlineResponse)
    {
        _patches.Clear();
        _loaded = true;

        if (!string.IsNullOrEmpty(onlineResponse))
        {
            List<Patch> parsed = PatchSource.ParseResponse(onlineResponse!);
            if (parsed.Count != 0)
            {
                _patches.AddRange(parsed);
                return;
            }
        }

        LoadPatchesFromIni();
    }

    private void LoadPatchesFromIni()
    {
        Ini.Load();
        foreach ((string name, string rawValue) in Ini.GetSection("Patches"))
        {
            string description = Ini.GetString(name + "_desc", "Patch loaded from config", "PatchDescriptions");
            string type = Ini.GetString(name + "_type", "bool", "PatchTypes");

            var patch = new Patch
            {
                Name = name,
                Description = description,
                Type = type,
            };

            if (type == "int")
            {
                patch.IntValue = int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : 0;
            }
            else
            {
                string lowered = rawValue.ToLowerInvariant();
                patch.Enabled = lowered is "1" or "true" or "yes" or "on";
            }

            _patches.Add(patch);
        }
    }

    public List<string> GetPatchLaunchArguments()
    {
        EnsurePatchStateLoaded();

        var arguments = new List<string>();
        foreach (Patch patch in _patches)
        {
            if (patch.Type == "bool")
            {
                arguments.Add("--" + patch.Name + "=" + (patch.Enabled ? "true" : "false"));
                Ini.SetBool(patch.Name, patch.Enabled, "Patches");
            }
            else if (patch.Type == "int")
            {
                arguments.Add("--" + patch.Name + "=" + patch.IntValue.ToString(CultureInfo.InvariantCulture));
                Ini.SetInt(patch.Name, patch.IntValue, "Patches");
            }

            Ini.SetString(patch.Name + "_desc", patch.Description, "PatchDescriptions");
        }

        return arguments;
    }

    public string GetPatchLaunchConfig()
    {
        var config = string.Empty;
        foreach (string argument in GetPatchLaunchArguments())
        {
            config += " " + argument;
        }

        return config;
    }
}

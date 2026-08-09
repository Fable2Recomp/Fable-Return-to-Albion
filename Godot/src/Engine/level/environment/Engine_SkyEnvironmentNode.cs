using System.Collections.Generic;
using Godot;
using Refii.Lighting;
using Refii.Models;

namespace Refii.Level;

[Tool]
public partial class Engine_SkyEnvironmentNode : WorldEnvironment
{
    private float _dayFraction = 0.5f;

    [Export(PropertyHint.Range, "0,24,0.05,suffix:h")]
    public float TimeOfDay
    {
        get => _dayFraction * 24f;
        set
        {
            _dayFraction = value / 24f;
            if (IsNodeReady())
            {
                Reevaluate();
            }
        }
    }

    [Export]
    public string LevelGdbPath { get; set; } = string.Empty;

    public SkyTheme? Theme { get; set; }

    public List<byte[]> LightingGdbs { get; } = new();

    public DirectionalLight3D? Sun { get; set; }

    public void Initialise(SkyTheme theme, float dayFraction, string levelGdbPath)
    {
        Theme = theme;
        _dayFraction = dayFraction;
        LevelGdbPath = levelGdbPath;
    }

    public void Reevaluate()
    {
        if (!EnsureTheme() || Theme is null)
        {
            return;
        }

        if (LightingGdbs.Count != 0 &&
            Engine_EnvironmentTheme.TryExtractSky(
                LightingGdbs, _dayFraction, out SkyTheme timeTheme))
        {
            Theme = timeTheme;
        }

        SkyFrame frame = Engine_SkyFrame.Evaluate(Theme, _dayFraction);

        if (Environment?.Sky?.SkyMaterial is ShaderMaterial material)
        {
            Engine_SkyEnvironment.ApplyFrame(material, frame);
        }

        Node3D? celestial = GetParent()?.GetNodeOrNull<Node3D>(
            Engine_CelestialElements.NodeName);
        if (celestial is not null)
        {
            Engine_CelestialElements.Update(celestial, Theme, frame);
        }

        if (LightingGdbs.Count == 0 ||
            !Engine_LightingTheme.TryExtract(
                LightingGdbs, _dayFraction, out LightingTheme lightingTheme))
        {
            return;
        }

        LightingFrame lighting = Engine_LightingFrame.Evaluate(Theme, lightingTheme, frame);

        if (Environment is not null)
        {
            Engine_LevelLighting.ConfigureEnvironment(Environment, lighting);
        }

        DirectionalLight3D? sun = ResolveSun();
        if (sun is not null)
        {
            Engine_LevelLighting.UpdateSun(sun, lighting);
        }
    }

    private bool EnsureTheme()
    {
        if (Theme is not null && LightingGdbs.Count != 0)
        {
            return true;
        }

        List<byte[]> chain = Engine_SkyEnvironment.BuildGdbChain(
            LevelGdbPath, Engine_BnkMeshLoader.ReadBytes);
        if (chain.Count == 0)
        {
            return Theme is not null;
        }

        if (Theme is null)
        {
            if (!Engine_EnvironmentTheme.TryExtractSky(chain, out SkyTheme sky))
            {
                return false;
            }

            Theme = sky;
        }

        if (LightingGdbs.Count == 0)
        {
            LightingGdbs.AddRange(chain);
        }

        return true;
    }

    private DirectionalLight3D? ResolveSun()
    {
        if (Sun is not null && IsInstanceValid(Sun))
        {
            return Sun;
        }

        Sun = GetParent()?.GetNodeOrNull<DirectionalLight3D>(
            Engine_LevelLighting.SunNodeName);
        return Sun;
    }
}

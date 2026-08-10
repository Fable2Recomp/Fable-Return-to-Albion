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

    /// <summary>
    /// The region's lighting for the current time of day, or null before the first evaluation.
    ///
    /// Everything in the region that has to be lit reads it from here — the prop streamer asks
    /// for it as each mesh arrives. One node owns the answer, so the editor and the game cannot
    /// end up with two.
    /// </summary>
    public LightingFrame? Lighting { get; private set; }

    /// <summary>
    /// The environment node for the world currently standing, or null before one is loaded.
    ///
    /// Godot allows one environment per World3D and Refii loads one region at a time, so this
    /// mirrors a real singleton rather than inventing one. It exists because the things that
    /// need the lighting are not all INSIDE the region: <c>GameEntities</c> hangs beside the
    /// region under <c>GameWorld</c>, carrying the guest's live characters, and a sibling
    /// cannot reach the region's environment by walking its own parents.
    ///
    /// If a second region is ever mounted at the same time, this becomes the wrong answer for
    /// one of them and the lookup has to become per-World3D.
    /// </summary>
    public static Engine_SkyEnvironmentNode? Current { get; private set; }

    /// <summary>
    /// Evaluate as the region stands up, in the EDITOR as well as in the game.
    ///
    /// The scene is saved with the sun and the environment already correct, but nothing
    /// standing in it carries any lighting: props are packed meshless and their instance
    /// parameters cannot be serialised. So the first evaluation is not a refresh, it is the
    /// only time the region is lit at all.
    ///
    /// It RETRIES, because the editor can lose the race. The theme is read back out of the
    /// banks through <c>Engine_BnkMeshLoader</c>, the addon mounts them asynchronously, and a
    /// level scene reopened at editor start arrives first. One attempt then finds no GDB, gives
    /// up, and the region stays on its defaults for the rest of the session with nothing said —
    /// which is exactly the kind of silent editor-only difference this is meant to remove.
    /// </summary>
    public override void _Ready()
    {
        Current = this;
        Reevaluate();
        SetProcess(Lighting is null);
    }

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }
    }

    public override void _Process(double delta)
    {
        if (Lighting is not null || ++_attempts > MountAttempts)
        {
            SetProcess(false);
            if (Lighting is null)
            {
                GD.Print($"[lighting] {Name}: no environment theme after {MountAttempts} " +
                    "frames; the region is on shader defaults");
            }

            return;
        }

        Reevaluate();
    }

    /// <summary>Frames to keep asking for the theme. Generous — a cold bank mount is slow.</summary>
    private const int MountAttempts = 600;

    private int _attempts;

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
        Lighting = lighting;

        if (Environment is not null)
        {
            Engine_LevelLighting.ConfigureEnvironment(Environment, lighting);
        }

        DirectionalLight3D? sun = ResolveSun();
        if (sun is not null)
        {
            Engine_LevelLighting.UpdateSun(sun, lighting);
        }

        // The region and everything mounted ALONGSIDE it, not just the sun and the sky.
        //
        // Everything standing in the world carries the ambient of the moment it was lit, and
        // that moves with the sun. Refreshing only the region misses the live guest entities:
        // GameWorld hangs them beside the region so they can carry the guest-to-Godot map
        // themselves, exactly as the camera does, so they are cousins rather than descendants.
        // One level up covers both, and in the editor it simply reaches the scene the region
        // was opened in.
        if (GetParent() is Node region)
        {
            Engine_LevelBakedLighting.RefreshInstanceLighting(
                region.GetParent() ?? region, lighting);
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

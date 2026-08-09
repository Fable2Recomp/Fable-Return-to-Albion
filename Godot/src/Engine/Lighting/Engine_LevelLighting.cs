using System;
using Godot;
using Refii.Level;

namespace Refii.Lighting;

public static class Engine_LevelLighting
{
    public const string SunNodeName = "Sun";

    public const float SunNearHalfExtent = 20.0f;

    public const float SunNearDepth = 80.0f;

    public const float SunFarHalfExtent = 50.0f;

    public const float SunFarDepth = 120.0f;

    public const float CascadeSplitNear = 35.0f;

    public const float CascadeSplitFar = 18.0f;

    public const float NearDepthBias = 0.001f;

    public const float NearSlopeBias = 2.0f;

    public const float FarDepthBias = 0.005f;

    public const float FarSlopeBias = 5.0f;

    public static DirectionalLight3D BuildSun(LightingFrame frame)
    {
        var light = new DirectionalLight3D
        {
            Name = SunNodeName,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = SunFarDepth,
            DirectionalShadowSplit1 = CascadeSplitNear / SunFarDepth,
            ShadowNormalBias = NearSlopeBias,
            ShadowBias = 0.03f,
        };

        UpdateSun(light, frame);
        return light;
    }

    public static void UpdateSun(DirectionalLight3D light, LightingFrame frame)
    {
        light.LightColor = new Color(frame.SunColour[0], frame.SunColour[1], frame.SunColour[2]);
        light.LightEnergy = frame.SunEnergy;
        light.ShadowEnabled = frame.SunShadow;
        light.Transform = new Transform3D(SunBasis(frame.SunDirection), Vector3.Zero);
    }

    public static void ConfigureEnvironment(Godot.Environment environment, LightingFrame frame)
    {
        if (environment is null)
        {
            return;
        }

        if (frame.HasAmbient)
        {
            environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            environment.AmbientLightColor = new Color(
                frame.AmbientColour[0], frame.AmbientColour[1], frame.AmbientColour[2]);
            environment.AmbientLightEnergy = frame.AmbientEnergy;
        }

        if (frame.HasFog)
        {
            environment.FogEnabled = true;
            environment.FogMode = Godot.Environment.FogModeEnum.Depth;
            environment.FogDepthBegin = frame.FogBegin;
            environment.FogDepthEnd = frame.FogEnd;
            environment.FogLightColor = new Color(
                frame.FogColour[0], frame.FogColour[1], frame.FogColour[2]);
        }
    }

    private static Basis SunBasis(float[] direction)
    {
        var forward = new Vector3(-direction[0], -direction[1], -direction[2]);
        if (forward.LengthSquared() < 1e-8f)
        {
            forward = Vector3.Down;
        }

        forward = forward.Normalized();
        Vector3 up = MathF.Abs(forward.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        return Basis.LookingAt(forward, up);
    }
}

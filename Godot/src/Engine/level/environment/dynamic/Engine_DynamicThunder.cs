using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public readonly record struct DynamicLightningStrike(Vector3 Direction, float Strength, float DistanceMetres)
{
    public float DelaySeconds => Math.Clamp(DistanceMetres / 343f, 0.25f, 8f);
}

public partial class Engine_DynamicThunder : Node
{
    private readonly List<(DynamicLightningStrike Strike, float Remaining)> _pending = new();
    private readonly List<AudioStreamPlayer3D> _voices = new();

    /// <summary>Each voice's last strike gain, by its index in <see cref="_voices"/>; 0 until it has played.</summary>
    private readonly List<float> _gains = new();
    private readonly List<AudioStream> _clips = new();
    private Engine_DynamicSkyDriver? _driver;
    private int _played;
    private float _sfx = 1f;

    public int PendingCount => _pending.Count;
    public int PlayedCount => _played;

    public override void _Ready()
    {
        foreach (string file in new[] { "thunder-clap.mp3", "thunder-roll.mp3" })
        {
            string path = "res://assets/dynamic_sky/" + file;
            if (ResourceLoader.Exists(path) && GD.Load<AudioStream>(path) is { } clip) _clips.Add(clip);
        }
        for (int i = 0; i < 4; i++)
        {
            var voice = new AudioStreamPlayer3D
            {
                Name = "Thunder" + i,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
                PanningStrength = 0.55f,
            };
            AddChild(voice);
            voice.TopLevel = true;
            _voices.Add(voice);
            _gains.Add(0f);
        }
    }

    public void Bind(Engine_DynamicSkyDriver? driver)
    {
        if (ReferenceEquals(_driver, driver)) return;
        if (_driver is not null) _driver.LightningStruck -= Queue;
        _driver = driver;
        _pending.Clear();
        foreach (var voice in _voices) voice.Stop();
        if (_driver is not null) _driver.LightningStruck += Queue;
    }

    public void Queue(DynamicLightningStrike strike)
    {
        if (_pending.Count == 4) _pending.RemoveAt(0);
        _pending.Add((strike, strike.DelaySeconds));
    }

    public void Advance(float delta, float exposure = 1f, float sfxVolume = 1f)
    {
        if (!float.IsFinite(delta) || delta <= 0f || Engine.IsEditorHint()) return;
        _sfx = float.IsFinite(sfxVolume) ? Math.Clamp(sfxVolume, 0f, 1f) : _sfx;
        // Each voice's gain is kept beside it: HasMeta/GetMeta by a string literal made two
        // finalizable StringNames per voice per frame.
        for (int v = 0; v < _voices.Count; v++)
            if (_gains[v] > 0f) _voices[v].VolumeDb = GainDb(_gains[v], exposure);
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            item.Remaining -= delta;
            if (item.Remaining > 0f) { _pending[i] = item; continue; }
            _pending.RemoveAt(i);
            if (_clips.Count == 0 || _voices.Count == 0) continue;
            int slot = _played % _voices.Count;
            var voice = _voices[slot];
            voice.Stream = _clips[_played % _clips.Count];
            voice.PitchScale = 0.9f + (float)Random.Shared.NextDouble() * 0.18f;
            float gain = Math.Clamp(item.Strike.Strength, 0.15f, 1f) *
                Mathf.Lerp(0.65f, 0.3f, Math.Clamp(item.Strike.DistanceMetres / 2500f, 0f, 1f));
            _gains[slot] = gain;
            voice.VolumeDb = GainDb(gain, exposure);
            Vector3 camera = GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
            voice.GlobalPosition = camera + item.Strike.Direction.Normalized() * item.Strike.DistanceMetres;
            voice.Play(_played % _clips.Count == 0 ? 0.32f : 0.12f);
            _played++;
        }
    }

    private float GainDb(float gain, float exposure) =>
        Mathf.LinearToDb(MathF.Max(0.00001f, gain * _sfx * Mathf.Lerp(0.3f, 1f, Math.Clamp(exposure, 0f, 1f))));

    public override void _ExitTree() => Bind(null);
}

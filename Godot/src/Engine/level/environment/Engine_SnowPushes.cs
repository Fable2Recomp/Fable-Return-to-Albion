using Godot;
using Refii.Host;

namespace Refii.Level;

/// <summary>
/// The spells' pushes on the modern snow (dynamic_precip.gdshaderinc): the eight slots the guest's
/// weather primitive holds (header [8..79] of the weather export, Engine_ClassicWeather.StatePushes)
/// and its clock (header [4]), as a small texture and a global the snow reads.
/// </summary>
public static class Engine_SnowPushes
{
    private const int Texels = 18;

    private static readonly StringName PushesName = "fable_snow_pushes";
    private static readonly StringName PushName = "fable_snow_push";

    private static readonly float[] _weather = new float[HostBridge.ClassicWeatherHeader + 64 * 8];
    private static readonly float[] _held = new float[Texels * 4];
    private static readonly byte[] _bytes = new byte[Texels * 16];
    private static Image? _image;
    private static ImageTexture? _texture;
    private static bool _on;

    /// <summary>Publishes the slots when they change and the clock every frame. Main thread, once a frame.</summary>
    public static void Pump()
    {
        bool live = HostBridge.GetClassicWeather(_weather) >= HostBridge.ClassicWeatherHeader;
        if (!live)
        {
            if (_on)
            {
                _on = false;
                RenderingServer.GlobalShaderParameterSet(PushName, Vector2.Zero);
            }
            return;
        }
        bool changed = _texture is null;
        for (int i = 0; i < _held.Length; i++)
        {
            float v = _weather[8 + i];
            changed |= v != _held[i];
            _held[i] = v;
        }
        if (changed)
        {
            System.Buffer.BlockCopy(_held, 0, _bytes, 0, _bytes.Length);
            if (_image is null)
            {
                _image = Image.CreateFromData(Texels, 1, false, Image.Format.Rgbaf, _bytes);
                _texture = ImageTexture.CreateFromImage(_image);
                RenderingServer.GlobalShaderParameterSet(PushesName, _texture);
            }
            else
            {
                _image.SetData(Texels, 1, false, Image.Format.Rgbaf, _bytes);
                _texture!.Update(_image);
            }
        }
        _on = true;
        RenderingServer.GlobalShaderParameterSet(PushName, new Vector2(_weather[4], 1f));
    }
}

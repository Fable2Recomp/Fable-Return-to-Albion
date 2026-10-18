using Godot;

namespace Refii.Level;

/// <summary>
/// The guest's roofs (<see cref="Engine_ClassicRoofs"/>) for the modern rain and snow: the nearest
/// ones around the eye in a small texture, read by fable_roofs.gdshaderinc, so no drop falls under
/// any building's roof — the title's cut, where the modern weather once kept only the hero's
/// building dry.
/// </summary>
public static class Engine_RoofCut
{
    /// <summary>Roofs the shaders test, nearest first after the one over the eye.</summary>
    private const int Slots = 32;

    /// <summary>The query box around the eye: the weather volumes' reach.</summary>
    private const float Reach = 60f;

    private static readonly StringName RoofsName = "fable_roofs";
    private static readonly StringName CountName = "fable_roof_count";

    private static readonly float[] _texels = new float[Slots * 3 * 4];
    private static readonly float[] _held = new float[Slots * 3 * 4];
    private static readonly byte[] _bytes = new byte[Slots * 3 * 16];
    private static Image? _image;
    private static ImageTexture? _texture;
    private static int _count = -1;
    private static Vector3 _lastEyeGodot = new(float.NaN, float.NaN, float.NaN);
    private static int _lastVersion = -1;

    /// <summary>Builds the roof list around the eye and publishes it when it changed. Main thread, once a frame.</summary>
    public static void Pump(Vector3 eyeGodot)
    {
        if (_texture is not null && _lastVersion == Engine_ClassicRoofs.Version && eyeGodot.DistanceSquaredTo(_lastEyeGodot) < 0.04f)
        {
            return;
        }
        _lastEyeGodot = eyeGodot;
        var eye = new Vector3(-eyeGodot.X, eyeGodot.Z, eyeGodot.Y);
        var reach = new Vector3(Reach, Reach, Reach);
        Engine_ClassicRoofs.Build(eye - reach, eye + reach, eye);
        _lastVersion = Engine_ClassicRoofs.Version;
        int count = System.Math.Min(Engine_ClassicRoofs.Roofs.Count, Slots);
        System.Array.Clear(_texels);
        for (int i = 0; i < count; i++)
        {
            Engine_ClassicRoofs.Roof r = Engine_ClassicRoofs.Roofs[i];
            int at = i * 12;
            _texels[at] = r.C0.X; _texels[at + 1] = r.C0.Y; _texels[at + 2] = r.C1.X; _texels[at + 3] = r.C1.Y;
            _texels[at + 4] = r.C2.X; _texels[at + 5] = r.C2.Y; _texels[at + 6] = r.C3.X; _texels[at + 7] = r.C3.Y;
            _texels[at + 8] = r.Top;
        }
        if (_texture is not null && count == _count && System.MemoryExtensions.SequenceEqual<float>(_texels, _held))
        {
            return;
        }
        System.Array.Copy(_texels, _held, _texels.Length);
        System.Buffer.BlockCopy(_texels, 0, _bytes, 0, _bytes.Length);
        if (_image is null)
        {
            _image = Image.CreateFromData(Slots * 3, 1, false, Image.Format.Rgbaf, _bytes);
            _texture = ImageTexture.CreateFromImage(_image);
            RenderingServer.GlobalShaderParameterSet(RoofsName, _texture);
        }
        else
        {
            _image.SetData(Slots * 3, 1, false, Image.Format.Rgbaf, _bytes);
            _texture!.Update(_image);
        }
        if (count != _count)
        {
            _count = count;
            RenderingServer.GlobalShaderParameterSet(CountName, count);
        }
    }
}

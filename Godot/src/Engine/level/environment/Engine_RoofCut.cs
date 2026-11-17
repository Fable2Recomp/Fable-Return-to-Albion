using Godot;

namespace Refii.Level;

/// <summary>
/// The guest's roofs (<see cref="Engine_ClassicRoofs"/>) for the modern rain and snow: the nearest
/// ones around the eye in a small texture, read by fable_roofs.gdshaderinc, so no drop falls under
/// any building's roof — the title's cut, where the modern weather once kept only the hero's
/// building dry.
/// </summary>
/// <remarks>
/// A ROOF IS A ROOF IN EVERY VIEW. The list is world geometry the drops are tested against, not
/// a view's state, so on split screen it is the union of the roofs around each player's eye
/// (each eye's own list, the second's after the first's, without repeats): a drop under any
/// listed roof is dry in whichever view draws it. Each eye keeps its own budget of slots.
/// </remarks>
public static class Engine_RoofCut
{
    /// <summary>Roofs the shaders test around one eye, nearest first after the one over the eye.</summary>
    private const int Slots = 32;

    /// <summary>The texture's capacity: a budget of <see cref="Slots"/> per view.</summary>
    private const int MaxSlots = Slots * Refii.Models.Engine_ClassicViews.Max;

    /// <summary>The query box around the eye: the weather volumes' reach.</summary>
    private const float Reach = 60f;

    private static readonly StringName RoofsName = "fable_roofs";
    private static readonly StringName CountName = "fable_roof_count";

    private static readonly float[] _texels = new float[MaxSlots * 3 * 4];
    private static readonly float[] _held = new float[MaxSlots * 3 * 4];
    private static readonly byte[] _bytes = new byte[MaxSlots * 3 * 16];
    private static readonly System.Collections.Generic.List<Engine_ClassicRoofs.Roof> _first = new(Slots);
    private static Vector3 _lastSecondGodot = new(float.NaN, float.NaN, float.NaN);
    private static ImageTexture? _texture;
    private static int _count = -1;
    private static Vector3 _lastEyeGodot = new(float.NaN, float.NaN, float.NaN);
    private static int _lastVersion = -1;

    /// <summary>Builds the roof list around the eye and publishes it when it changed. Main thread, once a frame.</summary>
    public static void Pump(Vector3 eyeGodot) => Pump(eyeGodot, null);

    /// <summary>
    /// As <see cref="Pump(Vector3)"/> around each view's eye: <paramref name="secondGodot"/> is
    /// split screen's second eye, null with one view. Main thread, once a frame.
    /// </summary>
    public static void Pump(Vector3 eyeGodot, Vector3? secondGodot)
    {
        Vector3 second = secondGodot ?? new Vector3(float.NaN, float.NaN, float.NaN);
        bool secondStill = secondGodot is null
            ? float.IsNaN(_lastSecondGodot.X)
            : second.DistanceSquaredTo(_lastSecondGodot) < 0.04f;
        if (_texture is not null && _lastVersion == Engine_ClassicRoofs.Version && eyeGodot.DistanceSquaredTo(_lastEyeGodot) < 0.04f && secondStill)
        {
            return;
        }
        _lastEyeGodot = eyeGodot;
        _lastSecondGodot = second;
        System.Array.Clear(_texels);
        int count = Around(eyeGodot, 0);
        if (secondGodot is { } other)
        {
            // The first eye's roofs, to leave out of the second's.
            _first.Clear();
            for (int i = 0; i < count; i++)
            {
                _first.Add(Engine_ClassicRoofs.Roofs[i]);
            }
            count = Around(other, count);
        }
        _lastVersion = Engine_ClassicRoofs.Version;
        if (_texture is not null && count == _count && System.MemoryExtensions.SequenceEqual<float>(_texels, _held))
        {
            return;
        }
        System.Array.Copy(_texels, _held, _texels.Length);
        System.Buffer.BlockCopy(_texels, 0, _bytes, 0, _bytes.Length);
        if (_texture is null)
        {
            var image = Image.CreateFromData(MaxSlots * 3, 1, false, Image.Format.Rgbaf, _bytes);
            _texture = Refii.Textures.Engine_TextureUpload.CreateTexture(image);
            RenderingServer.GlobalShaderParameterSet(RoofsName, _texture);
        }
        else
        {
            Refii.Textures.Engine_TextureUpload.UpdateFromData(
                _texture, MaxSlots * 3, 1, false, Image.Format.Rgbaf, _bytes);
        }
        if (count != _count)
        {
            _count = count;
            RenderingServer.GlobalShaderParameterSet(CountName, count);
        }
    }

    /// <summary>
    /// The roofs around one eye into the texels from slot <paramref name="from"/>, up to
    /// <see cref="Slots"/> of them, past the ones the first eye listed; the slots now used.
    /// </summary>
    private static int Around(Vector3 eyeGodot, int from)
    {
        var eye = new Vector3(-eyeGodot.X, eyeGodot.Z, eyeGodot.Y);
        var reach = new Vector3(Reach, Reach, Reach);
        Engine_ClassicRoofs.Build(eye - reach, eye + reach, eye);
        int count = from;
        int taken = 0;
        foreach (Engine_ClassicRoofs.Roof r in Engine_ClassicRoofs.Roofs)
        {
            if (taken >= Slots || count >= MaxSlots)
            {
                break;
            }
            if (from > 0 && Listed(r))
            {
                continue;
            }
            int at = count * 12;
            _texels[at] = r.C0.X; _texels[at + 1] = r.C0.Y; _texels[at + 2] = r.C1.X; _texels[at + 3] = r.C1.Y;
            _texels[at + 4] = r.C2.X; _texels[at + 5] = r.C2.Y; _texels[at + 6] = r.C3.X; _texels[at + 7] = r.C3.Y;
            _texels[at + 8] = r.Top;
            ++count;
            ++taken;
        }
        return count;
    }

    private static bool Listed(in Engine_ClassicRoofs.Roof roof)
    {
        foreach (Engine_ClassicRoofs.Roof r in _first)
        {
            if (r.C0 == roof.C0 && r.C2 == roof.C2 && r.Top == roof.Top)
            {
                return true;
            }
        }
        return false;
    }
}

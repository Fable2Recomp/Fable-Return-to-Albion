using System;
using Godot;

namespace Refii.Level;

public sealed class SkyFrame
{
    public float TimeOfDay { get; set; }

    /// <summary>
    /// Where the sun DISC is — the sky's vector, not the light's.
    /// </summary>
    /// <remarks>
    /// This is what the atmosphere, the disc and the glare want, because it is the direction the
    /// guest hands to the sky object. NOT the clouds: their pixel shader (0114) is lit by the
    /// rig's main light, colour and direction together (Engine_ClassicFeed's cloud pump). It carries SunAxisXYRotationOffset and
    /// SunAxisZOffset, neither of which reaches the main light. For lighting a surface use
    /// <see cref="MainLightDirection"/>; the two genuinely disagree.
    /// </remarks>
    public float[] SunDirection { get; } = new float[3];

    /// <summary>
    /// Where the main directional light comes FROM — the light rig's vector.
    /// </summary>
    /// <remarks>
    /// Built from the four authored MainLightElevation keyframes and LightRigXYRotation alone.
    /// This is the one that must drive the DirectionalLight3D and the bake, because it is the
    /// direction the guest sends to the same object as MainLightColour and MainLightShadowed.
    /// </remarks>
    public float[] MainLightDirection { get; } = new float[3];

    public float[] MoonDirection { get; } = new float[3];

    public float[] SkyTop { get; } = { 0.42f, 0.56f, 0.76f };

    public float[] SkyBottom { get; } = { 0.55f, 0.60f, 0.65f };

    public float[] SkySunset { get; } = { 1.0f, 0.47f, 0.22f };

    public float[] SkyParams { get; } = new float[4];

    public float[] MainLightColour { get; } = { 1f, 1f, 1f };

    /// <summary>Bumped each time a kept instance is evaluated over in place; see <see cref="SkyTheme.Revision"/>.</summary>
    public int Revision { get; set; }
}

public static class Engine_SkyFrame
{
    /// <summary>
    /// A sky direction in WORLD space.
    ///
    /// The region root carries `Scale = (-1, 1, 1)` (Engine_LevelScene), so the world the player
    /// sees is X-mirrored relative to the guest's own axes. Shader uniforms — the dome, the fog,
    /// the post-FX sun UV — are consumed in world space and take this directly. Anything placed
    /// as a NODE under that root must not: its local basis is mirrored again on the way out, so
    /// it needs the un-mirrored vector. See <see cref="ToRegionLocal"/>.
    /// </summary>
    public static Vector3 ToGodot(float[] d) => new(-d[0], d[1], d[2]);

    /// <summary>The same direction in the dome/fog shader space, which swaps Godot Y and Z.</summary>
    public static Vector3 ToDome(float[] d)
    {
        Vector3 g = ToGodot(d);
        return new Vector3(g.X, g.Z, g.Y);
    }

    /// <summary>
    /// The direction to hand a node that lives UNDER the region root, whose `Scale = (-1, 1, 1)`
    /// mirrors X again. Composed with that mirror it yields <see cref="ToGodot"/>.
    /// </summary>
    public static Vector3 ToRegionLocal(float[] d) => new(d[0], d[1], d[2]);

    public static SkyFrame Evaluate(SkyTheme theme, float timeOfDay) =>
        EvaluateInto(theme, timeOfDay, new SkyFrame());

    /// <summary>
    /// <see cref="Evaluate"/> written into <paramref name="frame"/>: every field is written, so a
    /// kept instance comes out as a new one would. Bumps its <see cref="SkyFrame.Revision"/>.
    /// </summary>
    public static SkyFrame EvaluateInto(SkyTheme theme, float timeOfDay, SkyFrame frame)
    {
        frame.Revision++;

        float time = float.IsFinite(timeOfDay) ? timeOfDay : 0.5f;
        time -= MathF.Floor(time);
        frame.TimeOfDay = time;

        CopyClamped(theme.SkyColour, frame.SkyTop, 0.42f, 0.56f, 0.76f);
        CopyClamped(theme.ComplementaryColour, frame.SkyBottom, 0.55f, 0.60f, 0.65f);
        CopyClamped(theme.SunsetColour, frame.SkySunset, 1.0f, 0.47f, 0.22f);
        Array.Copy(theme.MainLightColour, frame.MainLightColour, 3);

        frame.SkyParams[0] = MathF.Max(theme.SunIntensity, 0f);
        frame.SkyParams[1] = Math.Clamp(theme.ComplementaryBias, 0f, 1f);
        frame.SkyParams[2] = MathF.Max(theme.Rayleigh, 0.05f);
        frame.SkyParams[3] = MathF.Max(theme.Mie, 0.05f);

        const float toRadians = MathF.PI / 180f;
        const float twoPi = MathF.PI * 2f;

        float timeFactor =
            theme.HasSunAxis && float.IsFinite(theme.MainLightTimeFactor) &&
            theme.MainLightTimeFactor > 0f
                ? theme.MainLightTimeFactor
                : 1f;

        // THE GUEST BUILDS TWO SUN VECTORS, NOT ONE, and `sub_821DF670` builds them one after the
        // other from the same orbit. They are not interchangeable and we used to compute only the
        // first and hand it to both, which put the visible disc somewhere the light did not come
        // from.
        //
        // They go to different objects, which is what settles that they are meant to differ: the
        // sun and moon axes are delivered together to `*(a1+0x10)` (the sky), while the main
        // light's direction, its MainLightColour and its MainLightShadowed flag all go to
        // `*(a1+8)` through three `sub_8222AAC8` calls on the same `lwz r3, 8(r31)`.
        //
        // Both share the orbit `a = 2*PI * timeOfDay * TimeFactor` and the tilt
        // `Ry(SunAxisElevation)`. They differ in the final Z rotation and in what happens to Z:
        //
        //   LIGHT (0x821DFA48)  M = I . Rz(a) . Ry(e) . Rz(LightRigXYRotation)
        //                       axis = normalize((1,0,0) . M)
        //                       L = (cos(E)*axis.x, cos(E)*axis.y, sin(E))
        //
        //   DISC  (0x821DF878)  M = I . Rz(a) . Ry(e) . Rz(SunAxisXYRotationOffset + LightRigXYRotation)
        //                       v = (1,0,0) . M ;  v.z += SunAxisZOffset
        //                       S = normalize(v)
        //
        // The extra azimuth is `821df8a8 fmadds f1, f6, f30, f24` — SunAxisXYRotationOffset times
        // pi/180 PLUS the light rig's own angle — where the light block twenty instructions later
        // passes `fmr f1, f24`, the rig angle alone. So the disc leads or trails the light by
        // exactly SunAxisXYRotationOffset.
        //
        // AND THE DISC KEEPS THE ORBIT'S HEIGHT WHILE THE LIGHT REPLACES IT. The light throws
        // away axis.z and substitutes sin(E) from the four authored elevation keyframes; the disc
        // keeps -cos(a)sin(e), adds SunAxisZOffset and normalises. That is why the disc can sit on
        // the horizon while the light still comes from 70 degrees up.
        float f = time * timeFactor;
        float elevation = BlendElevation(theme, f) * toRadians;

        float a = f * twoPi;
        float b = (theme.HasSunAxis ? theme.SunAxisElevation : 26f) * toRadians;
        float c = theme.LightRigXyRotation * toRadians;

        float ca = MathF.Cos(a), sa = MathF.Sin(a);
        float cb = MathF.Cos(b), sb = MathF.Sin(b);
        float cc = MathF.Cos(c), sc = MathF.Sin(c);

        // Row 0 of Rz(a).Ry(b).Rz(c) — the orbit axis, already unit length because it is a row of
        // a rotation matrix, which is why the guest's `sub_82260C08` normalise here is a no-op.
        float axisX = (ca * cb * cc) - (sa * sc);
        float axisY = (ca * cb * sc) + (sa * cc);

        float cosElevation = MathF.Cos(elevation);

        // NO SECOND NORMALISE OF (x, y). This used to rescale the pair to unit length in the
        // ground plane before applying cos(E), which forces the result's elevation to be exactly
        // E. The guest does not: it takes axis.x and axis.y straight off the unit 3D axis, so
        // their length is sqrt(1 - axis.z^2) and the true elevation rides slightly above E
        // wherever the orbit is tilted. Verified at 0x821DFB18 and 0x821DFB50, which load the
        // axis components back unmodified and multiply each by cos(E).
        //
        // Our SunDirection is (groundX, up, groundZ) — the vertical is component [1], where the
        // guest's is [2]. Same vector, our axis order.
        Normalise(
            cosElevation * axisX,
            MathF.Sin(elevation),
            cosElevation * axisY,
            frame.MainLightDirection);

        // The disc's own azimuth, and its own Z.
        float discC = c + (theme.SunAxisXyRotation * toRadians);
        float cdc = MathF.Cos(discC), sdc = MathF.Sin(discC);

        Normalise(
            (ca * cb * cdc) - (sa * sdc),
            (-ca * sb) + theme.SunAxisZOffset,
            (ca * cb * sdc) + (sa * cdc),
            frame.SunDirection);

        if (theme.HasMoonAxis)
        {
            // THE MOON IS THE SUN'S CHAIN WITH A NEGATED ELEVATION, and no phase offset.
            // `sub_821DF670` builds three orbits from the SAME azimuth `v40 = f * 2pi`; the
            // moon's differs only in that `sub_821C1E38` is passed -(elevation) and in its own
            // rotation offset. A half-day offset was our invention and put the moon in the
            // wrong quarter of the sky.
            float mb = -theme.MoonAxisElevation * toRadians;
            // MoonAxisXyRotation PLUS THE RIG ROTATION, as the guest's third chain does:
            // `Rz(a) . R(-MoonAxisElevation) . Rz(theme[1040] + theme[928])`, where 1040 is the
            // moon's own XY rotation and 928 is LightRigXYRotation — the same term the sun's two
            // chains end on. MoonAxisZOffset is a VERTICAL offset and belongs only on the Z
            // component below; using it as a rotation put the moon a few degrees off its bearing
            // and dropped the rig rotation entirely.
            float mc = (theme.MoonAxisXyRotation + theme.LightRigXyRotation) * toRadians;
            float cmb = MathF.Cos(mb), smb = MathF.Sin(mb);
            float cmc = MathF.Cos(mc), smc = MathF.Sin(mc);

            Normalise(
                (ca * cmb * cmc) - (sa * smc),
                (-ca * smb) + theme.MoonAxisZOffset,
                (ca * cmb * smc) + (sa * cmc),
                frame.MoonDirection);
        }
        else
        {
            frame.MoonDirection[0] = -frame.SunDirection[0];
            frame.MoonDirection[1] = -frame.SunDirection[1];
            frame.MoonDirection[2] = -frame.SunDirection[2];
        }

        return frame;
    }

    private static void CopyClamped(float[] source, float[] target, float r, float g, float b)
    {
        target[0] = Finite(source[0], r);
        target[1] = Finite(source[1], g);
        target[2] = Finite(source[2], b);
    }

    private static float Finite(float value, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 4f) : fallback;

    /// <summary>
    /// The main light's elevation in DEGREES for a day fraction, cosine-blended across the four
    /// authored keyframes at 0, 6, 12 and 18 hours.
    ///
    /// A cosine ease, not a lerp: the guest computes
    /// <c>cos(-((q*0.25) - f) * 4*PI)</c> and mixes with
    /// <c>((v*(e_q - e_next) + (e_next + e_q)) * 0.5)</c>, which is exactly e_q at the start of
    /// a quadrant and exactly e_next a quarter later. The wrap from 18h back to 0h is
    /// <c>(q + 1) % 4</c>, so the day closes on itself.
    /// </summary>
    private static float BlendElevation(SkyTheme theme, float f)
    {
        // NO "UNSTATED" ARM ANY MORE. The guest has no such case: sub_82A45DD0 states all four
        // keyframes itself and sets their stated-flags, so the blend below always runs and a theme
        // that names none of them still gets the 50/20/70/20 arc. Substituting SunAxisElevation
        // was borrowing the SKY's axis tilt for the LIGHT's elevation — two different quantities —
        // and it produced a sun that never changed height across the day.
        float wrapped = f - MathF.Floor(f);
        int quadrant = (int)(wrapped * 4.0f);
        quadrant = Math.Clamp(quadrant, 0, 3);

        float current = theme.MainLightElevation[quadrant];
        float next = theme.MainLightElevation[(quadrant + 1) % 4];

        float phase = wrapped - (quadrant * 0.25f);
        float weight = MathF.Cos(phase * 4.0f * MathF.PI);
        return ((weight * (current - next)) + (next + current)) * 0.5f;
    }

    private static void Normalise(float x, float y, float z, float[] target)
    {
        float length = MathF.Sqrt((x * x) + (y * y) + (z * z));
        if (length > 1e-6f)
        {
            x /= length;
            y /= length;
            z /= length;
        }

        target[0] = x;
        target[1] = y;
        target[2] = z;
    }
}

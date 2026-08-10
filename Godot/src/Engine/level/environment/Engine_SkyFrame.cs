using System;

namespace Refii.Level;

public sealed class SkyFrame
{
    public float TimeOfDay { get; set; }

    public float[] SunDirection { get; } = new float[3];

    public float[] MoonDirection { get; } = new float[3];

    public float[] SkyTop { get; } = { 0.42f, 0.56f, 0.76f };

    public float[] SkyBottom { get; } = { 0.55f, 0.60f, 0.65f };

    public float[] SkySunset { get; } = { 1.0f, 0.47f, 0.22f };

    public float[] SkyParams { get; } = new float[4];

    public float[] MainLightColour { get; } = { 1f, 1f, 1f };
}

public static class Engine_SkyFrame
{
    public static SkyFrame Evaluate(SkyTheme theme, float timeOfDay)
    {
        var frame = new SkyFrame();

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

        // THE GUEST'S OWN SUN PATH, from sub_821DF670. This used to be a single fixed axis
        // tilt swept around Z, which put the sun at roughly the same height all day.
        //
        //   f     = timeOfDay * TimeFactor
        //   elev  = a COSINE blend of four authored keyframes at 0/6/12/18h, in degrees
        //   M     = I . Rz(f*2pi) . Ry(SunAxisElevation) . Rz(LightRigXYRotation)
        //   h     = normalize(M[0][0], M[0][1], 0)      <- ROW 0, XY only
        //   dir   = (cos(elev)*h.x, cos(elev)*h.y, sin(elev))
        //
        // Three details that are easy to get wrong and were all verified in the disassembly:
        // the matrix is ROW-major with row 0 at +0x00 (taking column 0 instead mirrors the sun
        // about X); the final Z rotation uses LightRigXYRotation ALONE, because
        // SunAxisXYRotationOffset belongs to a different vector built in an earlier block; and
        // SunAxisZOffset is likewise not part of the main light at all.
        float f = time * timeFactor;
        float elevation = BlendElevation(theme, f) * toRadians;

        float a = f * twoPi;
        float b = (theme.HasSunAxis ? theme.SunAxisElevation : 26f) * toRadians;
        float c = theme.LightRigXyRotation * toRadians;

        float ca = MathF.Cos(a), sa = MathF.Sin(a);
        float cb = MathF.Cos(b);
        float cc = MathF.Cos(c), sc = MathF.Sin(c);

        // Row 0 of Rz(a).Ry(b).Rz(c), with the Z component dropped before normalising.
        float hx = (ca * cb * cc) - (sa * sc);
        float hy = (ca * cb * sc) + (sa * cc);
        float hLength = MathF.Sqrt((hx * hx) + (hy * hy));
        if (hLength > 1e-4f)
        {
            hx /= hLength;
            hy /= hLength;
        }

        float cosElevation = MathF.Cos(elevation);

        // Our SunDirection is (groundX, up, groundZ) — the vertical is component [1], where the
        // guest's is [2]. Same vector, our axis order.
        Normalise(
            cosElevation * hx,
            MathF.Sin(elevation),
            cosElevation * hy,
            frame.SunDirection);

        if (theme.HasMoonAxis)
        {
            // UNCHANGED, and deliberately. The guest builds the moon the same way it builds the
            // sun, with -MoonAxisElevation — but which rotation parameters feed its chain was
            // not established, so this keeps the old construction rather than assuming the sun's
            // corrections carry across. Its own locals, so the two no longer share state.
            float moonTheta = ((time * timeFactor) - 0.5f) * twoPi;
            float cosTheta = MathF.Cos(moonTheta);
            float groundZ = MathF.Sin(moonTheta);
            float moonElevation = theme.MoonAxisElevation * toRadians;
            float moonPhi = (theme.MoonAxisZOffset + theme.MoonAxisXyRotation) * toRadians;
            float moonGroundX = cosTheta * MathF.Cos(moonElevation);
            float moonUp = cosTheta * MathF.Sin(moonElevation);
            float cosMoonPhi = MathF.Cos(moonPhi);
            float sinMoonPhi = MathF.Sin(moonPhi);

            Normalise(
                -((moonGroundX * cosMoonPhi) - (groundZ * sinMoonPhi)),
                -moonUp,
                -((moonGroundX * sinMoonPhi) + (groundZ * cosMoonPhi)),
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
        if (!theme.HasMainLightElevation)
        {
            return theme.HasSunAxis ? theme.SunAxisElevation : 26.0f;
        }

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

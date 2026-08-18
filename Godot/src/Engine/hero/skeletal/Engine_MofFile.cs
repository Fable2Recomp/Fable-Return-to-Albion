using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Refii.Hero;

public sealed class MofBoneOffset
{
    public string Name { get; set; } = string.Empty;

    public float[] Position { get; } = new float[3];

    public float[] Scale { get; } = { 1.0f, 1.0f, 1.0f };
}

public static class Engine_MofFile
{
    public const string ShadowBonePrefix = "ShadowBone:";

    public const float PositionScale = 0.1f;

    public static List<MofBoneOffset> Parse(byte[] bytes)
    {
        var offsets = new List<MofBoneOffset>();
        if (bytes.Length == 0)
        {
            return offsets;
        }

        string[] lines = Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.StartsWith(ShadowBonePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var offset = new MofBoneOffset { Name = line[ShadowBonePrefix.Length..].Trim() };
            var raw = new float[6];

            if (i + 1 >= lines.Length)
            {
                break;
            }

            if (lines[i + 1].StartsWith("PosX:", StringComparison.Ordinal))
            {
                if (i + 6 >= lines.Length ||
                    !Labelled(lines[i + 1], "PosX", out raw[0]) ||
                    !Labelled(lines[i + 2], "PosY", out raw[1]) ||
                    !Labelled(lines[i + 3], "PosZ", out raw[2]) ||
                    !Labelled(lines[i + 4], "SclX", out raw[3]) ||
                    !Labelled(lines[i + 5], "SclY", out raw[4]) ||
                    !Labelled(lines[i + 6], "SclZ", out raw[5]))
                {
                    continue;
                }

                i += 6;
            }
            else
            {
                if (i + 2 >= lines.Length ||
                    !Triplet(lines[i + 1], out float px, out float py, out float pz) ||
                    !Triplet(lines[i + 2], out float sx, out float sy, out float sz))
                {
                    continue;
                }

                raw[0] = px;
                raw[1] = py;
                raw[2] = pz;
                raw[3] = sx;
                raw[4] = sy;
                raw[5] = sz;
                i += 2;
            }

            offset.Position[0] = raw[0] * -PositionScale;
            offset.Position[1] = raw[2] * PositionScale;
            offset.Position[2] = raw[1] * PositionScale;
            offset.Scale[0] = raw[3];
            offset.Scale[1] = raw[5];
            offset.Scale[2] = raw[4];

            offsets.Add(offset);
        }

        return offsets;
    }

    private static bool Labelled(string line, string label, out float value)
    {
        value = 0.0f;
        string prefix = label + ":";

        return line.StartsWith(prefix, StringComparison.Ordinal) &&
            float.TryParse(
                line[prefix.Length..].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static bool Triplet(string line, out float x, out float y, out float z)
    {
        x = 0.0f;
        y = 0.0f;
        z = 0.0f;

        string[] parts = line.Split(',');
        return parts.Length >= 3 &&
            float.TryParse(
                parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
            float.TryParse(
                parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
            float.TryParse(
                parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z);
    }
}

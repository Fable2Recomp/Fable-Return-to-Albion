using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Refii.Level;

public sealed class GmdLayoutChild
{
    public string RawPath { get; set; } = string.Empty;

    public string AssetKey { get; set; } = string.Empty;

    public Transform3D Local { get; set; } = Transform3D.Identity;
}

public static class Engine_GmdLayout
{
    public const int PayloadWindow = 160;

    public const float MaxTranslation = 512.0f;

    private static readonly string[] Markers =
    {
        "Prop.Layout.Instance.",
        "Light.Layout.Instance.",
        "Environment.Layout.Instance.",
    };

    private static readonly string[] EmitNeedles =
    {
        "door", "window", "win_", "_win", "sign",
        "lamp", "lantern", "candle", "light",
    };

    public static bool IsShellParent(string modelPath)
    {
        string path = modelPath.Replace('\\', '/').ToLowerInvariant();
        return path.Contains("/exterior.mdl", StringComparison.Ordinal) ||
               path.Contains("/interior.mdl", StringComparison.Ordinal) ||
               Refii.Core.Engine_TitleGame.Active.Props.IsShellParent(path);
    }

    public static bool ShouldEmit(string assetKey, string resolvedPath)
    {
        string text = (assetKey + " " + resolvedPath.Replace('\\', '/')).ToLowerInvariant();
        foreach (string needle in EmitNeedles)
        {
            if (text.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static List<GmdLayoutChild> Parse(byte[]? bytes)
    {
        var children = new List<GmdLayoutChild>();
        if (bytes is null || bytes.Length == 0)
        {
            return children;
        }

        int cursor = 0;
        while (cursor < bytes.Length)
        {
            int start = -1;
            foreach (string marker in Markers)
            {
                int at = IndexOf(bytes, marker, cursor);
                if (at >= 0 && (start < 0 || at < start))
                {
                    start = at;
                }
            }

            if (start < 0)
            {
                break;
            }

            int end = start;
            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            if (end >= bytes.Length)
            {
                break;
            }

            string raw = Encoding.ASCII.GetString(bytes, start, end - start);
            int payloadStart = end + 1;
            int payloadEnd = Math.Min(bytes.Length, payloadStart + PayloadWindow);

            for (int s = payloadStart; s + 4 <= payloadEnd; s++)
            {
                if (bytes[s] == 0xff && bytes[s + 1] == 0xff &&
                    bytes[s + 2] == 0xff && bytes[s + 3] == 0xff)
                {
                    payloadEnd = s;
                    break;
                }
            }

            string assetKey = AssetKeyFromRawPath(raw);
            if (assetKey.Length != 0 &&
                TryParseTransform(bytes, payloadStart, payloadEnd, out Transform3D local))
            {
                children.Add(new GmdLayoutChild
                {
                    RawPath = raw,
                    AssetKey = assetKey,
                    Local = local,
                });
            }

            cursor = end + 1;
        }

        return children;
    }

    public static string AssetKeyFromRawPath(string rawPath)
    {
        string path = rawPath.Replace('\\', '/').ToLowerInvariant();

        int marker = path.IndexOf("layout.instance.", StringComparison.Ordinal);
        if (marker >= 0)
        {
            path = path[(marker + "layout.instance.".Length)..];
        }

        int art = path.IndexOf("art/", StringComparison.Ordinal);
        if (art >= 0)
        {
            path = path[art..];
        }

        int slash = path.LastIndexOf('/');
        string name = slash < 0 ? path : path[(slash + 1)..];

        foreach (string suffix in new[] { ".emdl", ".mdl", "_asset", "asset" })
        {
            if (name.Length >= suffix.Length &&
                name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
            }
        }

        return CompactKey(name);
    }

    public static string CompactKey(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                text.Append(char.ToLowerInvariant(c));
            }
        }

        return text.ToString();
    }

    private static bool TryParseTransform(
        byte[] bytes, int payloadStart, int payloadEnd, out Transform3D local)
    {
        local = Transform3D.Identity;

        float bestScore = float.PositiveInfinity;
        float bqx = 0f, bqy = 0f, bqz = 0f, bqw = 1f, btx = 0f, bty = 0f, btz = 0f;

        for (int align = 0; align < 4; align++)
        {
            var floats = new List<float>();
            for (int off = payloadStart + align; off + 4 <= payloadEnd; off += 4)
            {
                floats.Add(ReadBeF32(bytes, off));
            }

            if (floats.Count < 7)
            {
                continue;
            }

            for (int i = 0; i + 7 <= floats.Count; i++)
            {
                float qx = floats[i];
                float qy = floats[i + 1];
                float qz = floats[i + 2];
                float qw = floats[i + 3];
                float tx = floats[i + 4];
                float ty = floats[i + 5];
                float tz = floats[i + 6];

                if (!float.IsFinite(qx) || !float.IsFinite(qy) || !float.IsFinite(qz) ||
                    !float.IsFinite(qw) || !float.IsFinite(tx) || !float.IsFinite(ty) ||
                    !float.IsFinite(tz))
                {
                    continue;
                }

                if (MathF.Abs(tx) > MaxTranslation || MathF.Abs(ty) > MaxTranslation ||
                    MathF.Abs(tz) > MaxTranslation)
                {
                    continue;
                }

                float qmag = MathF.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
                if (!float.IsFinite(qmag) || qmag < 0.6f || qmag > 1.4f)
                {
                    continue;
                }

                float posMag = MathF.Sqrt((tx * tx) + (ty * ty) + (tz * tz));
                float score = (MathF.Abs(qmag - 1f) * 100f) +
                              (posMag < 1e-4f ? 20f : 0f) +
                              (i * 0.01f) + (align * 0.001f);

                if (score < bestScore)
                {
                    bestScore = score;
                    bqx = qx;
                    bqy = qy;
                    bqz = qz;
                    bqw = qw;
                    btx = tx;
                    bty = ty;
                    btz = tz;
                }
            }
        }

        if (!float.IsFinite(bestScore))
        {
            return false;
        }

        local = new Transform3D(
            SwapAxes(FromQuaternion(bqx, bqy, bqz, bqw)),
            new Vector3(btx, btz, bty));

        return true;
    }

    private static float[] FromQuaternion(float qx, float qy, float qz, float qw)
    {
        float length = MathF.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
        if (!float.IsFinite(length) || length < 1e-6f)
        {
            return new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
        }

        qx /= length;
        qy /= length;
        qz /= length;
        qw /= length;

        return new[]
        {
            1f - (2f * ((qy * qy) + (qz * qz))),
            2f * ((qx * qy) - (qw * qz)),
            2f * ((qx * qz) + (qw * qy)),
            2f * ((qx * qy) + (qw * qz)),
            1f - (2f * ((qx * qx) + (qz * qz))),
            2f * ((qy * qz) - (qw * qx)),
            2f * ((qx * qz) - (qw * qy)),
            2f * ((qy * qz) + (qw * qx)),
            1f - (2f * ((qx * qx) + (qy * qy))),
        };
    }

    private static Basis SwapAxes(float[] game)
    {
        var map = new[] { 0, 2, 1 };
        var m = new float[9];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                m[(row * 3) + col] = game[(map[row] * 3) + map[col]];
            }
        }

        return new Basis(
            new Vector3(m[0], m[3], m[6]),
            new Vector3(m[1], m[4], m[7]),
            new Vector3(m[2], m[5], m[8]));
    }

    private static float ReadBeF32(byte[] bytes, int at)
    {
        uint raw = ((uint)bytes[at] << 24) | ((uint)bytes[at + 1] << 16) |
                   ((uint)bytes[at + 2] << 8) | bytes[at + 3];
        return BitConverter.Int32BitsToSingle(unchecked((int)raw));
    }

    private static int IndexOf(byte[] haystack, string needle, int from)
    {
        int limit = haystack.Length - needle.Length;
        for (int i = Math.Max(from, 0); i <= limit; i++)
        {
            int k = 0;
            while (k < needle.Length && haystack[i + k] == (byte)needle[k])
            {
                k++;
            }

            if (k == needle.Length)
            {
                return i;
            }
        }

        return -1;
    }
}

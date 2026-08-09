using System;
using System.IO;
using System.IO.Compression;
using Godot;
using Refii.Level;
using Refii.Textures;

namespace Refii.Lighting;

public static class Engine_LevelBakedLighting
{
    public const string LightmapExtension = ".lmp";

    public const string ProbeRelativePath = "LightProbeData/LightProbesData.dat";

    public static void TryLoad(
        string gdbPath,
        Func<string, byte[]?> read,
        out LightmapContainer? lightmaps,
        out LightProbeField? probes)
    {
        lightmaps = null;
        probes = null;

        if (gdbPath.Length == 0)
        {
            return;
        }

        string lmpPath = Engine_LevelGdb.SiblingWithExtension(gdbPath, LightmapExtension);
        byte[]? lmp = MaybeGunzip(read(lmpPath) ?? read("data/" + lmpPath));
        if (lmp is not null)
        {
            if (Engine_LightmapFile.TryParse(lmp, out LightmapContainer container, out string lmError))
            {
                lightmaps = container;
            }
            else
            {
                GD.Print($"[baked] {lmpPath}: {lmError}");
            }
        }

        int slash = lmpPath.LastIndexOf('/');
        string directory = slash < 0 ? string.Empty : lmpPath[..slash];
        string probePath = directory.Length == 0
            ? ProbeRelativePath
            : $"{directory}/{ProbeRelativePath}";

        byte[]? probeBytes = MaybeGunzip(read(probePath) ?? read("data/" + probePath));
        if (probeBytes is not null)
        {
            if (Engine_LightProbeField.TryParse(probeBytes, out LightProbeField field, out string probeError))
            {
                probes = field;
            }
            else
            {
                GD.Print($"[baked] {probePath}: {probeError}");
            }
        }
    }

    public static void ApplyInstanceLighting(
        GeometryInstance3D node,
        Vector3 godotPosition,
        LightProbeField? probes,
        LightingFrame? frame)
    {
        bool probeValid = false;
        if (probes is not null && probes.HasData)
        {
            var gamePosition = new Vector3(
                godotPosition.X, godotPosition.Z, godotPosition.Y);
            ProbeSample sample = probes.Sample(gamePosition);
            if (sample.R.LengthSquared() + sample.G.LengthSquared() +
                sample.B.LengthSquared() > 0f)
            {
                probeValid = true;
                node.SetInstanceShaderParameter("probe_r", sample.R);
                node.SetInstanceShaderParameter("probe_g", sample.G);
                node.SetInstanceShaderParameter("probe_b", sample.B);
            }
        }

        float[] lightmap = frame?.LightmapColour ?? new float[] { 1f, 1f, 1f };
        node.SetInstanceShaderParameter(
            "lightmap_colour",
            new Vector4(lightmap[0], lightmap[1], lightmap[2],
                        probeValid ? 1f : 0f));

        float ar = 0f, ag = 0f, ab = 0f;
        if (frame is not null && frame.HasAmbient)
        {
            ar = frame.AmbientColour[0] * frame.AmbientEnergy;
            ag = frame.AmbientColour[1] * frame.AmbientEnergy;
            ab = frame.AmbientColour[2] * frame.AmbientEnergy;
        }

        node.SetInstanceShaderParameter(
            "ambient_colour",
            new Vector4(ar, ag, ab, frame is not null ? 1f : 0f));
    }

    public static ImageTexture? DecodeTexture(byte[] blob)
    {
        try
        {
            return Engine_TexImage.TryCreateTexture(blob, out _);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool TryPickTerrainLightmap(
        LightmapContainer container, out ulong key, out byte[] blob)
    {
        key = 0;
        blob = Array.Empty<byte>();

        long bestArea = -1;
        foreach (System.Collections.Generic.KeyValuePair<ulong, byte[]> entry in container.TerrainLightmapBlobs)
        {
            long area = BlobArea(entry.Value);
            if (area > bestArea)
            {
                bestArea = area;
                key = entry.Key;
                blob = entry.Value;
            }
        }

        return bestArea >= 0;
    }

    public static string Describe(LightmapContainer? lightmaps, LightProbeField? probes)
    {
        string lm = lightmaps is null
            ? "lmp=none"
            : $"lmp=v{lightmaps.Version} terrain={lightmaps.TerrainLightmapBlobs.Count} " +
              $"objtex={lightmaps.ObjectLightmapBlobs.Count} vbake={lightmaps.VertexBakes.Count} " +
              $"arrays={lightmaps.U32Arrays.Count} blocks={lightmaps.Blocks48.Count}";

        string pr = probes is null
            ? "probes=none"
            : $"probes=dim({probes.DimX}x{probes.DimY}x{probes.DimZ}) roots={probes.RootPairKeys.Length} " +
              $"leaves={probes.Leaves.Length / 32} bank0={probes.Banks[0].Length}";

        return $"{lm} {pr}";
    }

    private static long BlobArea(byte[] blob)
    {
        return TexHeader.TryParse(blob, out TexHeader header, out _)
            ? (long)header.Width * header.Height
            : 0;
    }

    private static byte[]? MaybeGunzip(byte[]? data)
    {
        if (data is null || data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B)
        {
            return data;
        }

        try
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception)
        {
            return data;
        }
    }
}

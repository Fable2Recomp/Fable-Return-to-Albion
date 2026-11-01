using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Refii.Models;

/// <summary>
/// Fable 3's skeletal morph offset file (<c>.mof</c>, 37 in <c>skeletalmorphs.bnk</c>: the
/// villagers' and heroes' <c>rigdata\morphs\&lt;sex&gt;_fat|strong|thin|tall.mof</c>, the
/// creatures' <c>rigdata\morphers\offsetfile_*.mof</c>). Plain text, one <c>Key:Value</c> per
/// line: a <c>#Morph Offset File</c> header, <c>ShadowBonesCount:N</c>, then per bone
/// <c>ShadowBone:&lt;name&gt;</c> followed by <c>PosX/PosY/PosZ</c> (a translation offset) and
/// <c>SclX/SclY/SclZ</c> (a scale). The guest reads them through the def's SkeletalMorphs rows
/// ({MorphType, MorphSkeleton} and BaseSkeletalMorph, <c>sub_827740A0</c> → <c>sub_82774A08</c>)
/// and blends them at the stat weights into the per-bone {offset float4, scale float4} record the
/// shell already takes as <c>RefiiHostGetBoneMorph</c>; this reader is the editor's and the
/// verifier's view of one source file.
/// </summary>
public sealed class Engine_MofFile
{
    /// <summary>One shadow bone's offset and scale, in the order the file lists them.</summary>
    public readonly record struct Bone(string Name, Vector3 Offset, Vector3 Scale);

    /// <summary>The <c>ShadowBonesCount</c> the header declares; -1 when absent.</summary>
    public int DeclaredCount { get; init; } = -1;

    /// <summary>The bones in file order.</summary>
    public IReadOnlyList<Bone> Bones { get; init; } = Array.Empty<Bone>();

    /// <summary>The header line was <c>#Morph Offset File</c>.</summary>
    public bool HasHeader { get; init; }

    public const string Header = "#Morph Offset File";

    /// <summary>Parses the file's bytes (ASCII, LF or CRLF). Never throws on content: unknown keys
    /// are skipped, a malformed number leaves the lane at its default (0 offset, 1 scale).</summary>
    public static Engine_MofFile Parse(ReadOnlySpan<byte> bytes) => Parse(Encoding.ASCII.GetString(bytes));

    public static Engine_MofFile Parse(string text)
    {
        var bones = new List<Bone>();
        int declared = -1;
        bool header = false;

        string? name = null;
        Vector3 offset = Vector3.Zero;
        Vector3 scale = Vector3.One;

        void Flush()
        {
            if (name is not null)
            {
                bones.Add(new Bone(name, offset, scale));
            }

            name = null;
            offset = Vector3.Zero;
            scale = Vector3.One;
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                header |= string.Equals(line, Header, StringComparison.Ordinal);
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string key = line[..colon];
            string value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "ShadowBonesCount":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                    {
                        declared = count;
                    }

                    break;
                case "ShadowBone":
                    Flush();
                    name = value;
                    break;
                case "PosX": offset.X = Lane(value, 0.0f); break;
                case "PosY": offset.Y = Lane(value, 0.0f); break;
                case "PosZ": offset.Z = Lane(value, 0.0f); break;
                case "SclX": scale.X = Lane(value, 1.0f); break;
                case "SclY": scale.Y = Lane(value, 1.0f); break;
                case "SclZ": scale.Z = Lane(value, 1.0f); break;
                default:
                    break;
            }
        }

        Flush();
        return new Engine_MofFile { DeclaredCount = declared, Bones = bones, HasHeader = header };
    }

    private static float Lane(string value, float fallback) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : fallback;

    /// <summary>The bone named <paramref name="name"/>, or null.</summary>
    public Bone? Find(string name)
    {
        foreach (Bone bone in Bones)
        {
            if (string.Equals(bone.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return bone;
            }
        }

        return null;
    }

    /// <summary>True when a bone moves or scales away from identity.</summary>
    public static bool IsIdentity(in Bone bone) => bone.Offset == Vector3.Zero && bone.Scale == Vector3.One;
}

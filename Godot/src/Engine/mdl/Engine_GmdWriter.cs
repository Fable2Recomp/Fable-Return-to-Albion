using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Godot;

namespace Refii.Models;

/// <summary>
/// Reference metadata for a standard Fable II <c>.mdl.gmd</c> attachment socket.
/// </summary>
public readonly record struct GmdSocketReference(
    string Name,
    string Category,
    string Description);

/// <summary>
/// Serializes <see cref="Engine_GmdSkeleton"/> and <see cref="GmdAttachment"/> tables into big-endian
/// <c>GameMesh</c> (<c>.mdl.gmd</c>) binaries, and bridges <c>.mdl.gmd</c> attachment sockets
/// into and out of <c>.glb</c>/<c>.gltf</c> files for Blender authoring.
/// </summary>
public static class Engine_GmdWriter
{
    /// <summary>
    /// Curated reference catalog of standard Fable II <c>.mdl.gmd</c> attachment sockets.
    /// </summary>
    public static readonly IReadOnlyList<GmdSocketReference> StandardSockets = new[]
    {
        new GmdSocketReference(
            "Weapon.Weapon.Hold",
            "Weapons (All 1H & 2H)",
            "Primary right-hand grip socket on the weapon handle/hilt where the character's right hand attaches."),
        new GmdSocketReference(
            "Weapon.Weapon.UpperHold",
            "Weapons (2H Rifles / Crossbows / Greatswords / Hammers)",
            "Secondary left-hand foregrip / two-handed support grip socket for rifles, blunderbusses, crossbows, greatswords, and hammers."),
        new GmdSocketReference(
            "Weapon.Weapon.Sheath",
            "Weapons (All 1H & 2H)",
            "Holster / sheath pivot on the weapon where it attaches to the character's hip or back when not drawn."),
        new GmdSocketReference(
            "Weapon.Weapon.ProjectileEmitter",
            "Ranged Weapons (Pistols / Rifles / Crossbows)",
            "Barrel-tip muzzle origin and forward trajectory (-Z in glTF / +Y in Blender) where bullets and bolts spawn."),
        new GmdSocketReference(
            "Weapon.Weapon.Top",
            "Melee Weapons (1H & 2H Swords / Axes / Hammers)",
            "Tip of the blade or top of the hammer head; defines weapon reach from Weapon.Weapon.Hold to Weapon.Weapon.Top for hit sweeps and trails."),
        new GmdSocketReference(
            "Weapon.FX.Particle.FXINV_Flintlock_Blast_01.par",
            "Ranged Weapons (Pistols / Rifles / Blunderbusses)",
            "Standard flintlock/clockwork muzzle flash and smoke blast particle emitter at the barrel tip."),
        new GmdSocketReference(
            "Weapon.FX.Particle.FX_Flintlock_Trigger_01.par",
            "Ranged Weapons (Crossbows)",
            "Crossbow trigger/bowstring release particle effect socket."),
        new GmdSocketReference(
            "Weapon.FX.Particle.Effect_1.",
            "Weapons (Legendary / Custom FX)",
            "Generic weapon particle effect attachment socket (used on Hal's Rifle, Legendary weapons, and custom FX; note trailing period)."),
        new GmdSocketReference(
            "Prop.Carry.1Hand.Right",
            "Carried Props & Items",
            "Right-hand grip socket for carried props, mugs, potions, trophies, and wooden toy swords."),
        new GmdSocketReference(
            "Prop.Carry.1Hand.Left",
            "Carried Props & Items",
            "Left-hand grip socket for off-hand carried props and items."),
        new GmdSocketReference(
            "Prop.Layout.Position",
            "Props & Environment",
            "Root layout/placement pivot socket on interactive and decorative props."),
        new GmdSocketReference(
            "Prop.Action.Generic",
            "Props & Environment",
            "Generic character interaction alignment socket on interactive props."),
        new GmdSocketReference(
            "Prop.FX.Particle.Effect_1.",
            "Props & Projectiles",
            "Particle emitter socket on props and projectile bolts (note trailing period)."),
        new GmdSocketReference(
            "Prop.Layout.Light",
            "Props & Environment",
            "Dynamic point-light attachment origin for lanterns, torches, and lit props."),
        new GmdSocketReference(
            "Character.Carry.Hand.Right",
            "Character Skeleton (Reference)",
            "Character right-hand parent dummy that Weapon.Weapon.Hold / Prop.Carry.1Hand.Right snaps onto."),
        new GmdSocketReference(
            "Character.Carry.Hand.Left",
            "Character Skeleton (Reference)",
            "Character left-hand parent dummy that Weapon.Weapon.UpperHold / Prop.Carry.1Hand.Left snaps onto."),
    };

    private static readonly string[] KnownSocketPrefixes =
    {
        "Character.",
        "Weapon.",
        "Prop.",
        "TARGET_",
        "WEAPON_",
        "FX_",
        "Layout_",
        "Dummy_",
        "GMD:",
        "GMD_",
    };

    /// <summary>
    /// Serializes a big-endian <c>GameMesh</c> (<c>.mdl.gmd</c>) binary using <paramref name="template"/>
    /// for the 44-byte header, skeleton nodes, and trailing section, and writing <paramref name="attachments"/>
    /// into the attachment table.
    /// </summary>
    public static byte[] Write(
        Engine_GmdSkeleton? template,
        IReadOnlyList<GmdAttachment> attachments)
    {
        using var ms = new MemoryStream();

        // 1. 44-byte Header (0x00..0x2B): "GameMesh", version = 3, and 32-byte LOD metadata
        if (template is not null && template.HeaderBytes is { Length: 0x2C })
        {
            ms.Write(template.HeaderBytes, 0, 0x2C);
        }
        else
        {
            var header = new byte[0x2C];
            Encoding.ASCII.GetBytes("GameMesh", 0, 8, header, 0);
            WriteU32(header, 0x08, 3u);
            ms.Write(header, 0, 0x2C);
        }

        // 2. Section 1: Skeleton Nodes (0x2C..)
        int nodeCount = template?.Names.Length ?? 0;
        WriteU32(ms, (uint)nodeCount);

        for (int i = 0; i < nodeCount; i++)
        {
            WriteStringZ(ms, template!.Names[i] ?? string.Empty);
            WriteU32(ms, unchecked((uint)template.Parents[i]));
        }

        // transformCount is always emitted (even when 0, per sub_82B7AAC8 in default.xex)
        WriteU32(ms, (uint)nodeCount);

        for (int i = 0; i < nodeCount; i++)
        {
            Transform3D gameLocal = Engine_GmdSkeleton.ToGameSpace(template!.Locals[i]);
            Quaternion q = gameLocal.Basis.Orthonormalized().GetRotationQuaternion().Normalized();
            Vector3 pos = gameLocal.Origin;
            Vector3 scale = i < template.NodeScales.Length ? template.NodeScales[i] : Vector3.One;
            float pad = i < template.NodePads.Length ? template.NodePads[i] : 0f;

            WriteF32(ms, q.X);
            WriteF32(ms, q.Y);
            WriteF32(ms, q.Z);
            WriteF32(ms, q.W);
            WriteF32(ms, pos.X);
            WriteF32(ms, pos.Y);
            WriteF32(ms, pos.Z);
            WriteF32(ms, scale.X);
            WriteF32(ms, scale.Y);
            WriteF32(ms, scale.Z);
            WriteF32(ms, pad);
        }

        // 3. Section 2: Attachment / Dummy Table
        int attCount = attachments?.Count ?? 0;
        WriteU32(ms, (uint)attCount);

        if (attachments is not null)
        {
            for (int i = 0; i < attCount; i++)
            {
                GmdAttachment att = attachments[i];
                WriteStringZ(ms, att.Name ?? string.Empty);

                Transform3D cleanGodot = new(att.Local.Basis.Orthonormalized(), att.Local.Origin);
                Transform3D gameTransform = Engine_GmdSkeleton.ToGameSpace(cleanGodot);
                Quaternion q = gameTransform.Basis.Orthonormalized().GetRotationQuaternion().Normalized();
                Vector3 pos = gameTransform.Origin;

                WriteF32(ms, q.X);
                WriteF32(ms, q.Y);
                WriteF32(ms, q.Z);
                WriteF32(ms, q.W);
                WriteF32(ms, pos.X);
                WriteF32(ms, pos.Y);
                WriteF32(ms, pos.Z);
                WriteU32(ms, unchecked((uint)att.Node));
            }
        }

        // 4. Section 3: Trailing Physics / Spline Section
        if (template is not null && template.TrailingBytes is { Length: > 0 })
        {
            ms.Write(template.TrailingBytes, 0, template.TrailingBytes.Length);
        }
        else
        {
            WriteU32(ms, 0u);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Appends a <c>GmdDummies</c> <see cref="Node3D"/> hierarchy onto <paramref name="root"/> prior to
    /// <see cref="GltfDocument.AppendFromScene"/> so every <c>.mdl.gmd</c> socket is exported into the <c>.glb</c>
    /// as a Blender Empty (Plain Axes) at its exact Y-up model-space transform.
    /// </summary>
    public static void AppendGmdDummiesToScene(Node3D root, Engine_GmdSkeleton? gmd)
    {
        if (root is null || gmd is null || gmd.Attachments.Length == 0)
        {
            return;
        }

        var container = new Node3D { Name = "GmdDummies" };
        root.AddChild(container);
        container.Owner = root;

        var seenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < gmd.Attachments.Length; i++)
        {
            GmdAttachment att = gmd.Attachments[i];
            if (string.IsNullOrWhiteSpace(att.Name))
            {
                continue;
            }

            seenCounts.TryGetValue(att.Name, out int count);
            seenCounts[att.Name] = count + 1;

            // Godot Node.Name sanitizes '.' to '_', so we also patch the GLB JSON chunk after export
            // to restore the exact dotted name in Blender's Outliner.
            string safeNodeName = count == 0
                ? att.Name.Replace('.', '_')
                : $"{att.Name.Replace('.', '_')}#{count}";

            var dummyNode = new Node3D
            {
                Name = safeNodeName,
                Transform = gmd.ComposeAttachment(i)
            };
            dummyNode.SetMeta("gmd_dummy_name", att.Name);
            dummyNode.SetMeta("gmd_parent_node", att.Node);
            dummyNode.SetMeta("gmd_index", i);

            container.AddChild(dummyNode);
            dummyNode.Owner = root;
        }
    }

    /// <summary>
    /// Post-processes a <c>.glb</c> file on disk so that every child under <c>GmdDummies</c> carries its
    /// exact dotted Fable II socket name (e.g. <c>Character.Carry.1Hand.Right</c>) and <c>extras.gmd_dummy_name</c>
    /// in the glTF JSON chunk.
    /// </summary>
    public static void PatchGlbGmdDummyNamesAndExtras(string glbFilePath, Engine_GmdSkeleton? gmd)
    {
        if (string.IsNullOrWhiteSpace(glbFilePath) || !File.Exists(glbFilePath) || gmd is null || gmd.Attachments.Length == 0)
        {
            return;
        }

        try
        {
            byte[] raw = File.ReadAllBytes(glbFilePath);
            byte[] patched = PatchGlbGmdDummyNamesAndExtras(raw, gmd);
            if (!ReferenceEquals(raw, patched))
            {
                File.WriteAllBytes(glbFilePath, patched);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Post-processes a <c>.glb</c> byte buffer so that every child under <c>GmdDummies</c> carries its
    /// exact dotted Fable II socket name and <c>extras.gmd_dummy_name</c> in the glTF JSON chunk.
    /// </summary>
    public static byte[] PatchGlbGmdDummyNamesAndExtras(byte[] glbBytes, Engine_GmdSkeleton? gmd)
    {
        if (glbBytes is null || glbBytes.Length < 20 || gmd is null || gmd.Attachments.Length == 0)
        {
            return glbBytes ?? Array.Empty<byte>();
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.AsSpan(0, 4));
        if (magic != 0x46546C67u) // "glTF"
        {
            return glbBytes;
        }

        uint jsonChunkLen = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.AsSpan(12, 4));
        uint jsonChunkType = BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.AsSpan(16, 4));
        if (jsonChunkType != 0x4E4F534Au || 20 + jsonChunkLen > glbBytes.Length)
        {
            return glbBytes;
        }

        try
        {
            string jsonStr = Encoding.UTF8.GetString(glbBytes, 20, (int)jsonChunkLen);
            JsonNode? rootNode = JsonNode.Parse(jsonStr);
            if (rootNode is not JsonObject rootObj ||
                rootObj["nodes"] is not JsonArray nodesArr)
            {
                return glbBytes;
            }

            // Locate the "GmdDummies" node
            JsonArray? dummyChildren = null;
            foreach (JsonNode? n in nodesArr)
            {
                if (n is JsonObject nodeObj &&
                    string.Equals(nodeObj["name"]?.GetValue<string>(), "GmdDummies", StringComparison.OrdinalIgnoreCase) &&
                    nodeObj["children"] is JsonArray chArr)
                {
                    dummyChildren = chArr;
                    break;
                }
            }

            if (dummyChildren is null || dummyChildren.Count == 0)
            {
                return glbBytes;
            }

            var seenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int count = Math.Min(dummyChildren.Count, gmd.Attachments.Length);
            for (int i = 0; i < count; i++)
            {
                int childNodeIdx = dummyChildren[i]?.GetValue<int>() ?? -1;
                if (childNodeIdx < 0 || childNodeIdx >= nodesArr.Count ||
                    nodesArr[childNodeIdx] is not JsonObject childObj)
                {
                    continue;
                }

                GmdAttachment att = gmd.Attachments[i];
                seenCounts.TryGetValue(att.Name, out int seen);
                seenCounts[att.Name] = seen + 1;

                string exactDisplayName = seen == 0 ? att.Name : $"{att.Name}.{seen:D3}";
                childObj["name"] = exactDisplayName;

                JsonObject extrasObj = childObj["extras"] as JsonObject ?? new JsonObject();
                extrasObj["gmd_dummy_name"] = att.Name;
                extrasObj["gmd_parent_node"] = att.Node;
                extrasObj["gmd_index"] = i;
                childObj["extras"] = extrasObj;
            }

            byte[] newJsonBytes = Encoding.UTF8.GetBytes(rootObj.ToJsonString());
            int pad = (4 - (newJsonBytes.Length % 4)) % 4;
            int newJsonChunkLen = newJsonBytes.Length + pad;

            int restOffset = 20 + (int)jsonChunkLen;
            int restLen = glbBytes.Length - restOffset;
            int totalLen = 20 + newJsonChunkLen + restLen;

            var output = new byte[totalLen];
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), 0x46546C67u);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), BinaryPrimitives.ReadUInt32LittleEndian(glbBytes.AsSpan(4, 4)));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8, 4), (uint)totalLen);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12, 4), (uint)newJsonChunkLen);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16, 4), 0x4E4F534Au);

            Array.Copy(newJsonBytes, 0, output, 20, newJsonBytes.Length);
            for (int p = 0; p < pad; p++)
            {
                output[20 + newJsonBytes.Length + p] = (byte)' ';
            }

            if (restLen > 0)
            {
                Array.Copy(glbBytes, restOffset, output, 20 + newJsonChunkLen, restLen);
            }

            return output;
        }
        catch
        {
            return glbBytes;
        }
    }

    /// <summary>
    /// Extracts any authored GMD socket Empty nodes from a <c>.glb</c> or <c>.gltf</c> binary, merges them
    /// with <paramref name="templateGmd"/>, and serializes a new <c>.mdl.gmd</c> binary.
    /// Returns <c>true</c> if the glTF contained authored GMD socket nodes; <c>false</c> if none were present.
    /// </summary>
    public static bool TryExtractGmdFromGltf(
        byte[] gltfBytes,
        Engine_GmdSkeleton? templateGmd,
        out byte[] rebuiltGmdBytes,
        out List<GmdAttachment> extractedAttachments)
    {
        rebuiltGmdBytes = Array.Empty<byte>();
        extractedAttachments = new List<GmdAttachment>();

        if (!TryExtractJsonSlice(gltfBytes, out ReadOnlyMemory<byte> jsonSlice))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonSlice);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("nodes", out JsonElement nodesEl) ||
                nodesEl.ValueKind != JsonValueKind.Array ||
                nodesEl.GetArrayLength() == 0)
            {
                return false;
            }

            int nodeCount = nodesEl.GetArrayLength();
            var localTransforms = new Transform3D[nodeCount];
            var parents = new int[nodeCount];
            Array.Fill(parents, -1);
            var names = new string[nodeCount];
            var hasMesh = new bool[nodeCount];
            var extrasDummyNames = new string?[nodeCount];
            var extrasParentNodes = new int?[nodeCount];
            var childrenMap = new List<int>[nodeCount];

            for (int i = 0; i < nodeCount; i++)
            {
                childrenMap[i] = new List<int>();
                JsonElement nodeEl = nodesEl[i];
                names[i] = nodeEl.TryGetProperty("name", out JsonElement nEl) && nEl.ValueKind == JsonValueKind.String
                    ? nEl.GetString() ?? string.Empty
                    : string.Empty;
                hasMesh[i] = nodeEl.TryGetProperty("mesh", out JsonElement mEl) && mEl.ValueKind == JsonValueKind.Number;
                localTransforms[i] = ParseGltfNodeLocalTransform(nodeEl);

                if (nodeEl.TryGetProperty("extras", out JsonElement extEl) && extEl.ValueKind == JsonValueKind.Object)
                {
                    if (extEl.TryGetProperty("gmd_dummy_name", out JsonElement dnEl) && dnEl.ValueKind == JsonValueKind.String)
                    {
                        extrasDummyNames[i] = dnEl.GetString();
                    }
                    if (extEl.TryGetProperty("gmd_parent_node", out JsonElement pnEl) && pnEl.TryGetInt32(out int pnVal))
                    {
                        extrasParentNodes[i] = pnVal;
                    }
                }
            }

            for (int i = 0; i < nodeCount; i++)
            {
                JsonElement nodeEl = nodesEl[i];
                if (nodeEl.TryGetProperty("children", out JsonElement chEl) && chEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement childIdxEl in chEl.EnumerateArray())
                    {
                        if (childIdxEl.TryGetInt32(out int childIdx) && childIdx >= 0 && childIdx < nodeCount)
                        {
                            parents[childIdx] = i;
                            childrenMap[i].Add(childIdx);
                        }
                    }
                }
            }

            // Compute each glTF node's scene-root Transform3D (skipping the top-level scene wrapper root's own transform only if it is identity)
            Transform3D ComposeSceneTransform(int idx)
            {
                var world = Transform3D.Identity;
                for (int at = idx, guard = 0; at >= 0 && at < nodeCount && guard < 64; guard++)
                {
                    world = localTransforms[at] * world;
                    at = parents[at];
                }
                return world;
            }

            // Check if there is an explicit "GmdDummies" / "Dummies" / "Sockets" container node:
            int containerNodeIdx = -1;
            for (int i = 0; i < nodeCount; i++)
            {
                if (!hasMesh[i] && IsGmdContainerNodeName(names[i]))
                {
                    containerNodeIdx = i;
                    break;
                }
            }

            var candidateNodeIndices = new List<int>();
            bool hasExplicitContainer = containerNodeIdx >= 0;

            if (hasExplicitContainer)
            {
                void CollectContainerDescendants(int parentIdx)
                {
                    foreach (int childIdx in childrenMap[parentIdx])
                    {
                        if (!hasMesh[childIdx])
                        {
                            candidateNodeIndices.Add(childIdx);
                        }
                        CollectContainerDescendants(childIdx);
                    }
                }
                CollectContainerDescendants(containerNodeIdx);
            }

            // Also scan for any loose socket Empties outside the container (e.g. parented directly to the scene or Mesh in Blender):
            for (int i = 0; i < nodeCount; i++)
            {
                if (i == containerNodeIdx || hasMesh[i] || candidateNodeIndices.Contains(i))
                {
                    continue;
                }

                string? explicitExtra = extrasDummyNames[i];
                if (!string.IsNullOrWhiteSpace(explicitExtra) || IsRecognizedSocketNodeName(names[i], templateGmd))
                {
                    candidateNodeIndices.Add(i);
                }
            }

            if (candidateNodeIndices.Count == 0)
            {
                return false;
            }

            var rawExtracted = new List<GmdAttachment>(candidateNodeIndices.Count);
            foreach (int nodeIdx in candidateNodeIndices)
            {
                string socketName = ResolveCanonicalSocketName(names[nodeIdx], extrasDummyNames[nodeIdx], templateGmd);
                if (string.IsNullOrWhiteSpace(socketName))
                {
                    continue;
                }

                int parentBoneIdx = extrasParentNodes[nodeIdx] ?? ResolveTemplateParentBone(socketName, templateGmd);
                if (templateGmd is not null && templateGmd.Names.Length == 0 && parentBoneIdx >= 0)
                {
                    parentBoneIdx = -1;
                }

                Transform3D worldTransform = ComposeSceneTransform(nodeIdx);
                Transform3D cleanWorld = new(worldTransform.Basis.Orthonormalized(), worldTransform.Origin);

                Transform3D attachmentLocal = cleanWorld;
                if (parentBoneIdx >= 0 && templateGmd is not null && parentBoneIdx < templateGmd.Locals.Length)
                {
                    Transform3D boneWorld = templateGmd.ComposeNode(parentBoneIdx);
                    attachmentLocal = boneWorld.AffineInverse() * cleanWorld;
                    attachmentLocal = new Transform3D(attachmentLocal.Basis.Orthonormalized(), attachmentLocal.Origin);
                }

                rawExtracted.Add(new GmdAttachment(socketName, attachmentLocal, parentBoneIdx));
            }

            if (rawExtracted.Count == 0)
            {
                return false;
            }

            if (templateGmd is null || templateGmd.Attachments.Length == 0)
            {
                extractedAttachments = rawExtracted;
            }
            else
            {
                // Merge authored sockets onto templateGmd attachments so any untouched baseline sockets
                // (such as Weapon.Weapon.Sheath or particle FX emitters) remain intact:
                var merged = new List<GmdAttachment>(templateGmd.Attachments);
                foreach (GmdAttachment authored in rawExtracted)
                {
                    int existingIdx = merged.FindIndex(a => a.Name.Equals(authored.Name, StringComparison.OrdinalIgnoreCase));
                    if (existingIdx >= 0)
                    {
                        merged[existingIdx] = authored;
                    }
                    else
                    {
                        merged.Add(authored);
                    }
                }
                extractedAttachments = merged;
            }

            rebuiltGmdBytes = Write(templateGmd, extractedAttachments);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts the scene-space transform of the first mesh node in a <c>.glb</c> or <c>.gltf</c> binary.
    /// Useful when a creator scales or rotates a mesh in Blender Object Mode without applying transforms (<c>Ctrl+A</c>).
    /// </summary>
    public static bool TryExtractFirstMeshSceneTransform(byte[] gltfBytes, out Transform3D meshSceneTransform)
    {
        meshSceneTransform = Transform3D.Identity;
        if (!TryExtractJsonSlice(gltfBytes, out ReadOnlyMemory<byte> jsonSlice))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonSlice);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("nodes", out JsonElement nodesEl) ||
                nodesEl.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            int nodeCount = nodesEl.GetArrayLength();
            var localTransforms = new Transform3D[nodeCount];
            var parents = new int[nodeCount];
            Array.Fill(parents, -1);
            int firstMeshNode = -1;

            for (int i = 0; i < nodeCount; i++)
            {
                JsonElement nodeEl = nodesEl[i];
                localTransforms[i] = ParseGltfNodeLocalTransform(nodeEl);
                if (firstMeshNode < 0 &&
                    nodeEl.TryGetProperty("mesh", out JsonElement mEl) &&
                    mEl.ValueKind == JsonValueKind.Number)
                {
                    firstMeshNode = i;
                }

                if (nodeEl.TryGetProperty("children", out JsonElement chEl) && chEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement childIdxEl in chEl.EnumerateArray())
                    {
                        if (childIdxEl.TryGetInt32(out int childIdx) && childIdx >= 0 && childIdx < nodeCount)
                        {
                            parents[childIdx] = i;
                        }
                    }
                }
            }

            if (firstMeshNode < 0)
            {
                return false;
            }

            var world = Transform3D.Identity;
            for (int at = firstMeshNode, guard = 0; at >= 0 && at < nodeCount && guard < 64; guard++)
            {
                world = localTransforms[at] * world;
                at = parents[at];
            }

            meshSceneTransform = world;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Normalizes a Blender/glTF node name into its canonical Fable II <c>.mdl.gmd</c> socket name,
    /// stripping Blender duplicate suffixes (<c>.001</c>, <c>#1</c>) and mapping underscore-sanitized
    /// names back to their canonical dotted spelling.
    /// </summary>
    public static string ResolveCanonicalSocketName(
        string? rawNodeName,
        string? extrasDummyName,
        Engine_GmdSkeleton? templateGmd = null)
    {
        if (!string.IsNullOrWhiteSpace(extrasDummyName))
        {
            return extrasDummyName.Trim();
        }

        if (string.IsNullOrWhiteSpace(rawNodeName))
        {
            return string.Empty;
        }

        string candidate = rawNodeName.Trim();
        if (candidate.StartsWith("GMD:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("GMD_", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[4..].Trim();
        }

        // Strip #1, #2 duplicate suffixes
        candidate = Regex.Replace(candidate, @"#\d+$", string.Empty);

        // Strip Blender .001, .002 duplicate suffixes
        candidate = Regex.Replace(candidate, @"\.\d{3,}$", string.Empty);

        // Check if candidate matches a templateGmd attachment directly or with '_' -> '.' (or without trailing dot)
        if (templateGmd is not null)
        {
            foreach (GmdAttachment att in templateGmd.Attachments)
            {
                string trimmedAtt = att.Name.TrimEnd('.');
                if (att.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                    trimmedAtt.Equals(candidate.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ||
                    att.Name.Replace('.', '_').Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                    trimmedAtt.Replace('.', '_').Equals(candidate.TrimEnd('_'), StringComparison.OrdinalIgnoreCase))
                {
                    return att.Name;
                }
            }
        }

        // Check against StandardSockets (exact, trimmed-dot, and underscore-sanitized)
        foreach (GmdSocketReference std in StandardSockets)
        {
            string trimmedStd = std.Name.TrimEnd('.');
            if (std.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                trimmedStd.Equals(candidate.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ||
                std.Name.Replace('.', '_').Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                trimmedStd.Replace('.', '_').Equals(candidate.TrimEnd('_'), StringComparison.OrdinalIgnoreCase))
            {
                return std.Name;
            }
        }

        // Restore dots for known underscore-sanitized prefixes:
        if (candidate.StartsWith("Weapon_Weapon_", StringComparison.OrdinalIgnoreCase))
        {
            return "Weapon.Weapon." + candidate["Weapon_Weapon_".Length..].Replace('_', '.');
        }
        if (candidate.StartsWith("Weapon_FX_Particle_", StringComparison.OrdinalIgnoreCase))
        {
            string rest = candidate["Weapon_FX_Particle_".Length..];
            if (rest.StartsWith("Effect_", StringComparison.OrdinalIgnoreCase))
            {
                return "Weapon.FX.Particle." + rest.TrimEnd('_', '.') + ".";
            }
            return "Weapon.FX.Particle." + rest;
        }
        if (candidate.StartsWith("Prop_Carry_", StringComparison.OrdinalIgnoreCase))
        {
            return "Prop.Carry." + candidate["Prop_Carry_".Length..].Replace('_', '.');
        }
        if (candidate.StartsWith("Prop_FX_Particle_", StringComparison.OrdinalIgnoreCase))
        {
            string rest = candidate["Prop_FX_Particle_".Length..];
            if (rest.StartsWith("Effect_", StringComparison.OrdinalIgnoreCase))
            {
                return "Prop.FX.Particle." + rest.TrimEnd('_', '.') + ".";
            }
            return "Prop.FX.Particle." + rest;
        }
        if (candidate.StartsWith("Prop_Action_", StringComparison.OrdinalIgnoreCase))
        {
            return "Prop.Action." + candidate["Prop_Action_".Length..].Replace('_', '.');
        }
        if (candidate.StartsWith("Character_Carry_", StringComparison.OrdinalIgnoreCase))
        {
            return "Character.Carry." + candidate["Character_Carry_".Length..].Replace('_', '.');
        }
        if (candidate.StartsWith("Weapon_Projectile_", StringComparison.OrdinalIgnoreCase))
        {
            return "Weapon.Projectile." + candidate["Weapon_Projectile_".Length..].Replace('_', '.');
        }
        if (candidate.StartsWith("Prop_Layout_", StringComparison.OrdinalIgnoreCase))
        {
            return "Prop.Layout." + candidate["Prop_Layout_".Length..].Replace('_', '.');
        }

        return candidate;
    }

    private static bool IsGmdContainerNodeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string trimmed = Regex.Replace(name.Trim(), @"\.\d{3,}$", string.Empty);
        return trimmed.Equals("GmdDummies", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("GmdSockets", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecognizedSocketNodeName(string? rawName, Engine_GmdSkeleton? templateGmd)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return false;
        }

        string resolved = ResolveCanonicalSocketName(rawName, null, templateGmd);
        foreach (string prefix in KnownSocketPrefixes)
        {
            if (resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                rawName.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (templateGmd is not null &&
            templateGmd.Attachments.Any(a => a.Name.Equals(resolved, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return StandardSockets.Any(s => s.Name.Equals(resolved, StringComparison.OrdinalIgnoreCase));
    }

    private static int ResolveTemplateParentBone(string socketName, Engine_GmdSkeleton? templateGmd)
    {
        if (templateGmd is null)
        {
            return -1;
        }

        foreach (GmdAttachment att in templateGmd.Attachments)
        {
            if (att.Name.Equals(socketName, StringComparison.OrdinalIgnoreCase))
            {
                return att.Node;
            }
        }

        return -1;
    }

    private static Transform3D ParseGltfNodeLocalTransform(JsonElement nodeEl)
    {
        if (nodeEl.TryGetProperty("matrix", out JsonElement matEl) &&
            matEl.ValueKind == JsonValueKind.Array &&
            matEl.GetArrayLength() == 16)
        {
            Span<float> m = stackalloc float[16];
            int idx = 0;
            foreach (JsonElement fEl in matEl.EnumerateArray())
            {
                m[idx++] = fEl.TryGetSingle(out float v) ? v : 0f;
            }

            // glTF 4x4 matrix is column-major:
            var col0 = new Vector3(m[0], m[1], m[2]);
            var col1 = new Vector3(m[4], m[5], m[6]);
            var col2 = new Vector3(m[8], m[9], m[10]);
            var origin = new Vector3(m[12], m[13], m[14]);
            return new Transform3D(new Basis(col0, col1, col2), origin);
        }

        Vector3 translation = Vector3.Zero;
        if (nodeEl.TryGetProperty("translation", out JsonElement tEl) &&
            tEl.ValueKind == JsonValueKind.Array &&
            tEl.GetArrayLength() == 3)
        {
            translation = new Vector3(
                tEl[0].TryGetSingle(out float tx) ? tx : 0f,
                tEl[1].TryGetSingle(out float ty) ? ty : 0f,
                tEl[2].TryGetSingle(out float tz) ? tz : 0f);
        }

        Quaternion rotation = Quaternion.Identity;
        if (nodeEl.TryGetProperty("rotation", out JsonElement rEl) &&
            rEl.ValueKind == JsonValueKind.Array &&
            rEl.GetArrayLength() == 4)
        {
            var q = new Quaternion(
                rEl[0].TryGetSingle(out float qx) ? qx : 0f,
                rEl[1].TryGetSingle(out float qy) ? qy : 0f,
                rEl[2].TryGetSingle(out float qz) ? qz : 0f,
                rEl[3].TryGetSingle(out float qw) ? qw : 1f);
            if (q.LengthSquared() > 0.0001f)
            {
                rotation = q.Normalized();
            }
        }

        Vector3 scale = Vector3.One;
        if (nodeEl.TryGetProperty("scale", out JsonElement sEl) &&
            sEl.ValueKind == JsonValueKind.Array &&
            sEl.GetArrayLength() == 3)
        {
            scale = new Vector3(
                sEl[0].TryGetSingle(out float sx) ? sx : 1f,
                sEl[1].TryGetSingle(out float sy) ? sy : 1f,
                sEl[2].TryGetSingle(out float sz) ? sz : 1f);
        }

        Basis basis = new Basis(rotation).Scaled(scale);
        return new Transform3D(basis, translation);
    }

    private static bool TryExtractJsonSlice(byte[] gltfBytes, out ReadOnlyMemory<byte> jsonSlice)
    {
        jsonSlice = default;
        if (gltfBytes is null || gltfBytes.Length < 12)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(gltfBytes.AsSpan(0, 4));
        if (magic == 0x46546C67u) // "glTF" (.glb)
        {
            if (gltfBytes.Length < 20)
            {
                return false;
            }

            int pos = 12;
            while (pos + 8 <= gltfBytes.Length)
            {
                uint chunkLen = BinaryPrimitives.ReadUInt32LittleEndian(gltfBytes.AsSpan(pos, 4));
                uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(gltfBytes.AsSpan(pos + 4, 4));
                pos += 8;
                if (chunkLen > (uint)(gltfBytes.Length - pos))
                {
                    break;
                }

                if (chunkType == 0x4E4F534Au) // "JSON"
                {
                    jsonSlice = gltfBytes.AsMemory(pos, (int)chunkLen);
                    return true;
                }

                pos += (int)chunkLen;
            }

            return false;
        }

        jsonSlice = gltfBytes.AsMemory();
        return true;
    }

    private static void WriteStringZ(Stream stream, string value)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(value ?? string.Empty);
        stream.Write(ascii, 0, ascii.Length);
        stream.WriteByte(0);
    }

    private static void WriteF32(Stream stream, float value)
    {
        uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
        WriteU32(stream, bits);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteU32(byte[] buf, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset, 4), value);
    }
}

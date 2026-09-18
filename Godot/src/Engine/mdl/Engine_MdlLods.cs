using System;
using System.Collections.Generic;

namespace Refii.Models;

/// <summary>
/// Hangs a model's coarser LOD payloads on its LOD-0 surfaces, as the levels
/// <see cref="Engine_MdlMesh"/> hands Godot per surface.
///
/// MATCHING RULE. A coarser payload's piece belongs to the LOD-0 surface with the same
/// <see cref="Engine_MdlMerge.SurfaceKey"/> — material textures, shader type, tangent and bone
/// layout, region — which is the rule LOD 0's own pieces are merged into surfaces by. Every
/// shipped coarser payload repeats LOD 0's mesh headers, material strings and hide-region table
/// verbatim, while parts are dropped and same-material submeshes collapse as levels coarsen, so
/// a positional match does not hold and this one does.
///
/// VERTICES. Under the header's shared-stream flag (<see cref="Engine_MdlEngine.SharedVertexStreamFlag"/>,
/// set on every boneless shipped model) a coarser rigid part ships no vertex stream: its indices
/// address the LOD-0 rigid part its record names (<see cref="MdlMeshBuffer.SharesLod0Vertices"/>),
/// so its triangles reuse the surface's vertices through the source map and only a LOD-0 vertex
/// the surface did not already hold is appended. Otherwise a part ships its own vertices: those
/// are appended after the LOD-0 vertices with no source vertex, and a skinned part's bone ids are
/// re-mapped by name from the payload's bone table to LOD 0's (a by-name subset in every shipped
/// model).
///
/// KEY. The guest picks a mesh LOD in `sub_82218560`:
/// `lod = trunc(LodScreenFraction · dist · 2·tan(fovY/2) / (|scale| · model+96))` — level i once
/// the model's reference size (header +72) projects under LodScreenFraction/i of the screen
/// height; LodScreenFraction is the definition's, 0.05 by default (`sub_82AA2658`). Godot draws
/// level i once `key · scale / (dist · 2·tan(fovX/2)) &lt; threshold / width`; with the viewport's
/// threshold set to `pixels · height / 720` the aspect cancels and
/// `key_i = reference · i / (LodScreenFraction · 720)` reproduces the guest's switch distances
/// at any resolution, with `mesh_lod_pixels` = 1 as parity. A model without a reference falls
/// back to the level's mean triangle edge length. A level whose key does not exceed the finer
/// level's is dropped for that surface, as is a level the surface's layout cannot take (a
/// lightmapped surface, whose second UV set only LOD 0's vertices carry). A dropped level never
/// fails the mesh.
/// </summary>
public static class Engine_MdlLods
{
    /// <summary>The drawable's default LodScreenFraction (`sub_82AA2658` @0x82aa2728); a definition may state its own, which the mesh does not know.</summary>
    public const float LodScreenFraction = 0.05f;

    /// <summary>The viewport height the keys are computed for; the viewport threshold scales with height/720.</summary>
    public const float ReferenceHeight = 720f;

    /// <summary>
    /// Attaches every usable coarser level to <paramref name="surfaces"/> in place, growing a
    /// surface's vertex arrays where a level needs vertices the surface does not hold.
    /// </summary>
    /// <returns>Levels attached and levels dropped, over all surfaces.</returns>
    public static (int Attached, int Dropped) Attach(
        byte[] data, MdlInfo info, List<MdlGeometry> surfaces)
    {
        if (info.Lods.Count == 0 || surfaces.Count == 0)
        {
            return (0, 0);
        }

        var targets = new List<Target>(surfaces.Count);
        var byKey = new Dictionary<string, Target>();
        bool lightmapped = info.LightmapUvs.Length > 0 && info.SkinnedCount == 0;
        foreach (MdlGeometry geo in surfaces)
        {
            var target = new Target(geo, lightmapped, info.LodReference > 0f);
            targets.Add(target);
            byKey.TryAdd(Engine_MdlMerge.SurfaceKey(geo), target);
        }

        int attached = 0;
        int dropped = 0;

        int level = 0;
        foreach (MdlLod lod in info.Lods)
        {
            ++level;
            float guestKey = info.LodReference > 0f
                ? info.LodReference * level / (LodScreenFraction * ReferenceHeight)
                : 0f;
            int[]? boneRemap = RemapBones(lod, info);
            if (boneRemap is null)
            {
                dropped += targets.Count;
                continue;
            }

            if (!Engine_MdlGeometry.TryBuild(
                    data, lod.MeshBuffers, lod.Meshes, lod.HideRegions, lod.Bones.Count,
                    out List<MdlGeometry> pieces))
            {
                dropped += targets.Count;
                continue;
            }

            foreach (Target target in targets)
            {
                target.BeginLevel();
            }

            foreach (MdlGeometry piece in pieces)
            {
                if (piece.VertexCount == 0 || piece.TriangleCount == 0 ||
                    !byKey.TryGetValue(Engine_MdlMerge.SurfaceKey(piece), out Target? target))
                {
                    continue;
                }

                target.Take(piece, boneRemap);
            }

            foreach (Target target in targets)
            {
                if (target.CommitLevel(guestKey))
                {
                    ++attached;
                }
                else if (target.LevelHadTriangles)
                {
                    ++dropped;
                }
            }
        }

        foreach (Target target in targets)
        {
            target.Finish();
        }

        return (attached, dropped);
    }

    /// <summary>
    /// The payload's bone index to LOD 0's, by name; null when a name is not in LOD 0's table.
    /// </summary>
    private static int[]? RemapBones(MdlLod lod, MdlInfo info)
    {
        var remap = new int[lod.Bones.Count];
        if (lod.Bones.Count == 0)
        {
            return remap;
        }

        var byName = new Dictionary<string, int>(info.Bones.Count);
        for (int i = 0; i < info.Bones.Count; i++)
        {
            byName.TryAdd(info.Bones[i].Name, i);
        }

        for (int i = 0; i < lod.Bones.Count; i++)
        {
            if (!byName.TryGetValue(lod.Bones[i].Name, out remap[i]))
            {
                return null;
            }
        }

        return remap;
    }

    /// <summary>One LOD-0 surface being grown: its committed vertices, and the level under way.</summary>
    private sealed class Target
    {
        private readonly MdlGeometry _geo;
        private readonly bool _lightmapped;
        private readonly int _baseCount;

        // Committed growth beyond the surface's own vertices, and LOD 0's numbering of each.
        private readonly List<float> _positions = new();
        private readonly List<float> _normals = new();
        private readonly List<float> _uvs = new();
        private readonly List<float> _tangents = new();
        private readonly List<int> _boneIds = new();
        private readonly List<float> _boneWeights = new();
        private readonly List<int> _source = new();
        private readonly Dictionary<int, int> _localOfSource = new();

        // The level under way: staged, and thrown away unless the level is kept.
        private readonly List<uint> _level = new();
        private readonly List<float> _stagedPositions = new();
        private readonly List<float> _stagedNormals = new();
        private readonly List<float> _stagedUvs = new();
        private readonly List<float> _stagedTangents = new();
        private readonly List<int> _stagedBoneIds = new();
        private readonly List<float> _stagedBoneWeights = new();
        private readonly List<int> _stagedSource = new();
        private readonly Dictionary<int, int> _stagedLocalOfSource = new();
        private bool _levelRefused;
        private float _lastEdge;

        public Target(MdlGeometry geo, bool lightmapped, bool guestKeyed)
        {
            _geo = geo;
            _lightmapped = lightmapped;
            _baseCount = geo.VertexCount;
            _lastEdge = guestKeyed ? 0f : MeanEdge(geo.Indices, 0, geo.Indices.Length);

            if (geo.SourceVertices.Length == _baseCount)
            {
                for (int v = 0; v < _baseCount; v++)
                {
                    if (geo.SourceVertices[v] >= 0)
                    {
                        _localOfSource.TryAdd(geo.SourceVertices[v], v);
                    }
                }
            }
        }

        public bool LevelHadTriangles => _level.Count >= 3;

        public void BeginLevel()
        {
            _level.Clear();
            _stagedPositions.Clear();
            _stagedNormals.Clear();
            _stagedUvs.Clear();
            _stagedTangents.Clear();
            _stagedBoneIds.Clear();
            _stagedBoneWeights.Clear();
            _stagedSource.Clear();
            _stagedLocalOfSource.Clear();
            _levelRefused = false;
        }

        public void Take(MdlGeometry piece, int[] boneRemap)
        {
            if (_levelRefused)
            {
                return;
            }

            // Same layout or nothing: the surface's arrays are the level's arrays.
            if (piece.HasTangents != _geo.HasTangents || piece.HasBones != _geo.HasBones ||
                (!piece.SharesLod0Vertices && _lightmapped))
            {
                _levelRefused = true;
                return;
            }

            var local = new uint[piece.VertexCount];
            for (int v = 0; v < piece.VertexCount; v++)
            {
                int source = piece.SharesLod0Vertices && v < piece.SourceVertices.Length
                    ? piece.SourceVertices[v]
                    : -1;

                if (source >= 0)
                {
                    if (_localOfSource.TryGetValue(source, out int have) ||
                        _stagedLocalOfSource.TryGetValue(source, out have))
                    {
                        local[v] = (uint)have;
                        continue;
                    }
                }

                local[v] = (uint)Stage(piece, v, source, boneRemap);
            }

            foreach (uint index in piece.Indices)
            {
                _level.Add(index < local.Length ? local[index] : 0);
            }
        }

        /// <summary>Copies one of the piece's vertices into the staged growth; returns its local index.</summary>
        private int Stage(MdlGeometry piece, int v, int source, int[] boneRemap)
        {
            int local = _baseCount + _source.Count + _stagedSource.Count;

            _stagedPositions.Add(piece.Positions[v * 3]);
            _stagedPositions.Add(piece.Positions[v * 3 + 1]);
            _stagedPositions.Add(piece.Positions[v * 3 + 2]);
            _stagedNormals.Add(piece.Normals[v * 3]);
            _stagedNormals.Add(piece.Normals[v * 3 + 1]);
            _stagedNormals.Add(piece.Normals[v * 3 + 2]);
            _stagedUvs.Add(piece.Uvs[v * 2]);
            _stagedUvs.Add(piece.Uvs[v * 2 + 1]);

            if (_geo.HasTangents)
            {
                for (int k = 0; k < 4; k++)
                {
                    _stagedTangents.Add(piece.Tangents[v * 4 + k]);
                }
            }

            if (_geo.HasBones)
            {
                for (int k = 0; k < 4; k++)
                {
                    int bone = piece.BoneIds[v * 4 + k];
                    _stagedBoneIds.Add(
                        piece.SharesLod0Vertices ? bone :
                        bone >= 0 && bone < boneRemap.Length ? boneRemap[bone] : 0);
                    _stagedBoneWeights.Add(piece.BoneWeights[v * 4 + k]);
                }
            }

            _stagedSource.Add(source);
            if (source >= 0)
            {
                _stagedLocalOfSource[source] = local;
            }

            return local;
        }

        /// <summary>Keeps the level under the guest's key, or its mean edge when the model has no reference; true when kept.</summary>
        public bool CommitLevel(float guestKey)
        {
            if (_levelRefused || _level.Count < 3)
            {
                return false;
            }

            int triangles = _level.Count - (_level.Count % 3);
            if (triangles < _level.Count)
            {
                _level.RemoveRange(triangles, _level.Count - triangles);
            }

            float edge = guestKey > 0f ? guestKey : MeanEdge(_level, 0, _level.Count);
            if (!(edge > _lastEdge))
            {
                return false;
            }

            _positions.AddRange(_stagedPositions);
            _normals.AddRange(_stagedNormals);
            _uvs.AddRange(_stagedUvs);
            _tangents.AddRange(_stagedTangents);
            _boneIds.AddRange(_stagedBoneIds);
            _boneWeights.AddRange(_stagedBoneWeights);
            _source.AddRange(_stagedSource);
            foreach (KeyValuePair<int, int> entry in _stagedLocalOfSource)
            {
                _localOfSource.TryAdd(entry.Key, entry.Value);
            }

            _geo.Lods.Add((edge, _level.ToArray()));
            _lastEdge = edge;
            return true;
        }

        /// <summary>Writes the committed growth into the surface's arrays.</summary>
        public void Finish()
        {
            if (_source.Count == 0)
            {
                return;
            }

            _geo.Positions = Concat(_geo.Positions, _positions);
            _geo.Normals = Concat(_geo.Normals, _normals);
            _geo.Uvs = Concat(_geo.Uvs, _uvs);
            if (_geo.HasTangents)
            {
                _geo.Tangents = Concat(_geo.Tangents, _tangents);
            }

            if (_geo.HasBones)
            {
                _geo.BoneIds = Concat(_geo.BoneIds, _boneIds);
                _geo.BoneWeights = Concat(_geo.BoneWeights, _boneWeights);
            }

            // The map keeps its length equal to the vertex count, or it is not a map at all.
            var source = new int[_baseCount + _source.Count];
            if (_geo.SourceVertices.Length == _baseCount)
            {
                Array.Copy(_geo.SourceVertices, source, _baseCount);
            }
            else
            {
                Array.Fill(source, -1, 0, _baseCount);
            }

            _source.CopyTo(source, _baseCount);
            _geo.SourceVertices = source;
        }

        private static T[] Concat<T>(T[] head, List<T> tail)
        {
            var result = new T[head.Length + tail.Count];
            Array.Copy(head, result, head.Length);
            tail.CopyTo(result, head.Length);
            return result;
        }

        private float MeanEdge(IReadOnlyList<uint> indices, int start, int count)
        {
            double total = 0;
            int edges = 0;
            int end = start + count - (count % 3);
            for (int i = start; i < end; i += 3)
            {
                total += Distance(indices[i], indices[i + 1]);
                total += Distance(indices[i + 1], indices[i + 2]);
                total += Distance(indices[i + 2], indices[i]);
                edges += 3;
            }

            return edges == 0 ? 0f : (float)(total / edges);
        }

        /// <summary>Between two local vertices, wherever each currently lives.</summary>
        private double Distance(uint a, uint b)
        {
            Position(a, out float ax, out float ay, out float az);
            Position(b, out float bx, out float by, out float bz);
            double dx = ax - bx;
            double dy = ay - by;
            double dz = az - bz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private void Position(uint local, out float x, out float y, out float z)
        {
            int v = (int)local;
            if (v < _baseCount)
            {
                x = _geo.Positions[v * 3];
                y = _geo.Positions[v * 3 + 1];
                z = _geo.Positions[v * 3 + 2];
                return;
            }

            v -= _baseCount;
            if (v < _source.Count)
            {
                x = _positions[v * 3];
                y = _positions[v * 3 + 1];
                z = _positions[v * 3 + 2];
                return;
            }

            v -= _source.Count;
            if (v < _stagedSource.Count)
            {
                x = _stagedPositions[v * 3];
                y = _stagedPositions[v * 3 + 1];
                z = _stagedPositions[v * 3 + 2];
                return;
            }

            x = y = z = 0f;
        }
    }
}

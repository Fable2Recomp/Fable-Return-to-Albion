using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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

        data = info.Body ?? data;

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
        var shared = new SharedVertices(data);
        Dictionary<string, int>? boneByName = null;

        int level = 0;
        foreach (MdlLod lod in info.Lods)
        {
            ++level;
            float guestKey = info.LodReference > 0f
                ? info.LodReference * level / (LodScreenFraction * ReferenceHeight)
                : 0f;
            int[]? boneRemap = RemapBones(lod, info, ref boneByName);
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

                target.Take(piece, boneRemap, shared);
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

        shared.Release();
        return (attached, dropped);
    }

    /// <summary>
    /// The payload's bone index to LOD 0's, by name; null when a name is not in LOD 0's table.
    /// </summary>
    private static int[]? RemapBones(MdlLod lod, MdlInfo info, ref Dictionary<string, int>? boneByName)
    {
        var remap = new int[lod.Bones.Count];
        if (lod.Bones.Count == 0)
        {
            return remap;
        }

        Dictionary<string, int>? byName = boneByName;
        if (byName is null)
        {
            byName = new Dictionary<string, int>(info.Bones.Count);
            for (int i = 0; i < info.Bones.Count; i++)
            {
                byName.TryAdd(info.Bones[i].Name, i);
            }

            boneByName = byName;
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

    /// <summary>
    /// LOD 0's rigid buffers, decoded for one model at the first vertex a level must append from
    /// each, and shared by every level and piece over them.
    /// </summary>
    private sealed class SharedVertices
    {
        private readonly byte[] _data;
        private readonly List<Decoded> _decoded = new();

        public SharedVertices(byte[] data)
        {
            _data = data;
        }

        /// <summary>One buffer's vertices; the pieces over it have already checked it fits the body.</summary>
        public Decoded Of(MdlMeshBuffer buffer)
        {
            foreach (Decoded known in _decoded)
            {
                if (known.Offset == buffer.VertexOffset)
                {
                    return known;
                }
            }

            Engine_MdlGeometry.TryRentRigidVertices(_data, buffer,
                out float[] positions, out float[] uvs, out float[] normals, out float[] tangents);
            var decoded = new Decoded(buffer.VertexOffset, positions, uvs, normals, tangents);
            _decoded.Add(decoded);
            return decoded;
        }

        public void Release()
        {
            foreach (Decoded decoded in _decoded)
            {
                Engine_MdlGeometry.ReturnRigidVertices(
                    decoded.Positions, decoded.Uvs, decoded.Normals, decoded.Tangents);
            }

            _decoded.Clear();
        }

        internal sealed record Decoded(int Offset, float[] Positions, float[] Uvs, float[] Normals, float[] Tangents);
    }

    /// <summary>One LOD-0 surface being grown: its committed vertices, and the level under way.</summary>
    private sealed class Target
    {
        private readonly MdlGeometry _geo;
        private readonly bool _lightmapped;
        private readonly int _baseCount;

        // Which local vertex holds a source vertex, among the surface's own: a table over the
        // range of source numbers it holds (local + 1, 0 for none), rented at the first piece that
        // asks and only then. A model whose coarser levels ship their own vertices never does.
        private int[]? _baseLocals;
        private int _baseSourceMin;
        private int _baseSourceRange;

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
            _lastEdge = guestKeyed ? 0f : MeanEdge(geo.Indices);
        }

        public bool LevelHadTriangles => _level.Count >= 3;

        private void RentBaseLocals()
        {
            int min = int.MaxValue, max = -1;
            int[] sources = _geo.SourceVertices;
            bool mapped = sources.Length == _baseCount;
            for (int v = 0; mapped && v < _baseCount; v++)
            {
                if (sources[v] >= 0)
                {
                    min = Math.Min(min, sources[v]);
                    max = Math.Max(max, sources[v]);
                }
            }

            _baseSourceMin = min;
            _baseSourceRange = max < 0 ? 0 : max - min + 1;
            _baseLocals = ArrayPool<int>.Shared.Rent(_baseSourceRange);
            Array.Clear(_baseLocals, 0, _baseSourceRange);
            for (int v = 0; mapped && v < _baseCount; v++)
            {
                // The first vertex with a source number keeps it: a merged surface can hold one twice.
                if (sources[v] >= 0 && _baseLocals[sources[v] - min] == 0)
                {
                    _baseLocals[sources[v] - min] = v + 1;
                }
            }
        }

        private bool TryLocal(int source, out int local)
        {
            if (_baseLocals is null)
            {
                RentBaseLocals();
            }

            uint at = (uint)(source - _baseSourceMin);
            if (at < (uint)_baseSourceRange && _baseLocals![at] != 0)
            {
                local = _baseLocals[at] - 1;
                return true;
            }

            return _localOfSource.TryGetValue(source, out local) ||
                _stagedLocalOfSource.TryGetValue(source, out local);
        }

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

        public void Take(MdlGeometry piece, int[] boneRemap, SharedVertices shared)
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

            int vertices = piece.VertexCount;
            uint[] local = ArrayPool<uint>.Shared.Rent(vertices);
            for (int v = 0; v < vertices; v++)
            {
                int source = piece.SharesLod0Vertices && v < piece.SourceVertices.Length
                    ? piece.SourceVertices[v]
                    : -1;

                if (source >= 0 && TryLocal(source, out int have))
                {
                    local[v] = (uint)have;
                    continue;
                }

                local[v] = (uint)Stage(piece, v, source, boneRemap, shared);
            }

            uint[] indices = piece.Indices;
            int at = _level.Count;
            CollectionsMarshal.SetCount(_level, at + indices.Length);
            Span<uint> level = CollectionsMarshal.AsSpan(_level)[at..];
            for (int i = 0; i < indices.Length; i++)
            {
                uint index = indices[i];
                level[i] = index < (uint)vertices ? local[index] : 0;
            }

            ArrayPool<uint>.Shared.Return(local);
        }

        /// <summary>Copies one of the piece's vertices into the staged growth; returns its local index.</summary>
        private int Stage(MdlGeometry piece, int v, int source, int[] boneRemap, SharedVertices shared)
        {
            int local = _baseCount + _source.Count + _stagedSource.Count;

            float[] positions = piece.Positions, normals = piece.Normals, uvs = piece.Uvs, tangents = piece.Tangents;
            if (piece.VerticesIn is { } buffer)
            {
                SharedVertices.Decoded decoded = shared.Of(buffer);
                (positions, normals, uvs, tangents) = (decoded.Positions, decoded.Normals, decoded.Uvs, decoded.Tangents);
                v = source - buffer.SharedVertexBase;
            }

            _stagedPositions.Add(positions[v * 3]);
            _stagedPositions.Add(positions[v * 3 + 1]);
            _stagedPositions.Add(positions[v * 3 + 2]);
            _stagedNormals.Add(normals[v * 3]);
            _stagedNormals.Add(normals[v * 3 + 1]);
            _stagedNormals.Add(normals[v * 3 + 2]);
            _stagedUvs.Add(uvs[v * 2]);
            _stagedUvs.Add(uvs[v * 2 + 1]);

            if (_geo.HasTangents)
            {
                for (int k = 0; k < 4; k++)
                {
                    _stagedTangents.Add(tangents[v * 4 + k]);
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

            float edge = guestKey > 0f ? guestKey : MeanEdge(CollectionsMarshal.AsSpan(_level));
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
            if (_baseLocals is not null)
            {
                ArrayPool<int>.Shared.Return(_baseLocals);
                _baseLocals = null;
            }

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

        private float MeanEdge(ReadOnlySpan<uint> indices)
        {
            double total = 0;
            int edges = 0;
            int end = indices.Length - (indices.Length % 3);
            for (int i = 0; i < end; i += 3)
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

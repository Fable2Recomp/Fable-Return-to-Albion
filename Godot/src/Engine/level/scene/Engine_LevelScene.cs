using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public static class Engine_LevelMesh
{
    public static ArrayMesh ToArrayMesh(TerrainMesh terrain, string surfaceName)
    {
        int count = terrain.VertexCount;
        var vertices = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new Vector2[count];

        for (int v = 0; v < count; v++)
        {
            vertices[v] = new Vector3(
                terrain.Positions[(v * 3) + 0],
                terrain.Positions[(v * 3) + 1],
                terrain.Positions[(v * 3) + 2]);
            normals[v] = new Vector3(
                terrain.Normals[(v * 3) + 0],
                terrain.Normals[(v * 3) + 1],
                terrain.Normals[(v * 3) + 2]);
            uvs[v] = new Vector2(terrain.Uvs[(v * 2) + 0], terrain.Uvs[(v * 2) + 1]);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = terrain.Indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetName(0, surfaceName);
        return mesh;
    }
}

public static class Engine_LevelScene
{
    public const string TerrainNodeName = "Terrain";

    public const string AdjacentNodeName = "AdjacentTerrain";

    public static bool TryBuild(
        LevelEntry level, Func<string, byte[]?>? source, out string scenePath, out string error)
    {
        scenePath = level.ScenePath;
        error = string.Empty;

        if (level.HeightfieldPath.Length == 0)
        {
            error = "level has no heightfield";
            return false;
        }

        if (!EnsureDirectory(level, out error))
        {
            return false;
        }

        // Nothing is baked into the project. The terrain mesh and its albedo are ordinary
        // game:// resources, so the scene stores only a reference to each and the bank
        // loaders build them on demand. A level costs a few hundred bytes on disk instead
        // of tens of megabytes of .res, and re-exporting the game data is picked up
        // without regenerating anything.
        string meshPath = Engine_BnkTerrainLoader.PathPrefix + level.HeightfieldPath;
        var terrainMesh = ResourceLoader.Load<ArrayMesh>(meshPath, "ArrayMesh");
        if (terrainMesh is null)
        {
            error = $"terrain mesh did not load from {meshPath}";
            return false;
        }

        var root = new Node3D
        {
            Name = level.SceneNodeName,
            Scale = new Vector3(-1f, 1f, 1f),
        };

        var terrainNode = new MeshInstance3D
        {
            Name = TerrainNodeName,
            Mesh = terrainMesh,
        };

        if (level.MainEhfPath.Length != 0)
        {
            terrainNode.MaterialOverride = LoadTerrainMaterial(level.MainEhfPath);
        }

        root.AddChild(terrainNode);
        terrainNode.Owner = root;

        // The vista IS embedded, unlike the terrain. It is a couple of hundred triangles
        // built from several .ehf at once, which no single game:// path names, and at that
        // size inlining costs nothing.
        TerrainMesh? vista = BuildAdjacent(level, source);
        if (vista is not null)
        {
            var vistaNode = new MeshInstance3D
            {
                Name = AdjacentNodeName,
                Mesh = Engine_LevelMesh.ToArrayMesh(vista, AdjacentNodeName),
            };

            root.AddChild(vistaNode);
            vistaNode.Owner = root;
        }

        var packed = new PackedScene();
        Error packedResult = packed.Pack(root);
        if (packedResult != Error.Ok)
        {
            root.QueueFree();
            error = $"could not pack scene: {packedResult}";
            return false;
        }

        Error saved = ResourceSaver.Save(packed, level.ScenePath);
        root.QueueFree();

        if (saved != Error.Ok)
        {
            error = $"could not save scene to {level.ScenePath}: {saved}";
            return false;
        }

        return true;
    }

    private static Material? LoadTerrainMaterial(string ehfPath)
    {
        string materialPath =
            Engine_BnkTerrainMaterialLoader.PathPrefix + ehfPath +
            Engine_BnkTerrainMaterialLoader.PathSuffix;

        var splat = ResourceLoader.Load<ShaderMaterial>(materialPath, "ShaderMaterial");
        if (splat is not null)
        {
            return splat;
        }

        string albedoPath = Engine_BnkTerrainAlbedoLoader.PathPrefix + ehfPath;
        var albedo = ResourceLoader.Load<Texture2D>(albedoPath, "Texture2D");
        if (albedo is null)
        {
            return null;
        }

        return new StandardMaterial3D
        {
            AlbedoTexture = albedo,
            Metallic = 0f,
            Roughness = 0.9f,
        };
    }

    private static TerrainMesh? BuildAdjacent(LevelEntry level, Func<string, byte[]?>? source)
    {
        if (source is null || level.AdjacentEhfPaths.Length == 0)
        {
            return null;
        }

        var meshes = new List<TerrainMesh>();
        foreach (string path in level.AdjacentEhfPaths)
        {
            byte[]? bytes = source(path);
            if (bytes is null)
            {
                continue;
            }

            if (!Engine_EhfPatches.TryBuild(bytes, out List<EhfVistaPatch> patches, out string why))
            {
                GD.Print($"[level] {level}: adjacent {path} skipped ({why})");
                continue;
            }

            foreach (EhfVistaPatch patch in patches)
            {
                if (patch.Mesh.TriangleCount != 0)
                {
                    meshes.Add(patch.Mesh);
                }
            }
        }

        return meshes.Count == 0 ? null : Concatenate(meshes);
    }

    private static TerrainMesh Concatenate(List<TerrainMesh> meshes)
    {
        int vertices = 0;
        int indices = 0;
        foreach (TerrainMesh mesh in meshes)
        {
            vertices += mesh.VertexCount;
            indices += mesh.Indices.Length;
        }

        var positions = new float[vertices * 3];
        var normals = new float[vertices * 3];
        var uvs = new float[vertices * 2];
        var merged = new int[indices];

        int vertexBase = 0;
        int indexCursor = 0;
        float lowest = float.PositiveInfinity;
        float highest = float.NegativeInfinity;

        foreach (TerrainMesh mesh in meshes)
        {
            int count = mesh.VertexCount;
            Array.Copy(mesh.Positions, 0, positions, vertexBase * 3, count * 3);
            Array.Copy(mesh.Normals, 0, normals, vertexBase * 3, count * 3);
            Array.Copy(mesh.Uvs, 0, uvs, vertexBase * 2, count * 2);

            foreach (int index in mesh.Indices)
            {
                merged[indexCursor++] = index + vertexBase;
            }

            vertexBase += count;
            lowest = MathF.Min(lowest, mesh.MinHeight);
            highest = MathF.Max(highest, mesh.MaxHeight);
        }

        return new TerrainMesh
        {
            Width = 0,
            Height = 0,
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Indices = merged,
            MinHeight = float.IsFinite(lowest) ? lowest : 0f,
            MaxHeight = float.IsFinite(highest) ? highest : 0f,
        };
    }

    private static bool EnsureDirectory(LevelEntry level, out string error)
    {
        error = string.Empty;

        string scene = level.ScenePath;
        string directory = scene[..scene.LastIndexOf('/')];

        Error made = DirAccess.MakeDirRecursiveAbsolute(directory);
        if (made is Error.Ok or Error.AlreadyExists)
        {
            return true;
        }

        error = $"could not create {directory}: {made}";
        return false;
    }
}

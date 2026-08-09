using System;
using System.Collections.Generic;
using Godot;
using Refii.Lighting;

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

    public const string WaterNodeName = "Water";

    public static bool TryBuild(
        LevelEntry level, Func<string, byte[]?>? source, out string scenePath, out string error) =>
        TryBuild(level, source, null, out scenePath, out error);

    public static bool TryBuild(
        LevelEntry level,
        Func<string, byte[]?>? source,
        Func<string, string?>? findByName,
        out string scenePath,
        out string error)
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

        AddWater(level, source, findByName, root);

        LightmapContainer? bakedLightmaps = null;
        LightProbeField? probeField = null;
        if (source is not null)
        {
            Engine_LevelGdb.TryRead(level, p => source(p), out string bakedGdbPath);
            Engine_LevelBakedLighting.TryLoad(
                bakedGdbPath, p => source(p), out bakedLightmaps, out probeField);

            GD.Print(
                $"[baked] {level}: {Engine_LevelBakedLighting.Describe(bakedLightmaps, probeField)}");

            if (bakedLightmaps is not null &&
                terrainNode.MaterialOverride is ShaderMaterial terrainSplat &&
                Engine_LevelBakedLighting.TryPickTerrainLightmap(
                    bakedLightmaps, out ulong terrainKey, out byte[] terrainBlob))
            {
                ImageTexture? lightmapTexture = Engine_LevelBakedLighting.DecodeTexture(terrainBlob);
                if (lightmapTexture is not null)
                {
                    terrainSplat.SetShaderParameter("lightmap", lightmapTexture);
                    GD.Print($"[baked] {level}: terrain lightmap 0x{terrainKey:X16} applied");
                }
            }
        }

        LightingFrame? lightingFrame = null;
        SkyTheme? resolvedSkyTheme = null;
        SkyFrame? resolvedSkyFrame = null;
        if (source is not null)
        {
            List<byte[]> lightingGdbs =
                Engine_SkyEnvironment.BuildGdbChain(level, p => source(p));
            if (lightingGdbs.Count != 0 &&
                Engine_EnvironmentTheme.TryExtractSky(lightingGdbs, out SkyTheme lightingSky))
            {
                float lightingTod =
                    lightingSky.SourceTimeOfDay >= 0f ? lightingSky.SourceTimeOfDay : 0.5f;
                string? forcedLightingTod =
                    System.Environment.GetEnvironmentVariable("REFII_FORCE_TOD");
                if (forcedLightingTod is not null &&
                    float.TryParse(
                        forcedLightingTod, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float forcedLighting))
                {
                    lightingTod = forcedLighting;
                }

                SkyFrame lightingSkyFrame = Engine_SkyFrame.Evaluate(lightingSky, lightingTod);
                resolvedSkyTheme = lightingSky;
                resolvedSkyFrame = lightingSkyFrame;

                if (Engine_LightingTheme.TryExtract(
                        lightingGdbs, lightingTod, out LightingTheme lightingTheme))
                {
                    lightingFrame = Engine_LightingFrame.Evaluate(
                        lightingSky, lightingTheme, lightingSkyFrame);
                }
            }
        }

        if (source is not null)
        {
            Node3D? props = Engine_LevelProps.Build(
                level, p => source(p), probeField, lightingFrame,
                out PropBuildStats propStats);

            if (props is not null)
            {
                root.AddChild(props);
                props.Owner = root;
                foreach (Node child in props.GetChildren())
                {
                    child.Owner = root;
                }
            }

            GD.Print($"[props] {level}: {propStats}");

            Node3D? entities = Refii.Entities.Engine_LevelEntities.Build(
                level, p => source(p), probeField, lightingFrame,
                out Refii.Entities.EntityPlaceStats entityStats);

            if (entities is not null)
            {
                root.AddChild(entities);
                entities.Owner = root;
                foreach (Node child in entities.GetChildren())
                {
                    child.Owner = root;
                }
            }

            GD.Print($"[entities] {level}: {entityStats}");
        }

        if (source is not null)
        {
            WorldEnvironment? sky = Engine_SkyEnvironment.Build(
                level, p => source(p), out _, out WeatherTheme weather);
            if (sky is not null)
            {
                root.AddChild(sky);
                sky.Owner = root;
            }

            float windAzimuth = weather.HasWind
                ? (weather.WindXyRotationMin + weather.WindXyRotationMax) * 0.5f *
                  (MathF.PI / 180f)
                : 0f;
            float windStrength = weather.HasWind
                ? (weather.WindStrengthMin + weather.WindStrengthMax) * 0.5f
                : 0f;

            MeshInstance3D? precipitation =
                Engine_Weather.Build(weather, windAzimuth, windStrength);
            if (precipitation is not null)
            {
                root.AddChild(precipitation);
                precipitation.Owner = root;
            }

            List<byte[]> gdbs = Engine_SkyEnvironment.BuildGdbChain(level, p => source(p));
            if (resolvedSkyTheme is not null && resolvedSkyFrame is not null)
            {
                SkyTheme skyTheme = resolvedSkyTheme;
                SkyFrame frame = resolvedSkyFrame;

                if (lightingFrame is not null)
                {
                    DirectionalLight3D sun = Engine_LevelLighting.BuildSun(lightingFrame);
                    root.AddChild(sun);
                    sun.Owner = root;

                    if (sky is not null)
                    {
                        Engine_LevelLighting.ConfigureEnvironment(sky.Environment, lightingFrame);
                    }

                    if (sky is Engine_SkyEnvironmentNode skyNode)
                    {
                        skyNode.LightingGdbs.AddRange(gdbs);
                        skyNode.Sun = sun;
                    }

                    GD.Print(
                        $"[lighting] {level}: tod={frame.TimeOfDay:0.##} " +
                        $"sun=({lightingFrame.SunColour[0]:0.00}," +
                        $"{lightingFrame.SunColour[1]:0.00}," +
                        $"{lightingFrame.SunColour[2]:0.00})x{lightingFrame.SunEnergy:0.00} " +
                        $"shadow={lightingFrame.SunShadow} " +
                        $"ambient={(lightingFrame.HasAmbient ? lightingFrame.AmbientEnergy.ToString("0.00") : "probes")} " +
                        $"fog={(lightingFrame.HasFog ? $"{lightingFrame.FogBegin:0}->{lightingFrame.FogEnd:0}" : "off")}");
                }

                Node3D? celestial = Engine_CelestialElements.Build(
                    skyTheme, frame, p => source(p));

                if (celestial is not null)
                {
                    root.AddChild(celestial);
                    celestial.Owner = root;
                    foreach (Node child in celestial.GetChildren())
                    {
                        child.Owner = root;
                    }
                }

                if (Engine_EnvironmentTheme.TryExtractClouds(gdbs, out CloudTheme cloudTheme))
                {
                    Node3D? clouds = Engine_Clouds.Build(
                        cloudTheme,
                        gdbs,
                        frame.SunDirection,
                        frame.MainLightColour,
                        p => source(p),
                        findByName is null ? null : n => findByName(n));

                    if (clouds is not null)
                    {
                        root.AddChild(clouds);
                        clouds.Owner = root;
                        foreach (Node child in clouds.GetChildren())
                        {
                            child.Owner = root;
                        }
                    }
                }
            }
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

    private static void AddWater(
        LevelEntry level,
        Func<string, byte[]?>? source,
        Func<string, string?>? findByName,
        Node3D root)
    {
        if (source is null)
        {
            return;
        }

        byte[]? bytes = null;
        foreach (string candidate in WaterCandidates(level))
        {
            bytes = source(candidate);
            if (bytes is not null && bytes.Length > 8)
            {
                break;
            }

            bytes = null;
        }

        if (bytes is null)
        {
            return;
        }

        if (!Engine_WaterFile.TryParse(bytes, out WaterScene scene, out string why))
        {
            GD.Print($"[water] {level}: {why}");
            return;
        }

        var container = new Node3D { Name = WaterNodeName };
        int emitted = 0;

        for (int i = 0; i < scene.Bodies.Count; i++)
        {
            WaterBody body = scene.Bodies[i];
            TerrainMesh? mesh = Engine_WaterMesh.BuildBody(body);
            if (mesh is null)
            {
                continue;
            }

            string name = $"WaterBody{i}";
            var node = new MeshInstance3D
            {
                Name = name,
                Mesh = Engine_LevelMesh.ToArrayMesh(mesh, name),
            };

            ShaderMaterial? material = Engine_WaterMaterial.Build(
                body,
                p => source(p),
                findByName is null ? null : n => findByName(n),
                out string materialError);

            if (material is null)
            {
                GD.Print($"[water] {level}: {materialError}");
            }
            else
            {
                node.MaterialOverride = material;
            }

            container.AddChild(node);
            node.Owner = root;
            emitted++;
        }

        if (emitted == 0)
        {
            container.QueueFree();
            return;
        }

        root.AddChild(container);
        container.Owner = root;

        foreach (Node child in container.GetChildren())
        {
            child.Owner = root;
        }

        GD.Print(
            $"[water] {level}: {emitted} body(s), {scene.TileCount} tiles");
    }

    private static IEnumerable<string> WaterCandidates(LevelEntry level)
    {
        if (level.MainEhfPath.Length != 0)
        {
            yield return ReplaceExtension(level.MainEhfPath, ".water");
        }

        if (level.HeightfieldPath.Length != 0)
        {
            yield return ReplaceExtension(level.HeightfieldPath, ".water");
        }
    }

    private static string ReplaceExtension(string path, string extension)
    {
        int dot = path.LastIndexOf('.');
        return dot < 0 ? path + extension : path[..dot] + extension;
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

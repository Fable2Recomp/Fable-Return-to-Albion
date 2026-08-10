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

    /// <summary>
    /// Builds a level and SAVES it as a scene — the editor's path.
    ///
    /// Saving is an editor capability, not a game one: <c>res://</c> is read-only inside an
    /// exported pack, so this only works while running from the project. The GAME calls
    /// <see cref="Build"/> and mounts the node directly, which is the same construction and one
    /// less round trip through the disk.
    /// </summary>
    public static bool TryBuild(
        LevelEntry level,
        Func<string, byte[]?>? source,
        Func<string, string?>? findByName,
        out string scenePath,
        out string error)
    {
        scenePath = level.ScenePath;

        if (!EnsureDirectory(level, out error))
        {
            return false;
        }

        if (Build(level, source, findByName, out error) is not { } root)
        {
            return false;
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

    /// <summary>
    /// Builds a level and hands back the live node. THE CALLER OWNS IT.
    ///
    /// This is the whole construction — terrain, water, props, entities, lighting, sky, weather
    /// — and it is the one implementation the editor and the game share. The editor packs and
    /// saves what comes back; the game adds it to the tree.
    ///
    /// THE GAME USED TO GO THROUGH THE DISK: build, `ResourceSaver.Save` to `res://levels/...`,
    /// then `ResourceLoader.Load` and instantiate. That only ever worked because we run from
    /// the editor — `res://` is inside a read-only pack in an exported build, so the save fails
    /// and the game gets no world. Nothing about streaming a region needs a `.tscn` to exist;
    /// the banks are read in place and the props page in over frames either way.
    ///
    /// Children still get their <c>Owner</c> set, because that is what makes them survive the
    /// editor's pack and it costs the game nothing.
    /// </summary>
    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?>? source,
        Func<string, string?>? findByName,
        out string error)
    {
        error = string.Empty;

        if (level.HeightfieldPath.Length == 0)
        {
            error = "level has no heightfield";
            return null;
        }

        string meshPath = Engine_BnkTerrainLoader.PathPrefix + level.HeightfieldPath;
        var terrainMesh = ResourceLoader.Load<ArrayMesh>(meshPath, "ArrayMesh");
        if (terrainMesh is null)
        {
            error = $"terrain mesh did not load from {meshPath}";
            return null;
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

        List<(string Path, TerrainMesh Mesh)> vistas = BuildAdjacent(level, source);
        var vistaNodes = new List<(string Path, MeshInstance3D Node)>();
        for (int i = 0; i < vistas.Count; i++)
        {
            (string vistaPath, TerrainMesh vistaMesh) = vistas[i];
            string name = vistas.Count == 1 ? AdjacentNodeName : $"{AdjacentNodeName}{i}";

            var vistaNode = new MeshInstance3D
            {
                Name = name,
                Mesh = Engine_LevelMesh.ToArrayMesh(vistaMesh, name),
                MaterialOverride = LoadTerrainMaterial(vistaPath),
            };

            root.AddChild(vistaNode);
            vistaNode.Owner = root;
            vistaNodes.Add((vistaPath, vistaNode));

            GD.Print(
                $"[vista] {level}: {name} from {vistaPath} " +
                $"({vistaMesh.TriangleCount} tris, " +
                (vistaNode.MaterialOverride is null ? "NO splat material" : "splat material") + ")");
        }

        AddWater(level, source, findByName, root);

        LightmapContainer? bakedLightmaps = null;
        LightProbeField? probeField = null;
        if (source is not null)
        {
            Engine_LevelGdb.TryRead(level, p => source(p), out string bakedGdbPath);
            GD.Print($"[level] {level}: scenario='{level.Scenario}' gdb={bakedGdbPath}");
            Engine_LevelBakedLighting.TryLoad(
                bakedGdbPath, p => source(p), out bakedLightmaps, out probeField);

            GD.Print(
                $"[baked] {level}: {Engine_LevelBakedLighting.Describe(bakedLightmaps, probeField)}");

            // The ground's bake, by the id the level itself states for this heightfield.
            //
            // Each heightfield names its own lightmap in a type-4 record of the .engine_level,
            // and the guest does one 64-bit map lookup with it — no search, no sizing rule.
            // Taking whichever blob is biggest lands on the right one only because a region
            // ships so few: chapter2slums has two, a 576x576 for the landscape and a 32x64 for
            // the sea vista.
            if (bakedLightmaps is not null && source is not null)
            {
                var surfaces = new List<(string Path, MeshInstance3D Node)>
                {
                    (level.MainEhfPath, terrainNode),
                };
                surfaces.AddRange(vistaNodes);

                BindTerrainLightmaps(level, surfaces, bakedLightmaps, bakedGdbPath, source);
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

                if (Engine_ThemeTimeline.TryExtractSkyBlended(
                        lightingGdbs, lightingTod, out SkyTheme blendedSky))
                {
                    lightingSky = blendedSky;
                }

                SkyFrame lightingSkyFrame = Engine_SkyFrame.Evaluate(lightingSky, lightingTod);
                resolvedSkyTheme = lightingSky;
                resolvedSkyFrame = lightingSkyFrame;

                if (Engine_ThemeTimeline.TryExtractLightingBlended(
                        lightingGdbs, lightingTod, out LightingTheme lightingTheme))
                {
                    lightingFrame = Engine_LightingFrame.Evaluate(
                        lightingSky, lightingTheme, lightingSkyFrame);
                }
            }
        }

        // The ground takes the theme's ambient and lightmap tint through the same call every
        // prop does. Its bake comes from the .lmp texture rather than the probe field, so it
        // stores no probe sample — the terrain shader gates on its own bound lightmap.
        //
        // Unlike a prop the terrain HAS its material here, so this survives the pack and the
        // saved scene is already correct before anything runs.
        if (lightingFrame is not null)
        {
            Engine_LevelBakedLighting.ApplyInstanceLighting(terrainNode, lightingFrame);

            // The vistas too. They are landscapes on the same theme, and leaving them on the
            // shader's defaults is how a distant hillside ends up lit differently from the
            // ground it continues.
            foreach ((string _, MeshInstance3D vistaNode) in vistaNodes)
            {
                Engine_LevelBakedLighting.ApplyInstanceLighting(vistaNode, lightingFrame);
            }
        }

        Refii.Entities.EntityInspectData? inspect = null;
        if (source is not null)
        {
            inspect = Refii.Entities.Engine_EntityInspect.Build(level, p => source(p));
        }

        if (source is not null)
        {
            Node3D? props = Engine_LevelProps.Build(
                level, p => source(p), probeField, lightingFrame, bakedLightmaps, inspect,
                out PropBuildStats propStats);

            if (props is not null)
            {
                root.AddChild(props);
                SetOwnerRecursive(props, root);
            }

            GD.Print($"[props] {level}: {propStats}");

            Node3D? entities = Refii.Entities.Engine_LevelEntities.Build(
                level, p => source(p), probeField, lightingFrame, inspect,
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

            if (inspect is not null)
            {
                Node3D? markers = Refii.Entities.Engine_EntityInspect.BuildMarkers(inspect);
                if (markers is not null)
                {
                    root.AddChild(markers);
                    markers.Owner = root;
                    foreach (Node child in markers.GetChildren())
                    {
                        child.Owner = root;
                    }

                    GD.Print(
                        $"[inspect] {level}: {markers.GetChildCount()} " +
                        "container marker(s) without a placed prop");
                }
            }
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
                        $"fog={(lightingFrame.HasFog ? $"{lightingFrame.FogBegin:0}->{lightingFrame.FogEnd:0}" : "off")}" +
                        $"@{lightingFrame.FogNearDensity:0.00}->{lightingFrame.FogDensity:0.00} " +
                        $"fogrgb=({lightingFrame.FogColour[0]:0.000}," +
                        $"{lightingFrame.FogColour[1]:0.000},{lightingFrame.FogColour[2]:0.000})");
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

                if (Engine_ThemeTimeline.TryExtractCloudsBlended(
                        gdbs, frame.TimeOfDay, out CloudTheme cloudTheme))
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

        return root;
    }

    /// <summary>
    /// Gives the terrain the bake its own heightfield names, and says so for any it does not.
    /// </summary>
    /// <remarks>
    /// The heightfields are matched by PATH because that is what the type-4 record carries — a
    /// backslashed asset path and the eight bytes of its lightmap id. A level that ships bakes
    /// for heightfields we do not draw is a normal state, not an error: the sea vista is merged
    /// into one distant mesh with no splat material of its own, so its bake has nowhere to go
    /// yet. That is reported rather than dropped in silence, which is how the wrong bake went
    /// unnoticed on the ground in the first place.
    /// </remarks>
    private static void BindTerrainLightmaps(
        LevelEntry level,
        List<(string Path, MeshInstance3D Node)> surfaces,
        LightmapContainer lightmaps,
        string gdbPath,
        Func<string, byte[]?> source)
    {
        string enginePath = Engine_LevelGdb.SiblingWithExtension(gdbPath, ".engine_level");
        byte[]? bytes = source(enginePath) ?? source("data/" + enginePath);
        if (bytes is null)
        {
            GD.Print($"[baked] {level}: no .engine_level at {enginePath}, terrain unlit");
            return;
        }

        EngineLevelFile engineLevel = Engine_EngineLevelFile.Parse(bytes);
        var bound = new HashSet<ulong>();

        foreach ((string surfacePath, MeshInstance3D node) in surfaces)
        {
            if (node.MaterialOverride is not ShaderMaterial splat)
            {
                continue;
            }

            foreach (EngineLevelHeightfield heightfield in engineLevel.Heightfields)
            {
                if (!SameAsset(heightfield.Path, surfacePath))
                {
                    continue;
                }

                if (!Engine_LevelBakedLighting.TryGetTerrainLightmap(
                        lightmaps, heightfield.LightmapKey, out TerrainLightmap bake))
                {
                    GD.Print(
                        $"[baked] {level}: heightfield states lightmap " +
                        $"0x{heightfield.LightmapKey:X16} but the .lmp has no such entry");
                    break;
                }

                ImageTexture? texture = Engine_LevelBakedLighting.DecodeTexture(bake.Blob);
                if (texture is null)
                {
                    GD.Print($"[baked] {level}: lightmap 0x{heightfield.LightmapKey:X16} did not decode");
                    break;
                }

                splat.SetShaderParameter("lightmap", texture);
                splat.SetShaderParameter("has_lightmap", true);
                bound.Add(heightfield.LightmapKey);

                // One texel per CELL is what makes the plain normalised heightfield coordinate
                // the right UV, so say it out loud when it is not — a bake built for a
                // different lattice is a real failure mode and it looks like bad lighting
                // rather than like a mismatch.
                bool cellAligned =
                    texture.GetWidth() == bake.SamplesX - 1 &&
                    texture.GetHeight() == bake.SamplesY - 1;

                GD.Print(
                    $"[baked] {level}: terrain lightmap 0x{heightfield.LightmapKey:X16} " +
                    $"({texture.GetWidth()}x{texture.GetHeight()} over " +
                    $"{bake.SamplesX}x{bake.SamplesY} samples" +
                    (cellAligned ? "" : ", NOT CELL-ALIGNED") +
                    $") applied from {heightfield.Path}");
                break;
            }
        }

        foreach (EngineLevelHeightfield heightfield in engineLevel.Heightfields)
        {
            if (!bound.Contains(heightfield.LightmapKey) &&
                lightmaps.TerrainLightmapBlobs.ContainsKey(heightfield.LightmapKey))
            {
                GD.Print(
                    $"[baked] {level}: lightmap 0x{heightfield.LightmapKey:X16} for " +
                    $"{heightfield.Path} is shipped but nothing draws that heightfield");
            }
        }
    }

    /// <summary>
    /// Whether a type-4 record's asset path and one of ours name the same file.
    /// </summary>
    /// <remarks>
    /// The .engine_level writes Windows paths — <c>worlds\albion\...\x.ehf</c> — while ours are
    /// the VFS's forward-slashed ones, and either may carry a directory prefix the other does
    /// not. Comparing the file name alone is enough to be unambiguous inside one level and is
    /// the only part both spellings agree on.
    /// </remarks>
    private static bool SameAsset(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        return string.Equals(LeafName(a), LeafName(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string LeafName(string path)
    {
        int cut = path.LastIndexOfAny(new[] { '/', '\\' });
        return cut < 0 ? path : path[(cut + 1)..];
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

    private static void SetOwnerRecursive(Node node, Node root)
    {
        node.Owner = root;
        foreach (Node child in node.GetChildren())
        {
            SetOwnerRecursive(child, root);
        }
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

    /// <summary>
    /// The level's adjacent heightfields, ONE MESH PER FILE rather than all of them fused.
    /// </summary>
    /// <remarks>
    /// Concatenating them into a single anonymous mesh costs the vista both halves of its
    /// lighting: a landscape's splat material is built for ITS heightfield's world extents, and
    /// a landscape's bake is keyed by ITS own type-4 record. Fused there is no file to ask about
    /// either, so the vista falls back to Godot's default material with no bake while the ground
    /// beside it takes the region's full lighting model.
    ///
    /// Keeping them separate costs one draw call each — a region ships one or two — and lets the
    /// vista go through exactly the same material and bake path as the terrain it continues.
    /// </remarks>
    private static List<(string Path, TerrainMesh Mesh)> BuildAdjacent(
        LevelEntry level, Func<string, byte[]?>? source)
    {
        var built = new List<(string, TerrainMesh)>();
        if (source is null || level.AdjacentEhfPaths.Length == 0)
        {
            return built;
        }

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

            var meshes = new List<TerrainMesh>();
            foreach (EhfVistaPatch patch in patches)
            {
                if (patch.Mesh.TriangleCount != 0)
                {
                    meshes.Add(patch.Mesh);
                }
            }

            // The PATCHES of one heightfield do fuse — they share its material and its bake.
            if (meshes.Count != 0)
            {
                built.Add((path, Concatenate(meshes)));
            }
        }

        return built;
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

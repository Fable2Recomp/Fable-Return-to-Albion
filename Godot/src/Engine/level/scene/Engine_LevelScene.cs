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

        // THE TANGENT FRAME, WITHOUT WHICH THE NORMAL MAPS DO NOTHING. The splat shader writes
        // NORMAL_MAP, and Godot applies that through TANGENT and BINORMAL — with no tangent
        // array the frame degenerates and the perturbed normal collapses back to the geometric
        // one, so every layer normal map was decoded, blended and then thrown away before
        // light() ran. The frame is analytic because the projection is: u runs along local +X
        // and v along local +Z (Engine_GhfTerrain.BuildMesh), so the tangent is +X everywhere,
        // and w = -1 makes Godot's `cross(NORMAL, TANGENT) * w` come out +Z to match v.
        //
        // THE HANDEDNESS IS PROVEN, NOT GUESSED — against the guest's own landscape vertex
        // shader (blob 0173): it builds T = Gram-Schmidt(+X), B = T x n = guest -Y, and its
        // ground V is ALSO negated (v = -worldY), so the invariant is "+sample.y tilts toward
        // +v". Our v runs along +Z with B along +Z — the same invariant, mirrored consistently
        // with the co-sampled diffuse. The broken combination is v and B disagreeing.
        var tangents = new float[count * 4];

        for (int v = 0; v < count; v++)
        {
            vertices[v] = new Vector3(
                terrain.Positions[(v * 3) + 0],
                terrain.Positions[(v * 3) + 1],
                terrain.Positions[(v * 3) + 2]);
            var normal = new Vector3(
                terrain.Normals[(v * 3) + 0],
                terrain.Normals[(v * 3) + 1],
                terrain.Normals[(v * 3) + 2]);
            normals[v] = normal;
            uvs[v] = new Vector2(terrain.Uvs[(v * 2) + 0], terrain.Uvs[(v * 2) + 1]);

            // GRAM-SCHMIDT PER VERTEX, exactly the guest's own construction (blob 0173 builds
            // T = Gram-Schmidt(+X) against the vertex normal). A constant (1, 0, 0) is NOT
            // orthogonal to a sloped normal, and Godot's compressed vertex format encodes the
            // normal+tangent PAIR on the assumption that it is — a non-orthogonal tangent
            // corrupts the shared encoding, which surfaced as garbage view-space normals on
            // every mirrored terrain instance (the level root's Scale(-1,1,1)) and killed the
            // ground's NdotL for every light in the game.
            Vector3 tangent = new Vector3(1f, 0f, 0f) - (normal * normal.X);
            tangent = tangent.LengthSquared() > 1e-8f ? tangent.Normalized() : new Vector3(1f, 0f, 0f);
            tangents[(v * 4) + 0] = tangent.X;
            tangents[(v * 4) + 1] = tangent.Y;
            tangents[(v * 4) + 2] = tangent.Z;
            tangents[(v * 4) + 3] = -1f;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Tangent] = tangents;
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

        var clock = System.Diagnostics.Stopwatch.StartNew();

        var packed = new PackedScene();
        Error packedResult = packed.Pack(root);
        if (packedResult != Error.Ok)
        {
            root.QueueFree();
            error = $"could not pack scene: {packedResult}";
            return false;
        }

        long packMs = clock.ElapsedMilliseconds;
        clock.Restart();

        Error saved = ResourceSaver.Save(packed, level.ScenePath);
        root.QueueFree();

        if (saved != Error.Ok)
        {
            error = $"could not save scene to {level.ScenePath}: {saved}";
            return false;
        }

        long size = 0;
        string globalized = ProjectSettings.GlobalizePath(level.ScenePath);
        if (System.IO.File.Exists(globalized))
        {
            size = new System.IO.FileInfo(globalized).Length;
        }

        GD.Print(
            $"[level-save] {level}: pack {packMs} ms, save {clock.ElapsedMilliseconds} ms, " +
            $"{size / (1024.0 * 1024.0):0.0} MB at {level.ScenePath}");

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
    /// <summary>
    /// The level's painted day-set grid, or null when it paints nothing. The grid is the
    /// heightfield's sibling with the .genv extension (the market: bwsmainheightfield_id_….genv
    /// beside ….ghf), spans the heightfield from its origin, and the guest loads every .genv the
    /// manifest lists — a region has one per heightfield that carries paint.
    /// </summary>
    private static Refii.Lighting.Engine_PaintedDaySets? LoadPaintedDaySets(
        LevelEntry level, Func<string, byte[]?> source, IReadOnlyList<byte[]> gdbs)
    {
        if (level.HeightfieldPath.Length == 0)
        {
            return null;
        }

        string genvPath = Engine_LevelGdb.SiblingWithExtension(level.HeightfieldPath, ".genv");
        byte[]? bytes = source(genvPath);
        if (bytes is null)
        {
            return null;
        }

        if (source(level.HeightfieldPath) is not { } ghfBytes ||
            !Engine_GhfTerrain.TryDecode(ghfBytes, out GhfHeights heights, out _))
        {
            GD.Print($"[painted] {level.Name}: {genvPath} present but the heightfield did not decode; no origin");
            return null;
        }

        if (!Refii.Lighting.Engine_PaintedDaySets.TryParse(
                bytes, heights.OriginX, heights.OriginZ, out Refii.Lighting.Engine_PaintedDaySets.Grid grid, out string error))
        {
            GD.Print($"[painted] {level.Name}: {genvPath}: {error}");
            return null;
        }

        var painted = new Refii.Lighting.Engine_PaintedDaySets();
        painted.Add(grid);
        if (painted.PaintedCells == 0)
        {
            GD.Print($"[painted] {level.Name}: {grid.Width}x{grid.Height} cells, none painted");
            return null;
        }

        Engine_LightingTheme.ReadPaintedBlendTimes(gdbs, out painted.BlendInSeconds, out painted.BlendOutSeconds);
        var sets = new Dictionary<uint, int>();
        foreach (uint cell in grid.Cells)
        {
            if (cell != Refii.Lighting.Engine_PaintedDaySets.None)
            {
                sets[cell] = sets.TryGetValue(cell, out int n) ? n + 1 : 1;
            }
        }

        var parts = new List<string>();
        foreach ((uint hash, int count) in sets)
        {
            parts.Add($"0x{hash:X8}x{count}");
        }

        GD.Print($"[painted] {level.Name}: {grid.Width}x{grid.Height} cells at {grid.Cell} m from " +
            $"({grid.OriginX:0.#}, {grid.OriginZ:0.#}), {painted.PaintedCells} painted: {string.Join(" ", parts)}; " +
            $"blend in/out {painted.BlendInSeconds:0.#}/{painted.BlendOutSeconds:0.#} s");
        return painted;
    }

    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?>? source,
        Func<string, string?>? findByName,
        out string error)
    {
        error = string.Empty;

        // PER-PHASE TIMING. It is what settled where the cost actually was: 14312 ms total,
        // of which terrain 9357 and props 3634 — so the answer was never "split the small
        // phases across frames", it was to get the whole thing off the main thread.
        //
        // THE GAME NOW CALLS THIS FROM A WORKER (see `GameWorld.StartBuild`); the editor still
        // calls it inline. Nothing here may touch Godot's scene tree: every node this builds is
        // detached until the caller mounts it, and `AddChild`/`Owner` on a detached subtree are
        // plain object writes rather than tree operations. Keep it that way — a `GetTree()`,
        // an autoload lookup or a `MakeCurrent` added here would fault only in the game, only
        // sometimes, and would look like a level-data bug.
        //
        // The phase numbers are still worth keeping. They are now CPU time on the worker rather
        // than stall time on the main loop, which is a different quantity with the same units.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var phases = new List<(string Name, long Ms)>();
        long phaseStart = 0;
        void Mark(string name)
        {
            long now = clock.ElapsedMilliseconds;
            phases.Add((name, now - phaseStart));
            phaseStart = now;
        }

        LevelReads? reads = null;
        if (source is not null)
        {
            reads = new LevelReads(source);
            source = reads.Read;
            Refii.Animations.Engine_AnimCatalog.Prewarm();
        }

        string heightfieldPath = level.HeightfieldPath;
        ScenarioManifest? gameManifest = null;
        if (source is not null)
        {
            Engine_LevelVfsConfig.Apply(level, source);

            if (Engine_LevelManifest.TryResolve(
                    level, source, out ScenarioManifest manifest, out string manifestPath,
                    out string scenarioUsed))
            {
                gameManifest = manifest;
                if (level.Scenario.Length == 0)
                {
                    level.Scenario = scenarioUsed;
                }

                GD.Print(
                    $"[level] LoadLevel world={level.World} level={level.Name} " +
                    $"scenario={scenarioUsed} " +
                    $"id=0x{Engine_LevelManifest.LevelId(level.Name):X8} — " +
                    $"{manifestPath}: {manifest.Describe()}");
            }
            else
            {
                GD.Print($"[level] {level}: no scenario .list, using discovered heightfield");
            }
        }

        Mark("manifest");

        if (gameManifest is not null && reads is not null)
        {
            Preload(level, gameManifest, reads);
            Mark("preload");

            if (gameManifest.Ghf.Count != 0)
            {
                bool discoveredIsStated = gameManifest.Ghf.Exists(line =>
                    Engine_LevelManifest.VirtualPath(line).Equals(
                        heightfieldPath, StringComparison.OrdinalIgnoreCase));

                if (!discoveredIsStated &&
                    Engine_LevelManifest.TryResolvePath(
                        gameManifest.Ghf[0], reads.Read, out string statedHeightfield))
                {
                    GD.Print(
                        $"[level] {level}: manifest states heightfield " +
                        $"{statedHeightfield}" +
                        (heightfieldPath.Length != 0
                            ? $" (discovery had {heightfieldPath})"
                            : string.Empty));
                    heightfieldPath = statedHeightfield;
                }
            }

            ReadGameOrderResources(level, gameManifest, reads.Read);
            Mark("game-order");
        }

        if (heightfieldPath.Length == 0)
        {
            error = "level has no heightfield";
            return null;
        }

        string meshPath = Engine_BnkTerrainLoader.PathPrefix + heightfieldPath;
        Func<string, byte[]?>? terrainRead = source;
        (ArrayMesh? Mesh, Material? Material,
            List<(string Path, string Name, ArrayMesh Mesh, Material? Material)> VistaBuilt)
            TerrainWork()
        {
            var mesh = ResourceLoader.Load<ArrayMesh>(meshPath, "ArrayMesh");

            Material? material = level.MainEhfPath.Length != 0
                ? LoadTerrainMaterial(level.MainEhfPath)
                : null;

            var vistaBuilt = new List<(string Path, string Name, ArrayMesh Mesh, Material? Material)>();
            List<(string Path, TerrainMesh Mesh)> vistas = BuildAdjacent(level, terrainRead);
            for (int i = 0; i < vistas.Count; i++)
            {
                (string vistaPath, TerrainMesh vistaMesh) = vistas[i];
                string name = vistas.Count == 1 ? AdjacentNodeName : $"{AdjacentNodeName}{i}";
                Material? vistaMaterial = LoadTerrainMaterial(vistaPath);

                GD.Print(
                    $"[vista] {level}: {name} from {vistaPath} " +
                    $"({vistaMesh.TriangleCount} tris, " +
                    (vistaMaterial is null ? "NO splat material" : "splat material") + ")");

                vistaBuilt.Add(
                    (vistaPath, name, Engine_LevelMesh.ToArrayMesh(vistaMesh, name), vistaMaterial));
            }

            return (mesh, material, vistaBuilt);
        }

        var terrainTask =
            OS.GetThreadCallerId() == OS.GetMainThreadId()
                ? null
                : System.Threading.Tasks.Task.Run(TerrainWork);

        var root = new Node3D
        {
            Name = level.SceneNodeName,
            Scale = new Vector3(-1f, 1f, 1f),
        };

        AddWater(level, source, findByName, root);
        Mark("water");

        bool hasMist = AddMist(level, source, root);
        Mark("mist");

        LightmapContainer? bakedLightmaps = null;
        LightProbeField? probeField = null;
        string bakedGdbPath = string.Empty;
        if (source is not null)
        {
            Engine_LevelGdb.TryRead(level, p => source(p), out bakedGdbPath);
            GD.Print($"[level] {level}: scenario='{level.Scenario}' gdb={bakedGdbPath}");
            Engine_LevelBakedLighting.TryLoad(
                bakedGdbPath, p => source(p), out bakedLightmaps, out probeField);
            Engine_LevelBakedLighting.CurrentProbes = probeField;

            GD.Print(
                $"[baked] {level}: {Engine_LevelBakedLighting.Describe(bakedLightmaps, probeField)}");
        }

        Mark("baked-lighting");

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

                if (Engine_ThemeTimeline.TryExtractWaterBlended(
                        lightingGdbs, lightingTod, out WaterTheme waterTheme))
                {
                    ApplyWaterTheme(root, waterTheme);
                }
            }
        }

        Mark("sky+lighting");

        Refii.Entities.EntityInspectData? inspect = null;
        if (source is not null)
        {
            inspect = Refii.Entities.Engine_EntityInspect.Build(level, p => source(p));
        }

        Mark("inspect");

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
            Mark("props");

            Node3D? entities = Refii.Entities.Engine_LevelEntities.Build(
                level, p => source(p), probeField, lightingFrame, inspect,
                out Refii.Entities.EntityPlaceStats entityStats);

            if (entities is not null)
            {
                root.AddChild(entities);
                SetOwnerRecursive(entities, root);
            }

            GD.Print($"[entities] {level}: {entityStats}");
            Mark("entities");

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

            // THE SAME DIRECTION THE RAIN USES, so vegetation and weather cannot disagree about
            // which way it blows — but the DIRECTION ONLY, as a unit vector. windStrength is not
            // applied: its units were never established, and scaling by it bent every tree flat.
            //
            // Set BEFORE the props are built, because materials take it at construction — pushing
            // it afterwards would leave this level's plants on the previous level's wind.
            Refii.Models.Engine_MdlMaterial.WindDirection = weather.HasWind
                ? new Vector2(MathF.Cos(windAzimuth), MathF.Sin(windAzimuth))
                : Vector2.Zero;

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

                        if (hasMist)
                        {
                            // THE MIST VOLUMES ARE THE ONLY FOG. Godot's volumetric fog has a
                            // level-wide density of its own (default 0.05) that fills every cubic
                            // metre once the effect is enabled, lit by every torch — which is
                            // what turned the guild hall into an orange soup. The authored mist
                            // is a set of placed FogVolumes; the global density is not authored
                            // and goes to zero.
                            sky.Environment.VolumetricFogEnabled = true;
                            sky.Environment.VolumetricFogDensity = 0f;
                        }
                    }

                    if (sky is Engine_SkyEnvironmentNode skyNode)
                    {
                        skyNode.LightingGdbs.AddRange(gdbs);
                        skyNode.Sun = sun;
                        skyNode.Painted = LoadPaintedDaySets(level, source, gdbs);
                    }

                    // BOTH THE STATED FIELDS AND THE SOLVED CURVE. The two densities are control
                    // points rather than amounts, so the stated numbers on their own say nothing
                    // about how thick the fog will look — the exponent is what decides that, and
                    // it is the value to check first when a region reads as too clear or as
                    // pea soup.
                    AtmosphereInputs fogInputs =
                        Engine_SkyAtmosphere.FromFrame(frame, skyTheme);
                    var fogState = new AtmosphereState();
                    Engine_SkyAtmosphere.SolveFogCurve(fogInputs, fogState);

                    GD.Print(
                        $"[lighting] {level}: tod={frame.TimeOfDay:0.##} " +
                        $"sun=({lightingFrame.SunColour[0]:0.00}," +
                        $"{lightingFrame.SunColour[1]:0.00}," +
                        $"{lightingFrame.SunColour[2]:0.00})x{lightingFrame.SunEnergy:0.00} " +
                        $"shadow={lightingFrame.SunShadow} " +
                        $"ambient={(lightingFrame.HasAmbient ? lightingFrame.AmbientEnergy.ToString("0.00") : "probes")}");

                    GD.Print(
                        $"[fog] {level}: " +
                        (fogState.HasFog
                            ? $"start={fogInputs.FoggingStart:0.#} " +
                              $"near={fogInputs.NearDistance:0.#}@{fogInputs.NearDensity:0.###} " +
                              $"far={fogInputs.FarDistance:0.#}@{fogInputs.FarDensity:0.###} " +
                              $"-> depth={fogState.FogOpticalDepthAtFar:0.###} " +
                              $"range={1f / fogState.FogRecipFarRange:0.#} " +
                              $"k={fogState.FogExponent:0.###} "
                            : "off ") +
                        $"closergb=({skyTheme.FogColour[0]:0.000}," +
                        $"{skyTheme.FogColour[1]:0.000},{skyTheme.FogColour[2]:0.000}) " +
                        $"closemax={skyTheme.CloseFogMaxDistance:0.#}");
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

        Mark("rest");

        (ArrayMesh? terrainMesh, Material? terrainMaterial,
            List<(string Path, string Name, ArrayMesh Mesh, Material? Material)> vistaBuilt) =
            terrainTask is not null ? terrainTask.GetAwaiter().GetResult() : TerrainWork();

        if (terrainMesh is null)
        {
            root.QueueFree();
            error = $"terrain mesh did not load from {meshPath}";
            return null;
        }

        var terrainNode = new MeshInstance3D
        {
            Name = TerrainNodeName,
            Mesh = terrainMesh,
        };

        if (terrainMaterial is not null)
        {
            terrainNode.MaterialOverride = terrainMaterial;
        }

        root.AddChild(terrainNode);
        terrainNode.Owner = root;

        var vistaNodes = new List<(string Path, MeshInstance3D Node)>();
        foreach ((string vistaPath, string vistaName, ArrayMesh vistaMesh, Material? vistaMaterial)
                 in vistaBuilt)
        {
            var vistaNode = new MeshInstance3D
            {
                Name = vistaName,
                Mesh = vistaMesh,
            };

            if (vistaMaterial is not null)
            {
                vistaNode.MaterialOverride = vistaMaterial;
            }

            root.AddChild(vistaNode);
            vistaNode.Owner = root;
            vistaNodes.Add((vistaPath, vistaNode));
        }

        if (bakedLightmaps is not null && source is not null)
        {
            var surfaces = new List<(string Path, MeshInstance3D Node)>
            {
                (level.MainEhfPath, terrainNode),
            };
            surfaces.AddRange(vistaNodes);

            BindTerrainLightmaps(level, surfaces, bakedLightmaps, bakedGdbPath, source);
        }

        if (lightingFrame is not null)
        {
            Engine_LevelBakedLighting.ApplyInstanceLighting(terrainNode, lightingFrame);
            foreach ((string _, MeshInstance3D vistaNode) in vistaNodes)
            {
                Engine_LevelBakedLighting.ApplyInstanceLighting(vistaNode, lightingFrame);
            }
        }

        Mark("terrain-join");

        GD.Print($"[level-build] {level}: {clock.ElapsedMilliseconds} ms of construction — " +
            string.Join("  ", phases.ConvertAll(p => $"{p.Name} {p.Ms}ms")));

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
        string enginePath = Engine_LevelGdb.EngineLevelPath(level, source, gdbPath);
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

    private static void Preload(LevelEntry level, ScenarioManifest manifest, LevelReads reads)
    {
        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string path)
        {
            if (path.Length != 0 && seen.Add(path))
            {
                wanted.Add(path);
            }
        }

        void AddBucket(List<string> bucket)
        {
            foreach (string line in bucket)
            {
                Add(Engine_LevelManifest.VirtualPath(line));
            }
        }

        AddBucket(manifest.Gdb);
        AddBucket(manifest.EngineTheme);
        AddBucket(manifest.Ghf);
        AddBucket(manifest.AudioMaps);
        AddBucket(manifest.Hdb);
        AddBucket(manifest.Genv);
        AddBucket(manifest.EngineLevel);
        AddBucket(manifest.AiConfig);
        AddBucket(manifest.Save);
        AddBucket(manifest.HavokScenario);
        AddBucket(manifest.Unrecognized);

        if (manifest.Gdb.Count != 0)
        {
            string stem = Engine_LevelManifest.VirtualPath(manifest.Gdb[0]);
            foreach (string extension in new[] { ".lmp", ".gdb", ".save", ".engine_level" })
            {
                Add(Engine_LevelGdb.SiblingWithExtension(stem, extension));
            }
        }

        foreach (string candidate in Engine_LevelVfsConfig.CandidatePaths(level))
        {
            Add(candidate);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        (int fetched, List<string> missing) = reads.Prefetch(wanted);

        string summary =
            $"[level-preload] {level}: {fetched}/{wanted.Count} manifest file(s) in " +
            $"{clock.ElapsedMilliseconds} ms";

        if (missing.Count != 0)
        {
            missing.Sort(StringComparer.Ordinal);
            var shown = new List<string>();
            for (int i = 0; i < missing.Count && shown.Count < 8; i++)
            {
                shown.Add(LeafName(missing[i]));
            }

            summary += $" — {missing.Count} unresolved: {string.Join(", ", shown)}" +
                       (missing.Count > shown.Count ? ", ..." : string.Empty);
        }

        GD.Print(summary);
    }

    private static void ReadGameOrderResources(
        LevelEntry level, ScenarioManifest manifest, Func<string, byte[]?> source)
    {
        var order = new (string Label, List<string> Bucket)[]
        {
            (".ghf  (terrain grid)", manifest.Ghf),
            ("audio map layer", manifest.AudioMaps),
            (".hdb  (height database)", manifest.Hdb),
            (".genv (env grid)", manifest.Genv),
        };

        foreach ((string label, List<string> bucket) in order)
        {
            foreach (string line in bucket)
            {
                string path = Engine_LevelManifest.VirtualPath(line);
                byte[]? bytes = source(path);
                GD.Print(bytes is not null
                    ? $"[level] {label} loaded ({bytes.Length} bytes)  {path}"
                    : $"[level] {label} load FAILED: {path}");
            }
        }
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

        ImageTexture? terrainTexture = null;
        var terrainBounds = Vector4.Zero;
        if (!Engine_WaterMaterial.GuestMode &&
            level.HeightfieldPath.Length != 0 &&
            source(level.HeightfieldPath) is { } ghfBytes &&
            Engine_GhfTerrain.TryDecode(ghfBytes, out GhfHeights heights, out _) &&
            heights.Width > 1 && heights.Height > 1)
        {
            var data = new byte[heights.Values.Length * sizeof(float)];
            Buffer.BlockCopy(heights.Values, 0, data, 0, data.Length);
            Image image = Image.CreateFromData(
                heights.Width, heights.Height, false, Image.Format.Rf, data);
            terrainTexture = ImageTexture.CreateFromImage(image);
            terrainBounds = new Vector4(
                heights.OriginX,
                heights.OriginZ,
                1f / ((heights.Width - 1) * Engine_GhfTerrain.CellSize),
                1f / ((heights.Height - 1) * Engine_GhfTerrain.CellSize));
        }

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
                if (terrainTexture is not null)
                {
                    material.SetShaderParameter("terrain_height_tex", terrainTexture);
                    material.SetShaderParameter("terrain_bounds", terrainBounds);
                    material.SetShaderParameter("has_terrain_height", true);
                }

                Aabb bodyAabb = node.Mesh.GetAabb();
                material.SetShaderParameter("w_body_bounds", new Vector4(
                    bodyAabb.Position.X,
                    bodyAabb.Position.Z,
                    bodyAabb.Position.X + bodyAabb.Size.X,
                    bodyAabb.Position.Z + bodyAabb.Size.Z));
                material.SetShaderParameter("has_body_bounds", true);

                node.MaterialOverride = material;
            }

            container.AddChild(node);
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

    private static void ApplyWaterTheme(Node3D root, WaterTheme theme)
    {
        Node3D? water = root.GetNodeOrNull<Node3D>(WaterNodeName);
        if (water is null)
        {
            return;
        }

        foreach (Node child in water.GetChildren())
        {
            if (child is MeshInstance3D mesh &&
                mesh.MaterialOverride is ShaderMaterial material)
            {
                Engine_WaterMaterial.Apply(material, theme);
            }
        }
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

    private static bool AddMist(
        LevelEntry level,
        Func<string, byte[]?>? source,
        Node3D root)
    {
        if (source is null)
        {
            return false;
        }

        byte[]? bytes = null;
        foreach (string candidate in MistCandidates(level))
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
            return false;
        }

        if (!Engine_MistFile.TryParse(bytes, out MistScene scene, out string why))
        {
            GD.Print($"[mist] {level}: {why}");
            return false;
        }

        if (scene.Volumes.Count == 0)
        {
            return false;
        }

        var container = new Node3D { Name = "Mist" };
        int emitted = 0;

        for (int i = 0; i < scene.Volumes.Count; i++)
        {
            MistVolume vol = scene.Volumes[i];

            float sizeX = MathF.Abs(vol.BoxSize[0]);
            float sizeY = MathF.Abs(vol.BoxSize[1]);
            float sizeZ = MathF.Abs(vol.BoxSize[2]);
            if (sizeX < 0.01f && sizeY < 0.01f && sizeZ < 0.01f)
            {
                continue;
            }

            float centreX = vol.BoxMin[0] + vol.BoxSize[0] * 0.5f;
            float centreY = vol.BoxMin[1] + vol.BoxSize[1] * 0.5f;
            float centreZ = vol.BoxMin[2] + vol.BoxSize[2] * 0.5f;

            var material = new FogMaterial();
            material.Density = vol.Density;
            material.Albedo = new Color(vol.Colour[0], vol.Colour[1], vol.Colour[2]);

            var fog = new FogVolume
            {
                Name = $"MistVolume{i}",
                Position = new Vector3(centreX, centreY, centreZ),
                Size = new Vector3(sizeX, sizeY, sizeZ),
                Shape = RenderingServer.FogVolumeShape.Box,
                Material = material,
            };

            container.AddChild(fog);
            emitted++;
            GD.Print($"[mist] volume {i}: min=({vol.BoxMin[0]:0.#},{vol.BoxMin[1]:0.#},{vol.BoxMin[2]:0.#}) " +
                $"size=({vol.BoxSize[0]:0.#},{vol.BoxSize[1]:0.#},{vol.BoxSize[2]:0.#}) density={vol.Density:0.####} " +
                $"colour=({vol.Colour[0]:0.00},{vol.Colour[1]:0.00},{vol.Colour[2]:0.00}) falloff={vol.Falloff:0.###} recip={vol.Reciprocal:0.###}");
        }

        if (emitted == 0)
        {
            container.QueueFree();
            return false;
        }

        root.AddChild(container);
        container.Owner = root;

        foreach (Node child in container.GetChildren())
        {
            child.Owner = root;
        }

        GD.Print(
            $"[mist] {level}: {emitted} volume(s) from {scene.Volumes.Count} record(s)");

        return true;
    }

    private static IEnumerable<string> MistCandidates(LevelEntry level)
    {
        if (level.MainEhfPath.Length != 0)
        {
            yield return ReplaceExtension(level.MainEhfPath, ".mist");
        }

        if (level.HeightfieldPath.Length != 0)
        {
            yield return ReplaceExtension(level.HeightfieldPath, ".mist");
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

using System.Collections.Generic;
using System;
using System.Linq;
using Godot;
using Refii.Level;
using Refii.Textures;

namespace Refii.Lighting;

public static partial class Engine_LevelBakedLighting
{
	public const string LightmapExtension = ".lmp";

	public const string ProbeRelativePath = "LightProbeData/LightProbesData.dat";

	/// <summary>
	/// Fable 3's two files, both the level path with its extension replaced (sub_82CC2250 and
	/// sub_82CC30E0 through sub_82D8A698); the names are the globals set at 0x83204760 and
	/// 0x83204720.
	/// </summary>
	public const string StreamedLightmapExtension = ".cflmp";

	public const string ProbeSetExtension = ".flpb";

	public static void TryLoad(
		string gdbPath,
		Func<string, byte[]?> read,
		out LightmapContainer? lightmaps,
		out LightProbeField? probes) =>
		TryLoad(gdbPath, read, Refii.Core.Engine_Title.Active, out lightmaps, out probes);

	public static void TryLoad(
		string gdbPath,
		Func<string, byte[]?> read,
		Refii.Core.Engine_Title title,
		out LightmapContainer? lightmaps,
		out LightProbeField? probes)
	{
		lightmaps = null;
		probes = null;

		if (gdbPath.Length == 0)
		{
			return;
		}

		if (title == Refii.Core.Engine_Title.Fable3)
		{
			TryLoadFable3(gdbPath, read, out lightmaps, out probes);
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

	private static void TryLoadFable3(
		string gdbPath,
		Func<string, byte[]?> read,
		out LightmapContainer? lightmaps,
		out LightProbeField? probes)
	{
		lightmaps = null;
		probes = null;

		string lightmapPath = Engine_LevelGdb.SiblingWithExtension(gdbPath, StreamedLightmapExtension);
		byte[]? lightmapBytes = MaybeGunzip(read(lightmapPath) ?? read("data/" + lightmapPath));
		if (lightmapBytes is not null)
		{
			if (Engine_LightmapFile.TryParse(lightmapBytes, out LightmapContainer container, out string lmError))
			{
				lightmaps = container;
			}
			else
			{
				GD.Print($"[baked] {lightmapPath}: {lmError}");
			}
		}

		string probePath = Engine_LevelGdb.SiblingWithExtension(gdbPath, ProbeSetExtension);
		byte[]? probeBytes = MaybeGunzip(read(probePath) ?? read("data/" + probePath));
		if (probeBytes is not null)
		{
			if (Engine_LightProbeField.TryParseProbeSet(probeBytes, out LightProbeField field, out string probeError))
			{
				probes = field;
			}
			else
			{
				GD.Print($"[baked] {probePath}: {probeError}");
			}
		}
	}

	/// <summary>Metadata key holding a node's baked probe sample as three <c>Vector4</c>.</summary>
	public const string ProbeMeta = "probe_sh";

	// Lazily cached StringNames so touching Engine_LevelBakedLighting in headless .NET testhost
	// never invokes native Godot StringName constructors before the engine is up.
	private static StringName? _probeMetaName;
	private static StringName? _probeAtMetaName;
	private static StringName? _bakeModeMetaName;
	private static StringName? _bakeKeyMetaName;
	private static StringName? _globalAmbientColour;
	private static StringName? _globalDarkening;
	private static StringName? _globalLightmapTint;
	private static StringName? _globalSunToward;
	private static StringName? _globalSunColour;
	private static StringName? _paramLightmapColour;
	private static StringName? _paramProbeB;
	private static StringName? _paramProbeG;
	private static StringName? _paramProbeR;
	private static StringName? _paramAmbientScale;

	public static StringName ProbeMetaName => _probeMetaName ??= ProbeMeta;
	public static StringName ProbeAtMetaName => _probeAtMetaName ??= "probe_at";
	public static StringName BakeModeMetaName => _bakeModeMetaName ??= "bake_mode";
	public static StringName BakeKeyMetaName => _bakeKeyMetaName ??= "bake_key";
	private static readonly float[] WhiteLightmap = { 1f, 1f, 1f };
	private static StringName Global_ambient_colour => _globalAmbientColour ??= "fable_ambient_colour";
	private static StringName Global_darkening => _globalDarkening ??= "fable_darkening";
	private static StringName Global_lightmap_tint => _globalLightmapTint ??= "fable_lightmap_tint";
	private static StringName Global_sun_toward => _globalSunToward ??= "fable_sun_toward";
	private static StringName Global_sun_colour => _globalSunColour ??= "fable_sun_colour";
	private static StringName Param_lightmap_colour => _paramLightmapColour ??= "lightmap_colour";
	private static StringName Param_probe_b => _paramProbeB ??= "probe_b";
	private static StringName Param_probe_g => _paramProbeG ??= "probe_g";
	private static StringName Param_probe_r => _paramProbeR ??= "probe_r";
	public static StringName Param_ambient_scale => _paramAmbientScale ??= "fable_ambient_scale";

	/// <summary>Where a runtime entity last sampled the probe field (region-local Godot axes).</summary>
	public const string ProbeAtMeta = "probe_at";

	/// <summary>
	/// The loaded level's light-probe field, kept for the entities that arrive after the build.
	/// The guest lights every dynamic object from this field (`g_PRTConstants`, the amb2 vertex
	/// path); a character that took only the theme's flat ambient stood as a black silhouette
	/// in a torch-lit cave whose ambient is 0.048.
	/// </summary>
	public static LightProbeField? CurrentProbes { get; set; }

	/// <summary>
	/// Samples the probe field for a runtime entity at its current place and writes its
	/// lighting. Re-samples once it has moved more than two metres from the last sample, and
	/// always when it has none; the bake does not move, the entity does.
	/// </summary>
	public static void LightEntityAt(GeometryInstance3D node, Vector3 regionLocal, LightingFrame? frame)
	{
		LightProbeField? probes = CurrentProbes;
		if (probes is not null && probes.HasData)
		{
			bool sampled = node.HasMeta(ProbeAtMetaName) &&
				node.GetMeta(ProbeAtMetaName) is { VariantType: Variant.Type.Vector3 } at &&
				at.AsVector3().DistanceSquaredTo(regionLocal) < 4f;
			if (!sampled)
			{
				StoreProbeSample(node, regionLocal, probes);
				node.SetMeta(ProbeAtMetaName, regionLocal);
			}
		}

		ApplyInstanceLighting(node, frame);
	}

	/// <summary>
	/// Metadata key holding WHICH bake this node carries, matching the shader's selector:
	/// 0 none, 1 probe SH, 2 linear COLOR, 3 gamma-encoded COLOR.
	///
	/// Needed because the mode cannot be inferred from what else the node has. A MultiMesh
	/// block carries its bake as Godot per-instance colour and no probe metadata at all, so
	/// "has probe metadata" read it as unlit and the per-instance colours were written, packed
	/// and then never sampled — every piece of foliage in the region silently lost its bake.
	/// </summary>
	public const string BakeModeMeta = "bake_mode";

	/// <summary>
	/// The order-2 SH band-0 constant, <c>1 / (2 * sqrt(PI))</c>, from
	/// <c>D3DXSHEvalDirection</c> at 0x82D13C0C. Anything that flattens a probe sample outside
	/// the shader has to apply it, or the two paths disagree by a factor of three and a half.
	/// </summary>
	public const float ShBand0 = 0.2820948f;

	/// <summary>
	/// Records the probe field's answer for one placement, as NODE METADATA.
	///
	/// It cannot be written as an instance shader parameter here, and that is the whole reason
	/// this function exists. Props and entities are packed into the level scene WITHOUT a mesh
	/// — <see cref="Engine_PropStreamer"/> pages them in later — and Godot only serialises
	/// <c>instance_shader_parameters/*</c> for a node whose material it can already see. So
	/// every value written at build time was dropped by <c>PackedScene.Pack</c>, arrived at
	/// nothing, and left the entire region rendering on the shader's own default ambient. The
	/// baked probe field, decoded and sampled correctly, reached no surface at all.
	///
	/// Metadata survives the pack. <see cref="ApplyInstanceLighting"/> turns it back into
	/// shader state once the mesh is standing.
	/// </summary>
	public static void StoreProbeSample(Node node, Vector3 godotPosition, LightProbeField? probes)
	{
		if (probes is null || !probes.HasData)
		{
			return;
		}

		// The field is indexed in the GUEST's axes; the caller has a Godot position.
		var gamePosition = new Vector3(godotPosition.X, godotPosition.Z, godotPosition.Y);
		ProbeSample sample = probes.Sample(gamePosition);

		if (sample.R.LengthSquared() + sample.G.LengthSquared() +
			sample.B.LengthSquared() <= 0f)
		{
			return;
		}

		var probe = new Godot.Collections.Array<Vector4>
		{
			sample.R, sample.G, sample.B,
		};
		node.SetMeta(ProbeMetaName, probe);
		GC.KeepAlive(probe);
	}

	/// <summary>
	/// Stores an authored per-instance bake (a .lmp u32-array D3DCOLOR) as a flat probe sample.
	///
	/// The L0 term is scaled so the shader's basis multiply (× <see cref="ShBand0"/>) recovers
	/// <c>pow(c, 2.2)</c>, and the pixel side's 1.3 then completes the guest's 2.86 vertex-bake
	/// compose. The directional bands stay zero — the source is one colour, not a field.
	/// </summary>
	public static void StoreFlatBakeColour(Node node, uint d3dColour)
	{
		float r = Mathf.Pow(((d3dColour >> 16) & 0xFF) / 255.0f, 2.2f) / ShBand0;
		float g = Mathf.Pow(((d3dColour >> 8) & 0xFF) / 255.0f, 2.2f) / ShBand0;
		float b = Mathf.Pow((d3dColour & 0xFF) / 255.0f, 2.2f) / ShBand0;

		var probe = new Godot.Collections.Array<Vector4>
		{
			new Vector4(r, 0f, 0f, 0f), new Vector4(g, 0f, 0f, 0f), new Vector4(b, 0f, 0f, 0f),
		};
		node.SetMeta(ProbeMetaName, probe);
		GC.KeepAlive(probe);
	}

	private static float? _ambientScale;

	private static float? _buildingInteriorAmbientScale;

	private static int _checkedGen = -1;

	private static void CheckGeneration()
	{
		Refii.Core.Engine_RefiiConfig.PollReload();
		int gen = Refii.Core.Engine_Switches.Generation;
		if (gen != _checkedGen)
		{
			_checkedGen = gen;
			_ambientScale = null;
			_buildingInteriorAmbientScale = null;
			if (!Refii.Core.Engine_AppRoot.IsTestHost &&
				Refii.Level.Engine_SkyEnvironmentNode.Current is { } sky &&
				sky.Lighting is not null &&
				sky.GetParent() is { } regionRoot)
			{
				QueueRefreshInstanceLighting(regionRoot, sky.Lighting);
			}
		}
	}

	public static void ReloadConfig()
	{
		_ambientScale = null;
		_buildingInteriorAmbientScale = null;
		_checkedGen = Refii.Core.Engine_Switches.Generation;
		if (!Refii.Core.Engine_AppRoot.IsTestHost &&
			Refii.Level.Engine_SkyEnvironmentNode.Current is { } sky &&
			sky.Lighting is not null &&
			sky.GetParent() is { } regionRoot)
		{
			QueueRefreshInstanceLighting(regionRoot, sky.Lighting);
		}
	}

	/// <summary>
	/// `building_interior_ambient_scale`: multiplier on the ambient floor inside an enclosed
	/// building in an outdoor level (1.0 = vanilla default; caves and sky-less levels are
	/// unaffected and use `interior_ambient_scale`).
	/// </summary>
	public static float BuildingInteriorAmbientScale
	{
		get
		{
			CheckGeneration();
			if (_buildingInteriorAmbientScale is null)
			{
				try
				{
					_buildingInteriorAmbientScale = System.Math.Clamp(
						Refii.Core.Engine_RefiiConfig.Float(
							Refii.Core.Engine_RefiiConfig.Read(out _),
							"building_interior_ambient_scale", 1.0f),
						0.0f, 4.0f);
				}
				catch (System.Exception)
				{
					_buildingInteriorAmbientScale = 1.0f;
				}
			}

			return _buildingInteriorAmbientScale.Value;
		}
	}

	/// <summary>
	/// Base ambient multiplier applied in Modern mode when the Filmic tone curve is active,
	/// compensating for the Filmic S-curve's stronger midtone compression while keeping the
	/// normalized <c>ambient_light_scale</c> CVar default at <c>1.0</c>.
	/// </summary>
	public const float FilmicBaseAmbientScale = 2.0f;

	/// <summary>
	/// Active tone-curve ambient multiplier: <see cref="FilmicBaseAmbientScale"/> (<c>2.0x</c>) in
	/// Modern mode when <see cref="Engine_PostFxParams.FilmicCurve"/> is active, <c>1.0x</c> otherwise.
	/// </summary>
	public static float ActiveToneCurveAmbientScale =>
		Engine_LightingFrame.ModernModel && Engine_PostFxParams.FilmicCurve
			? FilmicBaseAmbientScale
			: 1.0f;

	/// <summary>
	/// Effective ambient scale for the current frame: applies <see cref="ActiveToneCurveAmbientScale"/>
	/// and <see cref="BuildingInteriorAmbientScale"/> on top of <see cref="AmbientScale"/> when inside
	/// a building in an outdoor (sky-bearing) level.
	/// </summary>
	public static float EffectiveAmbientScale(LightingFrame? frame)
	{
		float scale = AmbientScale * ActiveToneCurveAmbientScale;
		bool isCaveLevel = Engine_LightingFrame.CurrentInterior || (frame is not null && !frame.HasSky);
		if (!isCaveLevel && Engine_LightZones.IsViewerIndoors)
		{
			scale *= BuildingInteriorAmbientScale;
		}

		return scale;
	}

	/// <summary>
	/// The remaster's `ambient_light_scale`: lifts the probe field's flat band and the theme
	/// ambient — the light the UNBAKED population stands in (rocks, barrels, doors, characters
	/// — everything the artists left movable and therefore probe-lit). It is what brings that
	/// population into the same warmth as the baked bridge beside it. 1.0 = shipped level.
	/// Public because the sky-ambient global the probes blend toward must sit in the same
	/// scaled space as the probe L0 it mixes with.
	/// </summary>
	public static float AmbientScale
	{
		get
		{
			if (_ambientScale is null)
			{
				try
				{
					_ambientScale = System.Math.Clamp(
						Refii.Core.Engine_RefiiConfig.Float(
							Refii.Core.Engine_RefiiConfig.Read(out _),
							"ambient_light_scale", 1.0f),
						0.1f, 4.0f);
				}
				catch (System.Exception)
				{
					_ambientScale = 1.0f;
				}

				// Diagnostic override, unclamped: REFII_AMBIENT_SCALE=<f>. The clamp above is the
				// launcher's range; an instrument run needs to go past it to find the level.
				if (float.TryParse(System.Environment.GetEnvironmentVariable("REFII_AMBIENT_SCALE"),
						System.Globalization.NumberStyles.Float,
						System.Globalization.CultureInfo.InvariantCulture, out float forced) &&
					forced > 0f)
				{
					_ambientScale = forced;
				}
			}

			return _ambientScale.Value;
		}
	}

	/// <summary>Metadata key holding the placement id whose authored bake this node owns.</summary>
	public const string BakeKeyMeta = "bake_key";

	/// <summary>Metadata key on the props container holding the level's gdb path.</summary>
	public const string LevelGdbMeta = "level_gdb";

	/// <summary>
	/// Metadata key on a placement whose texture bake is bound when its mesh stands, holding the
	/// level's gdb path. Fable 3 states its lightmapped placements this way (Engine_LevelProps);
	/// removed once resolved, the rect then rides <see cref="LightmapRectMeta"/>.
	/// </summary>
	public const string ObjectLightmapLevelMeta = "object_lightmap_level";

	/// <summary>Metadata key holding a bound texture bake's atlas window, as `lightmap_rect` takes it.</summary>
	public const string LightmapRectMeta = "lightmap_rect";

	private static StringName? _objectLightmapLevelMetaName;
	private static StringName? _lightmapRectName;
	private static StringName ObjectLightmapLevelMetaName => _objectLightmapLevelMetaName ??= ObjectLightmapLevelMeta;
	private static StringName LightmapRectName => _lightmapRectName ??= LightmapRectMeta;

	/// <summary>
	/// Binds a placement's object lightmap: its atlas window into `lightmap_rect` and bake mode 4.
	/// False when the container has no texture for the key.
	/// </summary>
	public static bool BindObjectLightmap(GeometryInstance3D node, LightmapContainer? container, ulong key)
	{
		if (!TryGetObjectLightmapRect(container, key, out Rect2 rect))
		{
			return false;
		}

		var window = new Vector4(rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y);
		node.SetInstanceShaderParameter(LightmapRectName, window);
		node.SetMeta(LightmapRectName, window);
		node.SetMeta(BakeModeMetaName, 4);
		return true;
	}

	/// <summary>
	/// The texture bake a placement was built with (<see cref="ObjectLightmapLevelMeta"/>), bound
	/// once and re-applied from <see cref="LightmapRectMeta"/> on every later light.
	/// </summary>
	private static void ApplyStatedObjectLightmap(GeometryInstance3D node)
	{
		if (node.HasMeta(LightmapRectName))
		{
			node.SetInstanceShaderParameter(LightmapRectName, node.GetMeta(LightmapRectName));
			return;
		}

		if (!node.HasMeta(ObjectLightmapLevelMetaName))
		{
			return;
		}

		string gdbPath = node.GetMeta(ObjectLightmapLevelMetaName).AsString();
		node.RemoveMeta(ObjectLightmapLevelMetaName);

		string stated = node.HasMeta(BakeKeyMetaName) ? node.GetMeta(BakeKeyMetaName).AsString() : string.Empty;
		if (stated.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			stated = stated[2..];
		}

		if (ulong.TryParse(stated, System.Globalization.NumberStyles.HexNumber, null, out ulong key) && key != 0)
		{
			BindObjectLightmap(node, ContainerFor(gdbPath), key);
		}
	}

	private static readonly System.Collections.Generic.Dictionary<string, LightmapContainer?>
		ContainerCache = new();

	/// <summary>The lightmap containers cached and the bytes of their blobs, for the heap census.</summary>
	internal static (long Containers, long Bytes) ContainerCensus()
	{
		lock (ContainerCache)
		{
			long containers = 0, bytes = 0;
			foreach (LightmapContainer? container in ContainerCache.Values)
			{
				if (container is null)
				{
					continue;
				}

				++containers;
				foreach (TerrainLightmap terrain in container.TerrainLightmapBlobs.Values)
				{
					bytes += terrain.Blob.LongLength;
				}

				foreach (byte[] blob in container.ObjectLightmapBlobs.Values)
				{
					bytes += blob.LongLength;
				}

				foreach (uint[] bakes in container.VertexBakes.Values)
				{
					bytes += bakes.LongLength * 4;
				}

				foreach (uint[] words in container.U32Arrays.Values)
				{
					bytes += words.LongLength * 4;
				}

				foreach (byte[] block in container.Blocks48.Values)
				{
					bytes += block.LongLength;
				}
			}

			return (containers, bytes);
		}
	}

	/// <summary>
	/// The level's .lmp, loaded once per gdb path and cached.
	///
	/// The streamer needs it and runs long after the build did. Rather than carry a million
	/// baked colours through the saved scene as node metadata, the scene carries the gdb path
	/// and the bakes are read back out of the bank the same way the environment theme is.
	/// </summary>
	/// <remarks>
	/// Callable from a worker. The first call for a level parses the whole .lmp; the load stays
	/// inside the lock so two callers asking at once parse once.
	/// </remarks>
	public static LightmapContainer? ContainerFor(string gdbPath)
	{
		if (gdbPath.Length == 0)
		{
			return null;
		}

		lock (ContainerCache)
		{
			if (ContainerCache.TryGetValue(gdbPath, out LightmapContainer? cached))
			{
				return cached;
			}

			TryLoad(gdbPath, Refii.Models.Engine_BnkMeshLoader.ReadBytes,
				out LightmapContainer? container, out _);
			ContainerCache[gdbPath] = container;
			return container;
		}
	}

	/// <summary>
	/// The container the level build already parsed, kept for <see cref="ContainerFor"/> so the
	/// first placement to bind its bake on the main thread does not parse the file again.
	/// </summary>
	public static void AdoptContainer(string gdbPath, LightmapContainer container)
	{
		if (gdbPath.Length == 0)
		{
			return;
		}

		lock (ContainerCache)
		{
			ContainerCache.TryAdd(gdbPath, container);
		}
	}

	public static void ClearContainerCache()
	{
		if (!Refii.Core.Engine_AppRoot.IsTestHost)
		{
			try
			{
				RenderingServer.GlobalShaderParameterSet(GlobalPropLightmapsName, default(Variant));
			}
			catch
			{
			}
		}

		lock (ContainerCache)
		{
			foreach (LightmapContainer? container in ContainerCache.Values)
			{
				if (container is null)
				{
					continue;
				}

				if (container.ObjectAtlas is not null && GodotObject.IsInstanceValid(container.ObjectAtlas))
				{
					container.ObjectAtlas.Dispose();
					container.ObjectAtlas = null;
				}

				lock (container.ObjectTextures)
				{
					foreach (ImageTexture? tex in container.ObjectTextures.Values)
					{
						if (tex is not null && GodotObject.IsInstanceValid(tex))
						{
							tex.Dispose();
						}
					}

					container.ObjectTextures.Clear();
				}
			}

			ContainerCache.Clear();
		}
	}

	/// <summary>
	/// The authored per-vertex bake of a placement, per surface of <paramref name="mesh"/> in its
	/// vertex order, or null when there is no bake for this placement or it does not fit.
	///
	/// THE FIT CHECK IS THE GUEST'S OWN. It refuses a bake buffer unless its byte size equals
	/// four times the summed vertex count over every submesh, and so do we — a bake that
	/// belongs to a different build of the model would otherwise be applied vertex-for-vertex
	/// to the wrong geometry, which looks like bad lighting rather than like a mismatch.
	///
	/// The bake is per PLACEMENT and every placement of a model shares one cached mesh, so it is
	/// not written into the mesh: classic reads it from the shared bake texture
	/// (Engine_ClassicVertexBakes), and the placements keep merging into instanced draws.
	/// </summary>
	public static Color[][]? TryBuildVertexColours(LightmapContainer? bakes, ulong key, ArrayMesh mesh)
	{
		if (bakes is null || mesh.GetSurfaceCount() == 0)
		{
			return null;
		}

		// An exact 64-bit lookup, which is what the guest does — `cmpld` against the whole key
		// in every one of the three maps that share this key space (`sub_82A54C88`,
		// `sub_822FFD28`, `sub_8222CC08`), with no mask and no half-swap. Scanning for a matching
		// LOW half silently picks the first placement sharing a hash with another; see
		// PropBlockBuild.InstanceBakeKeys for why that is not merely untidy.
		if (!bakes.VertexBakes.TryGetValue(key, out uint[]? colours))
		{
			return null;
		}

		// THE FIT CHECK IS AGAINST THE FILE'S VERTEX COUNT, NOT OURS, and the two are not the
		// same number. Splitting a mesh buffer into one surface per submesh re-emits every
		// vertex two submeshes share, so summing our surfaces overcounts — BS_Market_Wall_Straight
		// came to 770 against a 762-vertex bake purely because its four floorboard submeshes
		// share eight vertices between them. Checking that sum rejected the bake as a mismatch
		// when it was the right bake for the right model all along.
		int[] sourceVertices = mesh.HasMeta(Refii.Models.Engine_MdlMesh.SourceVertexMapMeta) &&
			mesh.GetMeta(Refii.Models.Engine_MdlMesh.SourceVertexMapMeta) is
			{ VariantType: Variant.Type.PackedInt32Array } mapMeta
			? mapMeta.AsInt32Array()
			: Array.Empty<int>();

		int total = mesh.GetMeta(
			Refii.Models.Engine_MdlMesh.SourceVertexTotalMeta, default) is
			{ VariantType: Variant.Type.Int } totalMeta
			? totalMeta.AsInt32()
			: 0;

		if (total == 0 || total != colours.Length || sourceVertices.Length == 0)
		{
			return null;
		}

		int cursor = 0;
		SourceSurfaces source = SurfacesOf(mesh);
		var baked = new Color[source.Arrays.Length][];
		for (int s = 0; s < source.Arrays.Length; s++)
		{
			Godot.Collections.Array arrays = source.Arrays[s];
			int count = source.VertexCounts[s];

			// Through the map, not by running cursor. Our vertex order is not the file's — a
			// shared vertex appears in two surfaces and everything after it shifts — so walking
			// the bake sequentially would paint the right colours onto the wrong vertices even
			// where the totals happened to agree.
			var vertexColours = new Color[count];
			var sourced = new bool[count];
			int unsourced = 0;
			for (int v = 0; v < count; v++)
			{
				int from = cursor + v < sourceVertices.Length ? sourceVertices[cursor + v] : -1;
				if (from >= 0 && from < colours.Length)
				{
					Color c = FromD3DColour(colours[from]);
					vertexColours[v] = c;
					sourced[v] = true;
					float lum = 0.2125f * c.R + 0.7154f * c.G + 0.0721f * c.B;
					VertexBakeSamples++;
					VertexBakeSum += lum;
					VertexBakeMax = MathF.Max(VertexBakeMax, lum);
				}
				else
				{
					++unsourced;
				}
			}

			// A vertex with no file vertex behind it takes its nearest sourced neighbour's bake.
			// These are the vertices a coarser model LOD appends after the LOD-0 vertices
			// (Engine_MdlLods), and the bake has no entry for them; full-bright would draw a
			// distant LOD brighter than the same object up close.
			if (unsourced != 0)
			{
				FillFromNearest(arrays, vertexColours, sourced, count);
				VertexBakeUnsourced += unsourced;
			}

			baked[s] = vertexColours;
			cursor += count;
		}

		return baked;
	}

	private sealed class SourceSurfaces
	{
		public Godot.Collections.Array[] Arrays = System.Array.Empty<Godot.Collections.Array>();
		public int[] VertexCounts = System.Array.Empty<int>();
		public Mesh.PrimitiveType[] Primitives = System.Array.Empty<Mesh.PrimitiveType>();
		public string[] Names = System.Array.Empty<string>();
		public Material?[] Materials = System.Array.Empty<Material?>();
		public Godot.Collections.Dictionary?[] Lods = System.Array.Empty<Godot.Collections.Dictionary?>();
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ArrayMesh, SourceSurfaces>
		SurfaceCache = new();

	private static SourceSurfaces SurfacesOf(ArrayMesh mesh)
	{
		lock (SurfaceCache)
		{
			if (SurfaceCache.TryGetValue(mesh, out SourceSurfaces? cached))
			{
				return cached;
			}
		}

		int surfaces = mesh.GetSurfaceCount();
		var read = new SourceSurfaces
		{
			Arrays = new Godot.Collections.Array[surfaces],
			VertexCounts = new int[surfaces],
			Primitives = new Mesh.PrimitiveType[surfaces],
			Names = new string[surfaces],
			Materials = new Material?[surfaces],
			Lods = new Godot.Collections.Dictionary?[surfaces],
		};

		for (int s = 0; s < surfaces; s++)
		{
			read.Arrays[s] = Refii.Models.Engine_MdlSurfaceArrays.Of(mesh, s);
			read.VertexCounts[s] = Refii.Models.Engine_MdlSurfaceArrays.VertexCount(mesh, s);
			read.Primitives[s] = mesh.SurfaceGetPrimitiveType(s);
			read.Names[s] = mesh.SurfaceGetName(s);
			read.Materials[s] = mesh.SurfaceGetMaterial(s);
			read.Lods[s] = Refii.Models.Engine_MdlMesh.SurfaceLods(mesh, s);
		}

		lock (SurfaceCache)
		{
			return SurfaceCache.TryGetValue(mesh, out SourceSurfaces? raced) ? raced : Add(mesh, read);
		}

		static SourceSurfaces Add(ArrayMesh mesh, SourceSurfaces read)
		{
			SurfaceCache.AddOrUpdate(mesh, read);
			return read;
		}
	}

	/// <summary>
	/// A D3DCOLOR to a Godot colour. The guest declares this stream as
	/// <c>D3DDECLTYPE_D3DCOLOR</c> (type index 4, four bytes), which is ARGB.
	///
	/// The value stays GAMMA-ENCODED — the shader decodes it, because that is where the guest
	/// does it and the exponent is a runtime constant there.
	/// </summary>
	private static Color FromD3DColour(uint value) => new(
		((value >> 16) & 0xFF) / 255.0f,
		((value >> 8) & 0xFF) / 255.0f,
		(value & 0xFF) / 255.0f,
		((value >> 24) & 0xFF) / 255.0f);

	/// <summary>
	/// Samples the probe field once PER MULTIMESH INSTANCE and stores the flat band as Godot's
	/// per-instance colour.
	///
	/// A MultiMesh carries one colour per instance and no room for the L1 bands, so only L0
	/// travels — which is the term that matters: across this region L0 averages 0.60 while L1
	/// stays inside ±0.27. Returns whether anything was written, so the caller knows whether
	/// the block still needs a single node-level sample as a fallback.
	/// </summary>
	/// <summary>Diagnostics: the applied vertex bake luminance (as stored, before the shader's
	/// decode) over every vertex baked since the counters were last read.</summary>
	public static long VertexBakeSamples;

	/// <summary>
	/// Baked vertices that had no file vertex behind them — coarser-LOD growth — and took a
	/// neighbour's bake. Run total.
	/// </summary>
	public static long VertexBakeUnsourced;

	/// <summary>
	/// Gives every unsourced vertex the bake of the nearest sourced vertex in the same surface,
	/// through a uniform grid over the sourced positions. A surface with no sourced vertex at all
	/// keeps neutral grey rather than full-bright.
	/// </summary>
	private static void FillFromNearest(
		Godot.Collections.Array arrays, Color[] colours, bool[] sourced, int count)
	{
		if (arrays[(int)Mesh.ArrayType.Vertex].Obj is not Vector3[] positions || positions.Length < count)
		{
			for (int v = 0; v < count; v++)
			{
				if (!sourced[v])
				{
					colours[v] = new Color(0.5f, 0.5f, 0.5f, 1f);
				}
			}

			return;
		}

		var bounds = new Aabb();
		bool any = false;
		for (int v = 0; v < count; v++)
		{
			if (!sourced[v])
			{
				continue;
			}

			bounds = any ? bounds.Expand(positions[v]) : new Aabb(positions[v], Vector3.Zero);
			any = true;
		}

		if (!any)
		{
			for (int v = 0; v < count; v++)
			{
				colours[v] = new Color(0.5f, 0.5f, 0.5f, 1f);
			}

			return;
		}

		float cell = Mathf.Max(bounds.Size.Length() / 24f, 0.01f);
		var grid = new Dictionary<(int, int, int), List<int>>();
		(int, int, int) Key(Vector3 p) => (
			(int)Mathf.Floor((p.X - bounds.Position.X) / cell),
			(int)Mathf.Floor((p.Y - bounds.Position.Y) / cell),
			(int)Mathf.Floor((p.Z - bounds.Position.Z) / cell));

		for (int v = 0; v < count; v++)
		{
			if (!sourced[v])
			{
				continue;
			}

			var key = Key(positions[v]);
			if (!grid.TryGetValue(key, out List<int>? bucket))
			{
				bucket = new List<int>(4);
				grid[key] = bucket;
			}

			bucket.Add(v);
		}

		for (int v = 0; v < count; v++)
		{
			if (sourced[v])
			{
				continue;
			}

			var (kx, ky, kz) = Key(positions[v]);
			int best = -1;
			float bestDistance = float.MaxValue;

			// Widen ring by ring until something is found; the ring after the first hit can
			// still hold a nearer vertex, so one more ring is searched before stopping.
			int foundAt = -1;
			for (int ring = 0; ring < 64 && (foundAt < 0 || ring <= foundAt + 1); ring++)
			{
				for (int x = kx - ring; x <= kx + ring; x++)
				for (int y = ky - ring; y <= ky + ring; y++)
				for (int z = kz - ring; z <= kz + ring; z++)
				{
					if (System.Math.Max(System.Math.Abs(x - kx),
							System.Math.Max(System.Math.Abs(y - ky), System.Math.Abs(z - kz))) != ring ||
						!grid.TryGetValue((x, y, z), out List<int>? bucket))
					{
						continue;
					}

					foreach (int candidate in bucket)
					{
						float distance = positions[candidate].DistanceSquaredTo(positions[v]);
						if (distance < bestDistance)
						{
							bestDistance = distance;
							best = candidate;
						}
					}
				}

				if (best >= 0 && foundAt < 0)
				{
					foundAt = ring;
				}
			}

			colours[v] = best >= 0 ? colours[best] : new Color(0.5f, 0.5f, 0.5f, 1f);
		}
	}
	public static double VertexBakeSum;
	public static float VertexBakeMax;

	/// <summary>
	/// One probe colour per batched instance, written into the batch's own buffer (the slot's
	/// last four floats, behind the transform) rather than through `SetInstanceColor`: the batch
	/// is uploaded whole once both halves are in (Engine_LevelProps), not instance by instance.
	/// </summary>
	public static bool ApplyMultiMeshProbes(
		float[] buffer,
		int stride,
		System.Collections.Generic.IReadOnlyList<Transform3D> instances,
		LightProbeField? probes,
		LightmapContainer? bakes = null,
		System.Collections.Generic.IReadOnlyList<ulong>? bakeKeys = null,
		System.Collections.Generic.IReadOnlyList<uint>? authoredColours = null)
	{
		bool haveField = probes is not null && probes.HasData;
		if (!haveField && bakes is null && authoredColours is null)
		{
			return false;
		}

		bool any = false;
		for (int i = 0; i < instances.Count; i++)
		{
			// AN AUTHORED COLOUR OUTRANKS THE PROBE FIELD — the .lmp's per-instance foliage
			// bake, same precedence the guest gives amb1 over PRT. Stored with the vertex
			// shader's 2.2 applied so the pixel side's 1.3 completes the 2.86 compose, which
			// is exactly the mode-2 contract the instance-colour path already speaks.
			if (authoredColours is not null && i < authoredColours.Count &&
				authoredColours[i] != 0)
			{
				uint c = authoredColours[i];
				WriteColour(buffer, i * stride + 12, new Color(
					Mathf.Pow(((c >> 16) & 0xFF) / 255.0f, 2.2f),
					Mathf.Pow(((c >> 8) & 0xFF) / 255.0f, 2.2f),
					Mathf.Pow((c & 0xFF) / 255.0f, 2.2f),
					1f));
				any = true;
				continue;
			}

			Vector3 godotPosition = instances[i].Origin;
			var gamePosition = new Vector3(godotPosition.X, godotPosition.Z, godotPosition.Y);
			ProbeSample sample = haveField ? probes!.Sample(gamePosition) : default;

			// No 48-byte fallback: those blocks are the title's probe result for SKINNED models
			// (`sub_82219178`); a static the field has no cell for is flat, as the title draws it.

			// The flat band only, SCALED BY THE BAND-0 CONSTANT. The three that follow it are
			// directional and there is nowhere on a MultiMesh instance to put them.
			//
			// The constant matters and is easy to lose here: the probe path evaluates the SH
			// through D3DXSHEvalDirection's basis, whose first term is 0.2820948, so a raw L0
			// stored straight into an instance colour is 3.5x hotter than the same bake taken
			// the other way. With the theme's factor of ~3.5 on top of it, every tree in the
			// region came out white and the grass turned teal.
			// The same ambient scale the singles take in ApplyInstanceLighting — one rule for
			// the whole probe-lit population, batched or not.
			float band = ShBand0 * AmbientScale;
			var colour = new Color(
				sample.R.X * band, sample.G.X * band, sample.B.X * band, 1f);
			if (colour.R > 0f || colour.G > 0f || colour.B > 0f)
			{
				any = true;
			}

			WriteColour(buffer, i * stride + 12, colour);
		}

		return any;
	}

	private static void WriteColour(float[] buffer, int at, Color colour)
	{
		buffer[at] = colour.R;
		buffer[at + 1] = colour.G;
		buffer[at + 2] = colour.B;
		buffer[at + 3] = colour.A;
	}

	/// <summary>
	/// The remaster's bake dial as the shader computes it (`fable_bake` in
	/// fable_lighting.gdshaderinc): interiors and a disabled dial ride at full strength.
	/// Mirrored here so the same factor can be applied to a static prop that has no authored
	/// bake to scale.
	/// </summary>
	public static float AuthoredBakeWeight
	{
		get
		{
			float weight = BakeWeight;
			float authored = weight > 1f ? weight : (InteriorNow > 0.5f || weight <= 0f ? 1f : weight);
			return authored * InteriorBakeNow;
		}
	}

	/// <summary>Mirrors of the global shader parameters, so this never round-trips the render thread.</summary>
	public static float BakeWeight = 0.45f;

	public static float InteriorNow;

	public static float InteriorBakeNow = 1f;

	/// <summary>
	/// Applies the authored-bake dial to a PROBE-LIT STATIC PROP as well.
	///
	/// The guest applies no per-path ambient gain: the authored-bake and PRT populations differ
	/// only by vertex stream and shader permutation (`sub_82214940` sets a stream binding and,
	/// for amb1, a single vertex-fetch OFFSET in c20; no colour, no intensity). The remaster's
	/// dial on authored bakes alone is therefore a new asymmetry, which an unbaked prop must
	/// share to match its neighbours.
	///
	/// It must NOT reach characters: the shader deliberately exempts the probe path for them.
	/// Opt-in, passed only by the prop paths.
	/// </summary>
	public static bool DialProbeLitProps;

	/// <summary>
	/// Writes one instance's own lighting into its shader parameters: the baked probe sample it
	/// is carrying, its bake mode and its bake dial. The theme's half is global and moves every
	/// frame (<see cref="PublishThemeLighting"/>); <paramref name="frame"/> is not read.
	///
	/// Called when a mesh arrives and again when a dial changes. Nothing here re-samples the
	/// probe field — the bake does not move.
	/// </summary>
	public static void ApplyInstanceLighting(GeometryInstance3D node, LightingFrame? frame) =>
		ApplyInstanceLighting(node, frame, staticProp: false);

	/// <summary>
	/// Nodes the prop paths have claimed. The region's periodic re-light walks one level ABOVE
	/// the region and cannot be trusted to contain only props, so whether a node is dressing is
	/// remembered here rather than inferred from where it sits in the tree — getting that wrong
	/// would dim every character.
	/// </summary>
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GeometryInstance3D, object> StaticProps = new();

	private static readonly object Marked = new();

	public static void MarkStaticProp(GeometryInstance3D node)
	{
		if (!StaticProps.TryGetValue(node, out _))
		{
			StaticProps.Add(node, Marked);
		}
	}

	public static void ApplyInstanceLighting(GeometryInstance3D node, LightingFrame? frame, bool staticProp)
	{
		if (staticProp)
		{
			MarkStaticProp(node);
		}
		else
		{
			staticProp = StaticProps.TryGetValue(node, out _);
		}

		// The classic renderer lights per draw from the same seam: every lit instance passes here,
		// and one with a transfer and a probe sample takes the title's AMB2 pair.
		long from = System.Diagnostics.Stopwatch.GetTimestamp();
		Refii.Models.Engine_ClassicLitInstances.Attach(node);
		Refii.Models.Engine_ModernFur.Attach(node);
		long attached = System.Diagnostics.Stopwatch.GetTimestamp();
		long find = Refii.Models.Engine_ClassicMaterial.AmbientFindTicks, compose = Refii.Models.Engine_ClassicMaterial.AmbientComposeTicks;
		long duplicate = Refii.Models.Engine_ClassicMaterial.AmbientDuplicateTicks, bind = Refii.Models.Engine_ClassicMaterial.AmbientBindTicks;
		using (Refii.Core.Engine_FramePhases.Measure("classic.transfer"))
		{
			Refii.Models.Engine_ClassicTransfer.Apply(node);
		}
		long transferred = System.Diagnostics.Stopwatch.GetTimestamp();
		_split = new LightSplit(attached - from, transferred - attached,
			Refii.Models.Engine_ClassicMaterial.AmbientFindTicks - find,
			Refii.Models.Engine_ClassicMaterial.AmbientComposeTicks - compose,
			Refii.Models.Engine_ClassicMaterial.AmbientDuplicateTicks - duplicate,
			Refii.Models.Engine_ClassicMaterial.AmbientBindTicks - bind, -1);

		// HasMeta FIRST, AND IT IS NOT BELT-AND-BRACES. `GetMeta(name, default)` reads as a safe
		// lookup with a fallback and is not one: Godot returns the fallback only when the fallback
		// is itself non-nil, and `default` for a Variant is nil. Every absent key otherwise takes
		// the ERR_FAIL path, printing an error plus a full managed backtrace on the main thread —
		// once per instance, every time time of day moves.
		// The meta Variant and its Array are disposed here: left to their finalizers they survive
		// a GC each, once per instance every time time of day moves.
		bool probeValid = false;
		using Variant probeMeta = node.HasMeta(ProbeMetaName) ? node.GetMeta(ProbeMetaName) : default;
		if (probeMeta.VariantType == Variant.Type.Array)
		{
			using Godot.Collections.Array probe = probeMeta.AsGodotArray();
			if (probe.Count == 3)
			{
				probeValid = true;
				Vector4 r = probe[0].AsVector4(), g = probe[1].AsVector4(), b = probe[2].AsVector4();
				// The remaster's ambient scale rides the L0 band only: scaling the directional
				// L1 bands too would exaggerate the field's tilt, not its level.
				float probeScale = AmbientScale;
				Refii.Core.Engine_InstanceParams.Set(node, Param_probe_r,
					new Vector4(r.X * probeScale, r.Y, r.Z, r.W));
				Refii.Core.Engine_InstanceParams.Set(node, Param_probe_g,
					new Vector4(g.X * probeScale, g.Y, g.Z, g.W));
				Refii.Core.Engine_InstanceParams.Set(node, Param_probe_b,
					new Vector4(b.X * probeScale, b.Y, b.Z, b.W));
			}
		}

		if (staticProp)
		{
			ApplyStatedObjectLightmap(node);
		}

		// The node's stated mode wins; a probe sample is only the fallback answer.
		float mode = node.HasMeta(BakeModeMetaName) &&
			node.GetMeta(BakeModeMetaName) is { VariantType: Variant.Type.Int } stored
				? stored.AsInt32()
				: (probeValid ? 1f : 0f);

		// THE INSTANCE CARRIES ONLY WHAT IS ITS OWN: the dial and the bake mode. The theme's
		// factor multiplies the bake for every mode — `bake * lightmap_colour.rgb *
		// fable_lightmap_tint`, the guest's own `bake * g_LightmapFactorAndSRGBGamma +
		// g_GlobalAmbient` — and arrives as a global (PublishThemeLighting). Mode 1 only: every
		// other mode already takes the dial inside `fable_bake`, and applying it twice would
		// halve them again.
		float dial = staticProp && DialProbeLitProps && mode > 0.5f && mode < 1.5f
			? AuthoredBakeWeight
			: 1f;

		// `lite_bake_variants`: a static prop's lite surfaces on the build for this mode, before the
		// mode itself is stated, so a copy is only ever drawn by instances in its own mode.
		if (staticProp && Refii.Models.Engine_ShaderVariant.Lite)
		{
			Refii.Models.Engine_ShaderSpecialisation.ApplyBakeMode(node, (int)MathF.Round(mode));
		}

		Refii.Core.Engine_InstanceParams.Set(node, Param_lightmap_colour, new Vector4(dial, dial, dial, mode));

		bool isIndoorInstance = (node.Layers & (Engine_LightZones.IndoorMask | Engine_LightZones.IndoorLampHostMask)) != 0u;
		float instanceAmbient = isIndoorInstance ? BuildingInteriorAmbientScale : 1.0f;
		Refii.Core.Engine_InstanceParams.Set(node, Param_ambient_scale, instanceAmbient);
		_split = _split with { Params = System.Diagnostics.Stopwatch.GetTimestamp() - transferred };
	}

	/// <summary>
	/// Evaluates <paramref name="node"/>'s instance lighting parameters directly from its metadata
	/// and layer mask without calling <c>GetInstanceShaderParameter</c> (which stalls the main thread
	/// on <c>RenderingServer</c> whenever a parameter is unset).
	/// </summary>
	public static void ReadInstanceLighting(
		GeometryInstance3D node,
		bool staticProp,
		out bool hasProbe,
		out Vector4 probeR,
		out Vector4 probeG,
		out Vector4 probeB,
		out Vector4 lightmapColour,
		out float ambientScale)
	{
		hasProbe = false;
		probeR = Vector4.Zero;
		probeG = Vector4.Zero;
		probeB = Vector4.Zero;

		using Variant probeMeta = node.HasMeta(ProbeMetaName) ? node.GetMeta(ProbeMetaName) : default;
		if (probeMeta.VariantType == Variant.Type.Array)
		{
			using Godot.Collections.Array probe = probeMeta.AsGodotArray();
			if (probe.Count == 3)
			{
				hasProbe = true;
				Vector4 r = probe[0].AsVector4(), g = probe[1].AsVector4(), b = probe[2].AsVector4();
				float probeScale = AmbientScale;
				probeR = new Vector4(r.X * probeScale, r.Y, r.Z, r.W);
				probeG = new Vector4(g.X * probeScale, g.Y, g.Z, g.W);
				probeB = new Vector4(b.X * probeScale, b.Y, b.Z, b.W);
			}
		}

		float mode = node.HasMeta(BakeModeMetaName) &&
			node.GetMeta(BakeModeMetaName) is { VariantType: Variant.Type.Int } stored
				? stored.AsInt32()
				: (hasProbe ? 1f : 0f);

		float dial = staticProp && DialProbeLitProps && mode > 0.5f && mode < 1.5f
			? AuthoredBakeWeight
			: 1f;

		lightmapColour = new Vector4(dial, dial, dial, mode);
		bool isIndoorInstance = (node.Layers & (Engine_LightZones.IndoorMask | Engine_LightZones.IndoorLampHostMask)) != 0u;
		ambientScale = isIndoorInstance ? BuildingInteriorAmbientScale : 1.0f;
	}

	/// <summary>The stages of the last <see cref="ApplyInstanceLighting"/>, in stopwatch ticks; Params -1 until it finished.</summary>
	private readonly record struct LightSplit(
		long Attach, long Transfer, long Find, long Compose, long Duplicate, long Bind, long Params);

	private static LightSplit _split;

	/// <summary>The last <see cref="ApplyInstanceLighting"/>'s split, for the streamer's slow-step log.</summary>
	/// <remarks>
	/// Kept as ticks and formatted only when read: every lit instance (each entity relight up to
	/// three times, every streamed prop, each node of a relight walk) passed through here, and
	/// built this string for a log line that prints only on a slow step.
	/// </remarks>
	public static string LastLightSplit
	{
		get
		{
			LightSplit split = _split;
			if (split == default)
			{
				return string.Empty;
			}

			double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			string text = $"attach {split.Attach * toMs:0.0}, transfer {split.Transfer * toMs:0.0} " +
				$"(find {split.Find * toMs:0.0}, " +
				$"compose {split.Compose * toMs:0.0}, " +
				$"duplicate {split.Duplicate * toMs:0.0}, " +
				$"bind {split.Bind * toMs:0.0})";
			return split.Params >= 0 ? text + $", params {split.Params * toMs:0.0}" : text;
		}
	}

	/// <summary>
	/// Publishes the theme's lighting to every surface, as global shader parameters. Every frame.
	/// </summary>
	/// <remarks>
	/// ONE VALUE FOR THE WHOLE REGION, so it is global rather than per instance. Written per
	/// instance it could only move on the periodic re-light, which walked the region a slice per
	/// frame: every time-of-day step and every tenth of a painted blend crossed the world as a
	/// visible wave, and between steps nothing moved at all.
	/// </remarks>
	public static void PublishThemeLighting(LightingFrame? frame)
	{
		float[] lightmap = frame?.LightmapColour ?? WhiteLightmap;
		RenderingServer.GlobalShaderParameterSet(Global_lightmap_tint,
			new Vector3(lightmap[0], lightmap[1], lightmap[2]));

		float ar = 0f, ag = 0f, ab = 0f;
		if (frame is not null && frame.HasAmbient)
		{
			float ambientScale = AmbientScale * ActiveToneCurveAmbientScale;
			ar = frame.AmbientColour[0] * frame.AmbientEnergy * ambientScale;
			ag = frame.AmbientColour[1] * frame.AmbientEnergy * ambientScale;
			ab = frame.AmbientColour[2] * frame.AmbientEnergy * ambientScale;
		}

		RenderingServer.GlobalShaderParameterSet(Global_ambient_colour, new Vector3(ar, ag, ab));

		// White when the theme states none, because this colour is the FLOOR of a lerp toward
		// 1 rather than a tint — black here would drive every normal-mapped surface's ambient
		// to nothing.
		float[] darkening = frame?.DarkeningColour ?? WhiteLightmap;
		RenderingServer.GlobalShaderParameterSet(Global_darkening,
			new Vector3(darkening[0], darkening[1], darkening[2]));

		// THE MAIN LIGHT, for the surfaces that cannot take it through Godot's `light()`.
		//
		// Water is `unshaded` — it composes reflection, refraction and a sun glint itself, so no
		// DirectionalLight3D ever reaches it and it needs the light stated.
		//
		// It is the SAME light, proven rather than assumed: the offscreen water renderer
		// (`sub_82B3CDE8`) uploads `light+0xD0` of the last PRIMITIVE_INSTANCE_DIRECTIONAL_LIGHT
		// in the frame's list to the binding its pixel shader reads as `g_LightColor`, and that
		// +0xD0 is written from the day-set theme's MAIN_LIGHT_COLOUR. That is the light
		// Engine_LevelLighting.UpdateSun already builds, so both now move together.
		//
		// TOWARD the sun, and X is flipped for the same reason the disc's is: SunDirection is
		// stated in the guest's axes and the level root is mirrored through X. This is the disc's
		// vector, NOT the DirectionalLight3D's forward — that one is the negation.
		PublishSunLighting(frame);
	}

	public static void PublishSunLighting(LightingFrame? frame)
	{
		float[] sunDir = frame?.SunDirection ?? new float[] { 0f, 1f, 0f };
		RenderingServer.GlobalShaderParameterSet(Global_sun_toward,
			Refii.Level.Engine_DynamicSkyLighting.OverridesSun ? Refii.Level.Engine_DynamicSkyLighting.SunToward
			: new Vector3(-sunDir[0], sunDir[1], sunDir[2]));

		float[] sunRgb = frame?.SunColour ?? WhiteLightmap;
		float sunEnergy = frame?.SunEnergy ?? 1f;
		RenderingServer.GlobalShaderParameterSet(Global_sun_colour,
			Refii.Level.Engine_DynamicSkyLighting.ResolveRadiance(new Vector3(sunRgb[0] * sunEnergy, sunRgb[1] * sunEnergy, sunRgb[2] * sunEnergy)));
	}

	/// <summary>
	/// Re-applies the instance lighting across a region that is already standing.
	///
	/// Two things need this and they are the two halves of "correct in the editor and in the
	/// game". The inspector's time-of-day slider re-aims the sun and repaints the sky, so the
	/// scene reads as though it updated while every prop keeps the ambient it was built with —
	/// at dawn, a blue midday ambient under an orange sun. And the running game instantiates
	/// the packed scene, where nothing has been applied yet at all.
	/// </summary>
	public static void RefreshInstanceLighting(Node root, LightingFrame frame) =>
		RefreshInstanceLighting(root, frame, staticProp: false);

	/// <summary>
	/// Bumped whenever the region is re-lit. Anything that caches "I already lit this node"
	/// compares against it, so a theme change still reaches every node.
	/// </summary>
	public static long LightingGeneration { get; private set; }

	/// <summary>
	/// Queues a re-light of everything under <paramref name="root"/>, carried out a slice per
	/// frame by <see cref="PumpRefresh"/> rather than in one pass.
	/// </summary>
	/// <remarks>
	/// Instance parameters on every geometry node are too much for one frame. It runs when a
	/// region first stands and when a per-instance dial changes, never on time of day. A new
	/// request restarts the walk. Cast-only nodes are skipped: nothing reads their lighting.
	/// </remarks>
	public static void QueueRefreshInstanceLighting(Node root, LightingFrame frame)
	{
		++LightingGeneration;
		_relightWalk = Descendants(root).GetEnumerator();
		// A copy, not the frame: the walk outlives the frame it was asked on, and the sky node
		// writes its published frames over again a few frames later.
		_relightFrame = (_relightCopy ??= new LightingFrame()).CopyFrom(frame);
	}

	private static LightingFrame? _relightCopy;

	private static System.Collections.Generic.IEnumerator<Node>? _relightWalk;
	private static LightingFrame? _relightFrame;

	/// <summary>Advances a queued re-light for up to <paramref name="budgetMs"/>. Main thread.</summary>
	public static void PumpRefresh(double budgetMs)
	{
		if (_relightWalk is null)
		{
			return;
		}

		long start = System.Diagnostics.Stopwatch.GetTimestamp();
		long budget = (long)(budgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
		while (System.Diagnostics.Stopwatch.GetTimestamp() - start < budget)
		{
			if (!_relightWalk.MoveNext())
			{
				_relightWalk = null;
				_relightFrame = null;
				return;
			}

			if (_relightWalk.Current is GeometryInstance3D instance &&
				GodotObject.IsInstanceValid(instance) &&
				instance.CastShadow != GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
			{
				ApplyInstanceLighting(instance, _relightFrame, staticProp: false);
			}
		}
	}

	public static void RefreshInstanceLighting(Node root, LightingFrame frame, bool staticProp)
	{
		++LightingGeneration;
		foreach (Node node in Descendants(root))
		{
			if (node is GeometryInstance3D instance)
			{
				ApplyInstanceLighting(instance, frame, staticProp);
			}
		}
	}

	/// <summary>
	/// Every node under <paramref name="root"/>, without allocating per node.
	/// </summary>
	/// <remarks>
	/// `GetChildren()` marshals a new array per call; `GetChildCount()` + `GetChild(i)` does not.
	/// Runs on every queued re-light. Order is
	/// depth-first, children before grandchildren. The stack is local because this is a lazy
	/// iterator: two concurrent enumerations would corrupt a shared one.
	/// </remarks>
	private static System.Collections.Generic.IEnumerable<Node> Descendants(Node root)
	{
		var walk = new System.Collections.Generic.Stack<Node>();
		for (int i = root.GetChildCount() - 1; i >= 0; --i)
		{
			walk.Push(root.GetChild(i));
		}

		while (walk.Count != 0)
		{
			// A queued re-light walks across frames (PumpRefresh), so a node pushed on one frame
			// can be freed by the next — a retired entity, a reloaded prop.
			Node node = walk.Pop();
			if (!GodotObject.IsInstanceValid(node))
			{
				continue;
			}

			yield return node;
			if (!GodotObject.IsInstanceValid(node))
			{
				continue;
			}

			for (int i = node.GetChildCount() - 1; i >= 0; --i)
			{
				walk.Push(node.GetChild(i));
			}
		}
	}

	public const string GlobalPropLightmapsName = "fable_prop_lightmaps";

	/// <summary>
	/// The atlas rect for one object's texture lightmap (the amb3 path — building interiors:
	/// 106 blobs game-wide, all 64x64). Builds the region atlas on first call and publishes it
	/// as the `fable_prop_lightmaps` global; the caller hands the rect to the instance as
	/// `lightmap_rect` and raises bake mode 4.
	/// </summary>
	public static bool TryGetObjectLightmapRect(
		LightmapContainer? container, ulong key, out Rect2 rect)
	{
		rect = default;
		if (container is null || container.ObjectLightmapBlobs.Count == 0)
		{
			return false;
		}

		if (!container.ObjectAtlasBuilt)
		{
			BuildObjectAtlas(container);
		}

		if (container.ObjectAtlas is null ||
			!container.ObjectAtlasRects.TryGetValue(key, out rect))
		{
			return false;
		}

		RenderingServer.GlobalShaderParameterSet(
			GlobalPropLightmapsName, container.ObjectAtlas);
		return true;
	}

	private static void BuildObjectAtlas(LightmapContainer container)
	{
		container.ObjectAtlasBuilt = true;

		var decoded = new List<(ulong Key, Image Image)>();
		long area = 0;
		int widest = 1;
		foreach (KeyValuePair<ulong, byte[]> entry in container.ObjectLightmapBlobs)
		{
			// Decoded to an image, never through a texture: `GetImage` on a texture is a GPU
			// readback that stalls every frame in flight, once per lightmap.
			Image? image = container.SharedKeyTable
				? ComposeObjectBake(container, entry.Key, entry.Value)
				: DecodeImage(entry.Value);
			if (image is not null && image.IsCompressed())
			{
				image.Decompress();
			}

			if (image is null)
			{
				GD.PushWarning($"[bake] object lightmap {entry.Key:X16} failed to decode");
				continue;
			}

			area += (long)image.GetWidth() * image.GetHeight();
			widest = Math.Max(widest, image.GetWidth());
			decoded.Add((entry.Key, image));
		}

		if (decoded.Count == 0)
		{
			return;
		}

		// ROWS OF THE TALLEST FIRST, a row as wide as the square the lightmaps would fill. Fable 2's
		// are all one size, where this is the plain grid (ceil(sqrt(n)) columns, in key order);
		// Fable 3's run 32..1024, and a grid of 1024 cells for 337 of them is 1.5 GB.
		int rowWidth = (int)Math.Ceiling(Math.Sqrt(area) / widest) * widest;
		List<(ulong Key, Image Image)> order =
			decoded.OrderByDescending(d => d.Image.GetHeight()).ToList();

		var places = new Vector2I[order.Count];
		int penX = 0;
		int penY = 0;
		int rowHeight = 0;
		for (int i = 0; i < order.Count; i++)
		{
			int w = order[i].Image.GetWidth();
			if (penX > 0 && penX + w > rowWidth)
			{
				penX = 0;
				penY += rowHeight;
				rowHeight = 0;
			}

			places[i] = new Vector2I(penX, penY);
			penX += w;
			rowHeight = Math.Max(rowHeight, order[i].Image.GetHeight());
		}

		int atlasHeight = penY + rowHeight;
		Image.Format atlasFormat = container.SharedKeyTable ? Image.Format.Rgbe9995 : Image.Format.Rgba8;
		using var atlas = Image.CreateEmpty(rowWidth, atlasHeight, false, atlasFormat);

		float atlasW = rowWidth;
		float atlasH = atlasHeight;
		for (int i = 0; i < order.Count; i++)
		{
			Image source = order[i].Image;
			try
			{
				if (source.GetFormat() != atlasFormat)
				{
					source.Convert(atlasFormat);
				}

				int x = places[i].X;
				int y = places[i].Y;
				atlas.BlitRect(source,
					new Rect2I(0, 0, source.GetWidth(), source.GetHeight()),
					new Vector2I(x, y));

				container.ObjectAtlasRects[order[i].Key] = new Rect2(
					x / atlasW, y / atlasH,
					source.GetWidth() / atlasW, source.GetHeight() / atlasH);
			}
			finally
			{
				source.Dispose();
			}
		}

		container.ObjectAtlas = Refii.Textures.Engine_TextureUpload.CreateTexture(atlas);
		GD.Print($"[bake] object lightmap atlas: {decoded.Count} texture(s), " +
			$"{rowWidth}x{atlasHeight}");
	}

	/// <summary>
	/// The per-object ambient the guest ships for EVERY placement — the .lmp's 48-byte blocks
	/// (guest container map +0x34): three float4 vectors the runtime then converges toward
	/// placed lights. Their shape matches our probe SH-L1 uniforms exactly (one vec4 per
	/// channel), so a prop the probe FIELD cannot answer for — movable crates, rocks, shelves,
	/// 694 of them in one region — takes its authored ambient instead of flat black.
	///
	/// LAYOUT HYPOTHESIS, marked honestly: file order is taken as the R, G and B channel
	/// vectors with the guest's (constant, x, y, z) coefficient order our basis already
	/// expects. If the guest orders coefficients differently the DIRECTIONAL part tilts, but
	/// the constant term — which carries the ambient level — is position-independent either
	/// way. Verify against a lit scene; do not silently retune.
	/// </summary>
	public static bool TryGetObjectAmbient(
		LightmapContainer? container, ulong key,
		out Vector4 r, out Vector4 g, out Vector4 b)
	{
		r = default;
		g = default;
		b = default;
		if (container is null || !container.Blocks48.TryGetValue(key, out byte[]? block) ||
			block.Length < 48)
		{
			return false;
		}

		Span<float> f = stackalloc float[12];
		for (int i = 0; i < 12; i++)
		{
			f[i] = BitConverter.Int32BitsToSingle(
				System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
					block.AsSpan(i * 4)));
		}

		r = new Vector4(f[0], f[1], f[2], f[3]);
		g = new Vector4(f[4], f[5], f[6], f[7]);
		b = new Vector4(f[8], f[9], f[10], f[11]);
		return r.LengthSquared() + g.LengthSquared() + b.LengthSquared() > 0f;
	}

	/// <summary>
	/// A Fable 3 bake as the one value the shared shaders read: the light the title's lighting
	/// shaders add for it, raised to 1/<see cref="FableBakeGamma"/> so their 1.3 decode returns it.
	/// </summary>
	/// <remarks>
	/// THE TITLE'S OWN COMPOSE (landscapematerial_*_amb3 and standardmaterial_*_amb3, register
	/// names from their constant tables): slot 0 on sampler 3 (g_TextureAOSampler) is
	/// A, slot 1 on sampler 7 (g_TextureLightmapSampler) is L, both over the same UV —
	///
	///     I   = (A.b * 31 + A.r) * g_LightmapFlickeringFactor
	///     out = Dlin * pow(A.g, g_AOContrast) * (sun + 2 * W * I * L.rgb + W * ambient)
	///
	/// so the bake term is 2·I·L with A.g its occlusion; W is the darkening the shaders already
	/// apply. The flicker factor and AO contrast are per-frame constants not yet read (1 here),
	/// and the occlusion the title also lays over the sun term is carried by the bake alone.
	/// A slot the entry does not state takes the title's default: white for slot 0
	/// (sub_82182B70), black for slot 1 (sub_821FABA0). Stored as RGB9E5: the light is HDR
	/// (blue carries whole units of intensity) and four bytes a texel.
	/// </remarks>
	public static Image? ComposeFable3Bake(byte[]? intensityBlob, byte[]? colourBlob, int width, int height)
	{
		Image? Load(byte[]? blob)
		{
			if (blob is null || blob.Length == 0 || DecodeImage(blob) is not { } image)
			{
				return null;
			}

			if (image.IsCompressed())
			{
				image.Decompress();
			}

			image.Convert(Image.Format.Rgbaf);
			if (image.GetWidth() != width || image.GetHeight() != height)
			{
				image.Resize(width, height, Image.Interpolation.Bilinear);
			}

			return image;
		}

		if (width <= 0 || height <= 0)
		{
			return null;
		}

		Image? colour = Load(colourBlob);
		if (colour is null)
		{
			Image unlit = Image.CreateEmpty(width, height, false, Image.Format.Rgbe9995);
			return unlit;
		}

		Image? intensity = Load(intensityBlob);
		byte[] l = colour.GetData();
		byte[]? a = intensity?.GetData();
		var light = new byte[(long)width * height * 12];
		float inverseGamma = 1f / FableBakeGamma;
		for (long t = 0; t < (long)width * height; t++)
		{
			float ar = 1f, ag = 1f, ab = 1f;
			if (a is not null)
			{
				ar = BitConverter.ToSingle(a, (int)(t * 16));
				ag = BitConverter.ToSingle(a, (int)(t * 16) + 4);
				ab = BitConverter.ToSingle(a, (int)(t * 16) + 8);
			}

			float scale = 2f * ((ab * 31f) + ar) * Math.Clamp(ag, 0f, 1f);
			for (int c = 0; c < 3; c++)
			{
				float value = Math.Max(BitConverter.ToSingle(l, (int)(t * 16) + (c * 4)), 0f) * scale;
				BitConverter.TryWriteBytes(light.AsSpan((int)(t * 12) + (c * 4), 4), MathF.Pow(value, inverseGamma));
			}
		}

		Image composed = Image.CreateFromData(width, height, false, Image.Format.Rgbf, light);
		composed.Convert(Image.Format.Rgbe9995);
		return composed;
	}

	/// <summary>The 1.3 decode `fable_bake` and the terrain splat apply to an authored bake.</summary>
	private const float FableBakeGamma = 1.3f;

	private static Image? DecodeImage(byte[] blob)
	{
		try
		{
			return Engine_TexImage.TryCreateImage(blob, out _);
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>A Fable 3 entry's composed bake at its slot-0 size (<see cref="ComposeFable3Bake"/>).</summary>
	private static Image? ComposeObjectBake(LightmapContainer container, ulong key, byte[] intensityBlob)
	{
		if (DecodeImage(intensityBlob) is not { } size)
		{
			return null;
		}

		container.ObjectSecondLightmapBlobs.TryGetValue(key, out byte[]? colour);
		return ComposeFable3Bake(intensityBlob, colour, size.GetWidth(), size.GetHeight());
	}

	/// <summary>The object lightmap for a placement key as its own texture, or null when none.</summary>
	public static ImageTexture? ObjectLightmapTexture(LightmapContainer? container, ulong key)
	{
		if (container is null || !container.ObjectLightmapBlobs.TryGetValue(key, out byte[]? blob))
		{
			return null;
		}
		lock (container.ObjectTextures)
		{
			if (!container.ObjectTextures.TryGetValue(key, out ImageTexture? texture))
			{
				texture = container.SharedKeyTable
					? (ComposeObjectBake(container, key, blob) is { } composed ? ImageTexture.CreateFromImage(composed) : null)
					: DecodeTexture(blob);
				container.ObjectTextures[key] = texture;
			}
			return texture;
		}
	}

	/// <summary>
	/// Fable 3: the entry's slot-1 texture, the bake's colour the title binds on sampler 7
	/// (`g_TextureLightmapSampler`, sub_8221DCD0 0x8221dd20), or null when the entry states none.
	/// </summary>
	public static ImageTexture? ObjectSecondLightmapTexture(LightmapContainer? container, ulong key)
	{
		if (container is null || !container.ObjectSecondLightmapBlobs.TryGetValue(key, out byte[]? blob))
		{
			return null;
		}
		lock (container.ObjectSecondTextures)
		{
			if (!container.ObjectSecondTextures.TryGetValue(key, out ImageTexture? texture))
			{
				texture = DecodeTexture(blob);
				container.ObjectSecondTextures[key] = texture;
			}
			return texture;
		}
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

	/// <summary>
	/// The bake for one heightfield, by the id its own type-4 record states.
	/// </summary>
	/// <remarks>
	/// This used to take whichever blob was largest, which is a guess that happens to be right
	/// once per level and wrong every other time: a region ships one bake per heightfield —
	/// chapter2slums has a 576x576 for the landscape and a 32x64 for the sea vista — and
	/// "largest" hands the landscape's bake to whoever asks first and the vista's to nobody.
	///
	/// The guest never searches. It carries the id from the type-4 record into the render
	/// entity and does a single 64-bit map lookup (<c>sub_82A8EA50</c>), so we do the same.
	///
	/// FABLE 3 keeps one table (<see cref="LightmapContainer.SharedKeyTable"/>). The type-4
	/// message carries the id (sub_82CD8B48 0x82cd8e4c..0x82cd8f58) to the landscape entity's
	/// +0x110 (sub_82CAFB28 0x82cafd40), which resolves it through the placements' own streamer
	/// lookup (sub_82CB0658 → sub_82CC8DE0) and binds the entry's two slots on samplers 3 and 7
	/// (sub_82251848). The colour is slot 1; an entry without one is drawn against the title's
	/// black default (sub_821FABA0), which is no baked colour, so it serves none here.
	/// </remarks>
	public static bool TryGetTerrainLightmap(
		LightmapContainer container, ulong key, out TerrainLightmap lightmap)
	{
		if (container.TerrainLightmapBlobs.TryGetValue(key, out lightmap!))
		{
			return lightmap.Blob.Length != 0;
		}

		if (container.SharedKeyTable &&
			container.ObjectSecondLightmapBlobs.TryGetValue(key, out byte[]? colour) &&
			colour.Length != 0)
		{
			container.ObjectLightmapBlobs.TryGetValue(key, out byte[]? intensity);
			lightmap = new TerrainLightmap
			{
				Blob = colour,
				IntensityBlob = intensity ?? Array.Empty<byte>(),
			};
			return true;
		}

		lightmap = null!;
		return false;
	}

	/// <summary>
	/// Fable 3's <c>g_LandscapeLightmapUVFactor</c> (VS c14) for one heightfield.
	/// </summary>
	/// <remarks>
	/// Built per landscape entity at +0x100 by sub_82CAF990 (0x82caf9f0..0x82cafaec) from the
	/// heightfield's header — samples X at +0x48, samples Y at +0x4C, metres per sample at +0x50
	/// (sub_82CB1010) — and uploaded by sub_82251848 (0x8225193c..0x82251980), or zero when the
	/// landscape has no bake:
	///
	///     x = 0.99609375 / ((samplesX - 1) * spacing)
	///     y = 0.99609375 / ((samplesY - 1) * spacing)
	///     z = w = 1 / 512
	///
	/// The landscape vertex shader (landscapematerial_amb3) applies it as
	/// <c>uv = (world.xy - g_TexCoordScale.zw) * factor.xy + factor.zw</c>, so the bake spans the
	/// whole heightfield whatever its size, inset by the 255/256 scale and 1/512 offset. Unlike
	/// Fable 2's <see cref="LightmapUvFactor"/> it does not depend on the bake's texel count.
	/// </remarks>
	public static Vector4 Fable3LightmapUvFactor(int samplesX, int samplesY, float metresPerSample)
	{
		if (samplesX <= 1 || samplesY <= 1 || !float.IsFinite(metresPerSample) || metresPerSample <= 0f)
		{
			return Vector4.Zero;
		}

		const float Inset = 0.99609375f;
		const float Offset = 1f / 512f;
		return new Vector4(
			Inset / ((samplesX - 1) * metresPerSample),
			Inset / ((samplesY - 1) * metresPerSample),
			Offset,
			Offset);
	}

	/// <summary>
	/// The planar scale that maps a world XY onto a heightfield's lightmap.
	/// </summary>
	/// <remarks>
	/// PROVEN, and it is a pure SCALE — there is no offset anywhere in the chain. The landscape
	/// vertex shader (VSHADER_LANDSCAPEMATERIAL_BLT1_GLT1_MIST0_AMB1, recompiled out of
	/// Shaders.sbk) states the whole mapping in two lines:
	///
	///     r7.z = g_LandscapeLightmapUVFactor.x * worldA
	///     r7.w = g_LandscapeLightmapUVFactor.y * worldB
	///     oTexCoord3.xy = r7.zw
	///
	/// and worldA/worldB are the same landscape-relative position the heightfield fetch uses.
	/// The CPU builds that factor in <c>sub_82A8EA50</c>:
	///
	///     v17 = 1.0 / (dims[1] * s)      s = *(float *)(model + 0x50)
	///     v20 = 1.0 / (dims[0] * s)
	///
	/// So the bake covers <c>texels * metres-per-texel</c> metres of world and the UV is the
	/// world position over that span. Which means that when the bake's texel grid matches the
	/// heightfield's cell grid — 576 texels over 576 cells, as it does here — the mapping is
	/// exactly the normalised heightfield coordinate, and the caller can use its own.
	/// </remarks>
	public static Vector2 LightmapUvFactor(int texelsX, int texelsY, float metresPerTexel)
	{
		if (texelsX <= 0 || texelsY <= 0 || !float.IsFinite(metresPerTexel) || metresPerTexel <= 0f)
		{
			return Vector2.Zero;
		}

		return new Vector2(1f / (texelsX * metresPerTexel), 1f / (texelsY * metresPerTexel));
	}

	public static string Describe(LightmapContainer? lightmaps, LightProbeField? probes)
	{
		string lm = lightmaps is null
			? "lmp=none"
			: $"lmp=v{lightmaps.Version} terrain={lightmaps.TerrainLightmapBlobs.Count} " +
			  $"objtex={lightmaps.ObjectLightmapBlobs.Count} vbake={lightmaps.VertexBakes.Count} " +
			  $"arrays={lightmaps.U32Arrays.Count} blocks={lightmaps.Blocks48.Count}" +
			  (lightmaps.ObjectSecondLightmapBlobs.Count > 0
				  ? $" objtex2={lightmaps.ObjectSecondLightmapBlobs.Count}"
				  : string.Empty);

		string pr = probes is null
			? "probes=none"
			: probes.HasCells
			? $"probes={probes.ProbePositions.Length} cells={probes.CellPlanes.Length} " +
			  $"box={probes.CellBoundsMin}..{probes.CellBoundsMax}"
			: $"probes=dim({probes.DimX}x{probes.DimY}x{probes.DimZ}) roots={probes.RootPairKeys.Length} " +
			  $"leaves={probes.Leaves.Length / 32} bank0={probes.Banks[0].Length} " +
			  $"L0={Range(probes.Banks[0])} L1={Range(probes.Banks[1])}";

		return $"{lm} {pr}";
	}

	/// <summary>
	/// min/mean/max over a probe bank, because the SCALE of the bake is what decides how it may
	/// be combined and a count of entries cannot say it. A bake that averages 0.2 wants a tint
	/// of about five; one that averages 0.8 does not, and the two are indistinguishable in a
	/// line that only reports how many there are.
	/// </summary>
	private static string Range(Vector3[] bank)
	{
		if (bank.Length == 0)
		{
			return "none";
		}

		float min = float.MaxValue, max = float.MinValue, sum = 0f;
		foreach (Vector3 entry in bank)
		{
			foreach (float channel in new[] { entry.X, entry.Y, entry.Z })
			{
				min = MathF.Min(min, channel);
				max = MathF.Max(max, channel);
				sum += channel;
			}
		}

		return $"{min:0.###}/{sum / (bank.Length * 3):0.###}/{max:0.###}";
	}

	private static byte[]? MaybeGunzip(byte[]? data) => Refii.Core.Engine_GameGzip.MaybeGunzip(data);
}

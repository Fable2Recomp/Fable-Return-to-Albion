using System.Collections.Generic;
using System;
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

	/// <summary>Metadata key holding a node's baked probe sample as three <c>Vector4</c>.</summary>
	public const string ProbeMeta = "probe_sh";

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
			bool sampled = node.HasMeta(ProbeAtMeta) &&
				node.GetMeta(ProbeAtMeta) is { VariantType: Variant.Type.Vector3 } at &&
				at.AsVector3().DistanceSquaredTo(regionLocal) < 4f;
			if (!sampled)
			{
				StoreProbeSample(node, regionLocal, probes);
				node.SetMeta(ProbeAtMeta, regionLocal);
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

		node.SetMeta(ProbeMeta, new Godot.Collections.Array<Vector4>
		{
			sample.R, sample.G, sample.B,
		});
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

		node.SetMeta(ProbeMeta, new Godot.Collections.Array<Vector4>
		{
			new Vector4(r, 0f, 0f, 0f), new Vector4(g, 0f, 0f, 0f), new Vector4(b, 0f, 0f, 0f),
		});
	}

	private static float? _ambientScale;

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
				if (float.TryParse(OS.GetEnvironment("REFII_AMBIENT_SCALE"),
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

	private static readonly System.Collections.Generic.Dictionary<string, LightmapContainer?>
		ContainerCache = new();

	/// <summary>
	/// The level's .lmp, loaded once per gdb path and cached.
	///
	/// The streamer needs it and runs long after the build did. Rather than carry a million
	/// baked colours through the saved scene as node metadata, the scene carries the gdb path
	/// and the bakes are read back out of the bank the same way the environment theme is.
	/// </summary>
	public static LightmapContainer? ContainerFor(string gdbPath)
	{
		if (gdbPath.Length == 0)
		{
			return null;
		}

		if (ContainerCache.TryGetValue(gdbPath, out LightmapContainer? cached))
		{
			return cached;
		}

		TryLoad(gdbPath, Refii.Models.Engine_BnkMeshLoader.ReadBytes,
			out LightmapContainer? container, out _);
		ContainerCache[gdbPath] = container;
		return container;
	}

	public static void ClearContainerCache() => ContainerCache.Clear();

	/// <summary>
	/// Returns a copy of <paramref name="mesh"/> carrying the authored per-vertex bake, or null
	/// when there is no bake for this placement or it does not fit.
	///
	/// THE FIT CHECK IS THE GUEST'S OWN. It refuses a bake buffer unless its byte size equals
	/// four times the summed vertex count over every submesh, and so do we — a bake that
	/// belongs to a different build of the model would otherwise be applied vertex-for-vertex
	/// to the wrong geometry, which looks like bad lighting rather than like a mismatch.
	///
	/// The copy is unavoidable: the bake is per PLACEMENT and the streamer shares one cached
	/// mesh across every placement of a model, so a shared mesh cannot carry it.
	/// </summary>
	public static ArrayMesh? TryBuildVertexBake(LightmapContainer? bakes, ulong key, ArrayMesh mesh)
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

		var baked = new ArrayMesh();
		int cursor = 0;
		SourceSurfaces source = SurfacesOf(mesh);
		for (int s = 0; s < source.Arrays.Length; s++)
		{
			var arrays = (Godot.Collections.Array)source.Arrays[s].Duplicate(false);
			int count = source.VertexCounts[s];

			// Through the map, not by running cursor. Our vertex order is not the file's — a
			// shared vertex appears in two surfaces and everything after it shifts — so walking
			// the bake sequentially would paint the right colours onto the wrong vertices even
			// where the totals happened to agree.
			var vertexColours = new Color[count];
			for (int v = 0; v < count; v++)
			{
				int from = cursor + v < sourceVertices.Length ? sourceVertices[cursor + v] : -1;
				vertexColours[v] = from >= 0 && from < colours.Length
					? FromD3DColour(colours[from])
					: new Color(1f, 1f, 1f, 1f);
				if (from >= 0 && from < colours.Length)
				{
					Color c = vertexColours[v];
					float lum = 0.2125f * c.R + 0.7154f * c.G + 0.0721f * c.B;
					VertexBakeSamples++;
					VertexBakeSum += lum;
					VertexBakeMax = MathF.Max(VertexBakeMax, lum);
				}
			}

			arrays[(int)Mesh.ArrayType.Color] = vertexColours;
			cursor += count;

			baked.AddSurfaceFromArrays(source.Primitives[s], arrays);
			int added = baked.GetSurfaceCount() - 1;
			baked.SurfaceSetName(added, source.Names[s]);
			baked.SurfaceSetMaterial(added, source.Materials[s]);
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
		};

		for (int s = 0; s < surfaces; s++)
		{
			Godot.Collections.Array arrays = mesh.SurfaceGetArrays(s);
			read.Arrays[s] = arrays;
			read.VertexCounts[s] = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
			read.Primitives[s] = mesh.SurfaceGetPrimitiveType(s);
			read.Names[s] = mesh.SurfaceGetName(s);
			read.Materials[s] = mesh.SurfaceGetMaterial(s);
		}

		lock (SurfaceCache)
		{
			return SurfaceCache.TryGetValue(mesh, out SourceSurfaces? raced) ? raced : Add(mesh, read);
		}

		static SourceSurfaces Add(ArrayMesh mesh, SourceSurfaces read)
		{
			SurfaceCache.Add(mesh, read);
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
	public static double VertexBakeSum;
	public static float VertexBakeMax;

	public static bool ApplyMultiMeshProbes(
		MultiMesh multiMesh,
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
				multiMesh.SetInstanceColor(i, new Color(
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

			// THE SPARSE-FIELD FALLBACK, per instance: the guest's per-object ambient block
			// (the .lmp 48-byte payload) answers where the probe field has no cell, looked up
			// by the instance's own bake key.
			if (sample.R.LengthSquared() + sample.G.LengthSquared() +
				sample.B.LengthSquared() <= 0f &&
				bakes is not null && bakeKeys is not null && i < bakeKeys.Count &&
				TryGetObjectAmbient(bakes, bakeKeys[i],
					out Vector4 ar, out Vector4 ag, out Vector4 ab))
			{
				sample = new ProbeSample { R = ar, G = ag, B = ab };
			}

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

			multiMesh.SetInstanceColor(i, colour);
		}

		return any;
	}

	/// <summary>
	/// Writes one instance's lighting into its shader parameters: the baked probe sample it is
	/// carrying, and the theme's ambient and lightmap tint for the moment.
	///
	/// Called when a mesh arrives and again whenever time of day moves, so the two halves stay
	/// together. Nothing here re-samples the probe field — the bake does not move.
	/// </summary>
	public static void ApplyInstanceLighting(GeometryInstance3D node, LightingFrame? frame)
	{
		// HasMeta FIRST, AND IT IS NOT BELT-AND-BRACES. `GetMeta(name, default)` reads as a safe
		// lookup with a fallback and is not one: Godot returns the fallback only when the fallback
		// is itself non-nil, and `default` for a Variant is nil. Every absent key otherwise takes
		// the ERR_FAIL path, printing an error plus a full managed backtrace on the main thread —
		// once per instance, every time time of day moves.
		bool probeValid = false;
		if (node.HasMeta(ProbeMeta) &&
			node.GetMeta(ProbeMeta) is { VariantType: Variant.Type.Array } probeMeta)
		{
			var sample = probeMeta.AsGodotArray<Vector4>();
			if (sample.Count == 3)
			{
				probeValid = true;
				// The remaster's ambient scale rides the L0 band only: scaling the directional
				// L1 bands too would exaggerate the field's tilt, not its level.
				float probeScale = AmbientScale;
				node.SetInstanceShaderParameter("probe_r",
					new Vector4(sample[0].X * probeScale, sample[0].Y, sample[0].Z, sample[0].W));
				node.SetInstanceShaderParameter("probe_g",
					new Vector4(sample[1].X * probeScale, sample[1].Y, sample[1].Z, sample[1].W));
				node.SetInstanceShaderParameter("probe_b",
					new Vector4(sample[2].X * probeScale, sample[2].Y, sample[2].Z, sample[2].W));
			}
		}

		// The node's stated mode wins; a probe sample is only the fallback answer.
		float mode = node.HasMeta(BakeModeMeta) &&
			node.GetMeta(BakeModeMeta) is { VariantType: Variant.Type.Int } stored
				? stored.AsInt32()
				: (probeValid ? 1f : 0f);

		float[] lightmap = frame?.LightmapColour ?? new float[] { 1f, 1f, 1f };
		node.SetInstanceShaderParameter(
			"lightmap_colour",
			new Vector4(lightmap[0], lightmap[1], lightmap[2], mode));

		float ar = 0f, ag = 0f, ab = 0f;
		if (frame is not null && frame.HasAmbient)
		{
			float ambientScale = AmbientScale;
			ar = frame.AmbientColour[0] * frame.AmbientEnergy * ambientScale;
			ag = frame.AmbientColour[1] * frame.AmbientEnergy * ambientScale;
			ab = frame.AmbientColour[2] * frame.AmbientEnergy * ambientScale;
		}

		node.SetInstanceShaderParameter("ambient_colour", new Vector4(ar, ag, ab, 1f));

		// White when the theme states none, because this colour is the FLOOR of a lerp toward
		// 1 rather than a tint — black here would drive every normal-mapped surface's ambient
		// to nothing.
		float[] darkening = frame?.DarkeningColour ?? new float[] { 1f, 1f, 1f };
		node.SetInstanceShaderParameter(
			"darkening_colour", new Vector4(darkening[0], darkening[1], darkening[2], 1f));

		// THE MAIN LIGHT, for the surfaces that cannot take it through Godot's `light()`.
		//
		// Water is `unshaded` — it composes reflection, refraction and a sun glint itself, so no
		// DirectionalLight3D ever reaches it and it needs the light stated. It was lit by a
		// direction hardcoded in Engine_WaterMaterial instead, which meant the glint sat in the
		// same corner of every lake at dawn, noon and dusk.
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
		float[] sunDir = frame?.SunDirection ?? new float[] { 0f, 1f, 0f };
		node.SetInstanceShaderParameter(
			"sun_direction", new Vector4(-sunDir[0], sunDir[1], sunDir[2], 0f));

		float[] sunRgb = frame?.SunColour ?? new float[] { 1f, 1f, 1f };
		float sunEnergy = frame?.SunEnergy ?? 1f;
		node.SetInstanceShaderParameter(
			"sun_colour",
			new Vector4(sunRgb[0] * sunEnergy, sunRgb[1] * sunEnergy, sunRgb[2] * sunEnergy, 1f));
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
	public static void RefreshInstanceLighting(Node root, LightingFrame frame)
	{
		foreach (Node node in Descendants(root))
		{
			if (node is GeometryInstance3D instance)
			{
				ApplyInstanceLighting(instance, frame);
			}
		}
	}

	private static System.Collections.Generic.IEnumerable<Node> Descendants(Node root)
	{
		foreach (Node child in root.GetChildren())
		{
			yield return child;
			foreach (Node grandchild in Descendants(child))
			{
				yield return grandchild;
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
		int cellW = 1;
		int cellH = 1;
		foreach (KeyValuePair<ulong, byte[]> entry in container.ObjectLightmapBlobs)
		{
			ImageTexture? texture = DecodeTexture(entry.Value);
			Image? image = texture?.GetImage();
			if (image is null)
			{
				GD.PushWarning($"[bake] object lightmap {entry.Key:X16} failed to decode");
				continue;
			}

			cellW = Math.Max(cellW, image.GetWidth());
			cellH = Math.Max(cellH, image.GetHeight());
			decoded.Add((entry.Key, image));
		}

		if (decoded.Count == 0)
		{
			return;
		}

		int columns = (int)Math.Ceiling(Math.Sqrt(decoded.Count));
		int rows = (decoded.Count + columns - 1) / columns;
		var atlas = Image.CreateEmpty(
			columns * cellW, rows * cellH, false, Image.Format.Rgba8);

		float atlasW = columns * cellW;
		float atlasH = rows * cellH;
		for (int i = 0; i < decoded.Count; i++)
		{
			Image source = decoded[i].Image;
			if (source.GetFormat() != Image.Format.Rgba8)
			{
				source.Convert(Image.Format.Rgba8);
			}

			int x = (i % columns) * cellW;
			int y = (i / columns) * cellH;
			atlas.BlitRect(source,
				new Rect2I(0, 0, source.GetWidth(), source.GetHeight()),
				new Vector2I(x, y));

			container.ObjectAtlasRects[decoded[i].Key] = new Rect2(
				x / atlasW, y / atlasH,
				source.GetWidth() / atlasW, source.GetHeight() / atlasH);
		}

		container.ObjectAtlas = ImageTexture.CreateFromImage(atlas);
		GD.Print($"[bake] object lightmap atlas: {decoded.Count} texture(s), " +
			$"{columns * cellW}x{rows * cellH}");
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
	/// </remarks>
	public static bool TryGetTerrainLightmap(
		LightmapContainer container, ulong key, out TerrainLightmap lightmap)
	{
		return container.TerrainLightmapBlobs.TryGetValue(key, out lightmap!) &&
			lightmap.Blob.Length != 0;
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
			  $"arrays={lightmaps.U32Arrays.Count} blocks={lightmaps.Blocks48.Count}";

		string pr = probes is null
			? "probes=none"
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

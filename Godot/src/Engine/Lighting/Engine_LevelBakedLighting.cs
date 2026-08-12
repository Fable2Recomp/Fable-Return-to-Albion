using System;
using System.IO;
using System.IO.Compression;
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
		for (int s = 0; s < mesh.GetSurfaceCount(); s++)
		{
			Godot.Collections.Array arrays = mesh.SurfaceGetArrays(s);
			int count = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;

			// Through the map, not by running cursor. Our vertex order is not the file's — a
			// shared vertex appears in two surfaces and everything after it shifts — so walking
			// the bake sequentially would paint the right colours onto the wrong vertices even
			// where the totals happened to agree.
			var vertexColours = new Color[count];
			for (int v = 0; v < count; v++)
			{
				int source = cursor + v < sourceVertices.Length ? sourceVertices[cursor + v] : -1;
				vertexColours[v] = source >= 0 && source < colours.Length
					? FromD3DColour(colours[source])
					: new Color(1f, 1f, 1f, 1f);
			}

			arrays[(int)Mesh.ArrayType.Color] = vertexColours;
			cursor += count;

			baked.AddSurfaceFromArrays(mesh.SurfaceGetPrimitiveType(s), arrays);
			int added = baked.GetSurfaceCount() - 1;
			baked.SurfaceSetName(added, mesh.SurfaceGetName(s));
			baked.SurfaceSetMaterial(added, mesh.SurfaceGetMaterial(s));
		}

		return baked;
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
	public static bool ApplyMultiMeshProbes(
		MultiMesh multiMesh,
		System.Collections.Generic.IReadOnlyList<Transform3D> instances,
		LightProbeField? probes)
	{
		if (probes is null || !probes.HasData)
		{
			return false;
		}

		bool any = false;
		for (int i = 0; i < instances.Count; i++)
		{
			Vector3 godotPosition = instances[i].Origin;
			var gamePosition = new Vector3(godotPosition.X, godotPosition.Z, godotPosition.Y);
			ProbeSample sample = probes.Sample(gamePosition);

			// The flat band only, SCALED BY THE BAND-0 CONSTANT. The three that follow it are
			// directional and there is nowhere on a MultiMesh instance to put them.
			//
			// The constant matters and is easy to lose here: the probe path evaluates the SH
			// through D3DXSHEvalDirection's basis, whose first term is 0.2820948, so a raw L0
			// stored straight into an instance colour is 3.5x hotter than the same bake taken
			// the other way. With the theme's factor of ~3.5 on top of it, every tree in the
			// region came out white and the grass turned teal.
			var colour = new Color(
				sample.R.X * ShBand0, sample.G.X * ShBand0, sample.B.X * ShBand0, 1f);
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
				node.SetInstanceShaderParameter("probe_r", sample[0]);
				node.SetInstanceShaderParameter("probe_g", sample[1]);
				node.SetInstanceShaderParameter("probe_b", sample[2]);
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
			ar = frame.AmbientColour[0] * frame.AmbientEnergy;
			ag = frame.AmbientColour[1] * frame.AmbientEnergy;
			ab = frame.AmbientColour[2] * frame.AmbientEnergy;
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

	private static byte[]? MaybeGunzip(byte[]? data)
	{
		if (data is null || data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B)
		{
			return data;
		}

		try
		{
			using var input = new MemoryStream(data);
			using var gzip = new GZipStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			gzip.CopyTo(output);
			return output.ToArray();
		}
		catch (Exception)
		{
			return data;
		}
	}
}

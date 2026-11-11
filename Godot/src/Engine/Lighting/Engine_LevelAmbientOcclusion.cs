using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using Refii.Level;
using Refii.Models;

namespace Refii.Lighting;

/// <summary>
/// Deserialized static ambient occlusion container for one level scenario:
/// per-placement baked world AO factors and 2D world-space terrain contact AO map (<c>fable_terrain_contact_ao</c>).
/// </summary>
public sealed class StaticAoContainer
{
	/// <summary>Maps 64-bit deterministic placement key to base vertex index in <see cref="PropAoBytes"/>.</summary>
	public required Dictionary<ulong, int> PlacementOffsets { get; init; }

	/// <summary>Maps 64-bit deterministic placement key to normalized placement world AO factor (<c>[0.05, 1.0]</c>).</summary>
	public Dictionary<ulong, float> PlacementAoFactors { get; set; } = new();

	/// <summary>UNORM8 (0..255) baked ambient occlusion per canonical model source vertex across all static prop placements.</summary>
	public required byte[] PropAoBytes { get; set; }

	/// <summary>Width of <see cref="TerrainAoBytes"/> in texels (0 if no heightfield).</summary>
	public int TerrainWidth { get; init; }

	/// <summary>Height of <see cref="TerrainAoBytes"/> in texels (0 if no heightfield).</summary>
	public int TerrainHeight { get; init; }

	/// <summary>(OriginX, OriginZ, InvSpanX, InvSpanZ) mapping local/world XZ to terrain contact AO UVs.</summary>
	public Vector4 TerrainWorldRect { get; init; }

	/// <summary>Minimum world-space Y height of the sampled terrain heightfield.</summary>
	public float TerrainMinHeight { get; init; }

	/// <summary>Maximum world-space Y height of the sampled terrain heightfield.</summary>
	public float TerrainMaxHeight { get; init; }

	/// <summary>RG8 (2 bytes per texel: R = UNORM8 contact AO, G = UNORM8 normalized terrain height) row-major (TerrainWidth * TerrainHeight * 2).</summary>
	public required byte[] TerrainAoBytes { get; set; }

	public ImageTexture? RuntimeTerrainTexture { get; set; }
}

/// <summary>
/// Offline static inter-object &amp; terrain-contact ambient occlusion baker, binary <c>.ao</c>
/// serializer, and runtime global shader binder (<c>fable_terrain_contact_ao</c>).
/// </summary>
public static class Engine_LevelAmbientOcclusion
{
	public const uint Magic = 0x4F414652u; // "RFAO" little-endian
	public const uint Version = 2u;
	public const int PropAtlasRowWidth = 2048;
	public const int MaxPropAtlasRows = 8192;
	public const int MaxPropAtlasVertices = PropAtlasRowWidth * MaxPropAtlasRows;
	public const int MaxVerticesPerPlacement = 32768;
	public const float PropAoMaxRadius = 2.5f;
	public const float TerrainAoMaxRadius = 2.6f;
	public const float MinBakedAo = 0.05f;
	public const float MinTerrainContactAo = 0.25f;

	private static readonly Vector3[] HemisphereKernel =
		Engine_ModelSelfAo.BuildCosineFibonacciHemisphere(Engine_ModelSelfAo.RayCount);

	private static bool? _bakedAoEnabled;
	private static float? _bakedAoStrength;
	private static float? _aoLightAffect;
	private static int? _terrainAoResolution;

	private static ImageTexture? _fallbackTerrainTexture;
	private static ImageTexture? _boundTerrainTexture;

	private static StringName? _paramTerrainContactAo;
	private static StringName? _paramTerrainAoRect;
	private static StringName? _paramTerrainAoHeight;
	private static StringName? _paramAoStrength;
	private static StringName? _paramAoLightAffect;

	private static StringName ParamTerrainContactAo => _paramTerrainContactAo ??= new StringName("fable_terrain_contact_ao");
	private static StringName ParamTerrainAoRect => _paramTerrainAoRect ??= new StringName("fable_terrain_ao_rect");
	private static StringName ParamTerrainAoHeight => _paramTerrainAoHeight ??= new StringName("fable_terrain_ao_height");
	private static StringName ParamAoStrength => _paramAoStrength ??= new StringName("fable_ao_strength");
	private static StringName ParamAoLightAffect => _paramAoLightAffect ??= new StringName("fable_ao_light_affect");

	public static StaticAoContainer? Current { get; private set; }

	public static string CacheAoDirectory =>
		Path.Combine(Refii.Core.Engine_Title.Active.CacheDirectory, "ao");

	/// <summary>Whether pre-baked ambient occlusion is enabled (<c>baked_ao_enabled</c> CVar, default true).</summary>
	public static bool BakedAoEnabled
	{
		get
		{
			if (_bakedAoEnabled is null)
			{
				try
				{
					_bakedAoEnabled = Refii.Core.Engine_RefiiConfig.Bool(
						Refii.Core.Engine_RefiiConfig.Read(out _),
						"baked_ao_enabled",
						true);
				}
				catch (Exception)
				{
					_bakedAoEnabled = true;
				}
			}

			return _bakedAoEnabled.Value;
		}
	}

	/// <summary>Strength/exponent dial for baked AO in Modern mode (<c>baked_ao_strength</c> CVar, default 1.0).</summary>
	public static float BakedAoStrength
	{
		get
		{
			if (_bakedAoStrength is null)
			{
				try
				{
					float val = Refii.Core.Engine_RefiiConfig.Float(
						Refii.Core.Engine_RefiiConfig.Read(out _),
						"baked_ao_strength",
						1.0f);
					_bakedAoStrength = Math.Clamp(val, 0.0f, 2.0f);
				}
				catch (Exception)
				{
					_bakedAoStrength = 1.0f;
				}
			}

			return _bakedAoStrength.Value;
		}
	}

	/// <summary>Direct light attenuation in deep crevices (<c>ao_light_affect</c> CVar, default 0.25).</summary>
	public static float AoLightAffect
	{
		get
		{
			if (_aoLightAffect is null)
			{
				try
				{
					float val = Refii.Core.Engine_RefiiConfig.Float(
						Refii.Core.Engine_RefiiConfig.Read(out _),
						"ao_light_affect",
						0.25f);
					_aoLightAffect = Math.Clamp(val, 0.0f, 1.0f);
				}
				catch (Exception)
				{
					_aoLightAffect = 0.25f;
				}
			}

			return _aoLightAffect.Value;
		}
	}

	/// <summary>Alias for <see cref="BakedAoStrength"/> used by <see cref="Engine_LevelLighting"/>.</summary>
	public static float BakedAoScale => BakedAoStrength;

	/// <summary>
	/// Resolution of the cropped 2D ground contact AO map (<c>terrain_ao_resolution</c> CVar:
	/// 2048 Performance / Android, 4096 Balanced, 8192 Quality).
	/// </summary>
	public static int TerrainAoResolution
	{
		get
		{
			if (_terrainAoResolution is null)
			{
				int defaultRes = Refii.AndroidRuntime.AndroidBakedShadows.KeepsOneBake ? 2048 : 4096;
				try
				{
					int raw = Refii.Core.Engine_RefiiConfig.Int(
						Refii.Core.Engine_RefiiConfig.Read(out _),
						"terrain_ao_resolution",
						defaultRes);
					_terrainAoResolution = raw <= 2048 ? 2048 : (raw <= 4096 ? 4096 : 8192);
				}
				catch (Exception)
				{
					_terrainAoResolution = defaultRes;
				}
			}

			return _terrainAoResolution.Value;
		}
		set => _terrainAoResolution = value <= 2048 ? 2048 : (value <= 4096 ? 4096 : 8192);
	}

	public static void ReloadConfig()
	{
		_bakedAoEnabled = null;
		_bakedAoStrength = null;
		_aoLightAffect = null;
		_terrainAoResolution = null;
		PublishAoParameters();
	}

	/// <summary>
	/// Publishes the global shader uniforms <c>fable_ao_strength</c> and <c>fable_ao_light_affect</c>
	/// according to the active CVars and lighting mode.
	/// </summary>
	public static void PublishAoParameters()
	{
		if (Refii.Core.Engine_AppRoot.IsTestHost)
		{
			return;
		}

		if (OS.GetThreadCallerId() == OS.GetMainThreadId())
		{
			PublishAoParametersOnMainThread();
		}
		else
		{
			Callable.From(PublishAoParametersOnMainThread).CallDeferred();
		}
	}

	private static void PublishAoParametersOnMainThread()
	{
		bool active = BakedAoEnabled && Engine_LightingFrame.ModernModel;
		float strength = active ? BakedAoStrength : 0.0f;
		float lightAffect = active ? AoLightAffect : 0.0f;
		RenderingServer.GlobalShaderParameterSet(ParamAoStrength, strength);
		RenderingServer.GlobalShaderParameterSet(ParamAoLightAffect, lightAffect);
	}

	/// <summary>
	/// Computes the deterministic 64-bit placement key from a prop's normalized model path and
	/// region-local <see cref="Transform3D"/> (quantized to 1 cm / 0.01 basis units).
	/// </summary>
	public static ulong ComputePlacementAoKey(string modelPath, in Transform3D transform)
	{
		const ulong fnvOffset = 14695981039346656037UL;
		const ulong fnvPrime = 1099511628211UL;

		ulong hash = fnvOffset;
		if (!string.IsNullOrEmpty(modelPath))
		{
			ReadOnlySpan<char> span = modelPath.AsSpan().Trim();
			for (int i = 0; i < span.Length; i++)
			{
				char c = span[i];
				if (c == '\\')
				{
					c = '/';
				}
				else if ((uint)(c - 'A') <= ('Z' - 'A'))
				{
					c = (char)(c + 32);
				}

				hash ^= c;
				hash *= fnvPrime;
			}
		}

		MixQuantizedFloat(ref hash, transform.Origin.X);
		MixQuantizedFloat(ref hash, transform.Origin.Y);
		MixQuantizedFloat(ref hash, transform.Origin.Z);
		MixQuantizedFloat(ref hash, transform.Basis.Row0.X);
		MixQuantizedFloat(ref hash, transform.Basis.Row0.Y);
		MixQuantizedFloat(ref hash, transform.Basis.Row0.Z);
		MixQuantizedFloat(ref hash, transform.Basis.Row1.X);
		MixQuantizedFloat(ref hash, transform.Basis.Row1.Y);
		MixQuantizedFloat(ref hash, transform.Basis.Row1.Z);
		MixQuantizedFloat(ref hash, transform.Basis.Row2.X);
		MixQuantizedFloat(ref hash, transform.Basis.Row2.Y);
		MixQuantizedFloat(ref hash, transform.Basis.Row2.Z);

		return hash == 0UL ? 1UL : hash;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void MixQuantizedFloat(ref ulong hash, float value)
	{
		const ulong fnvPrime = 1099511628211UL;
		int q = float.IsFinite(value) ? (int)MathF.Round(value * 100.0f) : 0;
		uint u = unchecked((uint)q);
		hash ^= u & 0xFFu;
		hash *= fnvPrime;
		hash ^= (u >> 8) & 0xFFu;
		hash *= fnvPrime;
		hash ^= (u >> 16) & 0xFFu;
		hash *= fnvPrime;
		hash ^= (u >> 24) & 0xFFu;
		hash *= fnvPrime;
	}

	/// <summary>
	/// Looks up the baked vertex offset in <see cref="StaticAoContainer.PlacementOffsets"/> for a static prop placement.
	/// </summary>
	public static bool TryGetPlacementOffset(string modelPath, in Transform3D transform, out int vertexOffset)
	{
		vertexOffset = -1;
		if (Current is not { } container || !BakedAoEnabled)
		{
			return false;
		}

		ulong key = ComputePlacementAoKey(modelPath, in transform);
		return container.PlacementOffsets.TryGetValue(key, out vertexOffset) && vertexOffset >= 0;
	}

	/// <summary>
	/// Looks up the baked world AO factor (<c>[0.05, 1.0]</c>) for a static prop placement.
	/// </summary>
	public static bool TryGetPlacementAoFactor(string modelPath, in Transform3D transform, out float aoFactor)
	{
		aoFactor = 1.0f;
		if (Current is not { } container || !BakedAoEnabled)
		{
			return false;
		}

		ulong key = ComputePlacementAoKey(modelPath, in transform);
		if (container.PlacementAoFactors.TryGetValue(key, out float factor))
		{
			aoFactor = Math.Clamp(factor, MinBakedAo, 1.0f);
			return true;
		}

		return false;
	}

	/// <summary>
	/// Computes a normalized per-placement world AO factor (<c>[0.05, 1.0]</c>) for each placement
	/// from its contiguous vertex slice in <paramref name="propAoBytes"/>.
	/// </summary>
	public static Dictionary<ulong, float> ComputePlacementAoFactors(
		IReadOnlyDictionary<ulong, int> placementOffsets,
		ReadOnlySpan<byte> propAoBytes)
	{
		var factors = new Dictionary<ulong, float>(placementOffsets.Count);
		if (placementOffsets.Count == 0 || propAoBytes.IsEmpty)
		{
			return factors;
		}

		var sorted = new List<KeyValuePair<ulong, int>>(placementOffsets.Count);
		foreach (KeyValuePair<ulong, int> kv in placementOffsets)
		{
			if (kv.Value >= 0 && kv.Value < propAoBytes.Length)
			{
				sorted.Add(kv);
			}
		}

		sorted.Sort((a, b) => a.Value.CompareTo(b.Value));
		const float inv255 = 1.0f / 255.0f;

		for (int i = 0; i < sorted.Count; i++)
		{
			ulong key = sorted[i].Key;
			int start = sorted[i].Value;
			int end = i + 1 < sorted.Count
				? Math.Min(sorted[i + 1].Value, propAoBytes.Length)
				: propAoBytes.Length;
			int count = end - start;
			if (count <= 0)
			{
				factors[key] = 1.0f;
				continue;
			}

			long sum = 0;
			for (int v = start; v < end; v++)
			{
				sum += propAoBytes[v];
			}

			float meanAo = (sum * inv255) / count;
			// Normalize out the ~0.88 baseline self-occlusion already carried in vertex COLOR.a.
			factors[key] = Math.Clamp(meanAo / 0.88f, MinBakedAo, 1.0f);
		}

		return factors;
	}

	/// <summary>
	/// Returns the relative <c>.ao</c> path for a level and scenario GDB path (e.g. <c>albion/bowerstone_market/day.ao</c>).
	/// </summary>
	public static string GetScenarioAoRelativePath(LevelEntry level, string gdbPath)
	{
		string shadowRel = Engine_LevelShadowMaps.GetShadowMapRelativePath(level, gdbPath);
		return Path.ChangeExtension(shadowRel, ".ao");
	}

	/// <summary>
	/// Returns the full disk path inside <c>&lt;app&gt;/cache/&lt;Title&gt;/ao/</c> for a level scenario.
	/// </summary>
	public static string GetScenarioAoDiskPath(LevelEntry level, string gdbPath) =>
		Path.Combine(CacheAoDirectory, GetScenarioAoRelativePath(level, gdbPath));

	/// <summary>
	/// Serializes a <see cref="StaticAoContainer"/> into the binary <c>.ao</c> format.
	/// </summary>
	public static byte[] Serialize(StaticAoContainer container)
	{
		int placementCount = container.PlacementOffsets.Count;
		int propVertCount = container.PropAoBytes.Length;
		int terrainW = Math.Max(0, container.TerrainWidth);
		int terrainH = Math.Max(0, container.TerrainHeight);
		int terrainByteCount = terrainW * terrainH * 2;

		const int headerBytes = 48;
		int dirBytes = placementCount * 12;
		int totalBytes = headerBytes + dirBytes + propVertCount + terrainByteCount;

		var bytes = new byte[totalBytes];
		Span<byte> span = bytes.AsSpan();

		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), Magic);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), Version);
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8, 4), placementCount);
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12, 4), propVertCount);
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), terrainW);
		BinaryPrimitives.WriteInt32LittleEndian(span.Slice(20, 4), terrainH);
		BinaryPrimitives.WriteSingleLittleEndian(span.Slice(24, 4), container.TerrainWorldRect.X);
		BinaryPrimitives.WriteSingleLittleEndian(span.Slice(28, 4), container.TerrainWorldRect.Y);
		BinaryPrimitives.WriteSingleLittleEndian(span.Slice(32, 4), container.TerrainWorldRect.Z);
		BinaryPrimitives.WriteSingleLittleEndian(span.Slice(36, 4), container.TerrainWorldRect.W);
		BinaryPrimitives.WriteSingleLittleEndian(span.Slice(40, 4), container.TerrainMinHeight);
		BinaryPrimitives.WriteSingleLittleEndian(span.Slice(44, 4), container.TerrainMaxHeight);

		var sorted = new List<KeyValuePair<ulong, int>>(container.PlacementOffsets);
		sorted.Sort((a, b) => a.Key.CompareTo(b.Key));

		int cursor = headerBytes;
		for (int i = 0; i < sorted.Count; i++)
		{
			KeyValuePair<ulong, int> kv = sorted[i];
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(cursor, 8), kv.Key);
			BinaryPrimitives.WriteInt32LittleEndian(span.Slice(cursor + 8, 4), kv.Value);
			cursor += 12;
		}

		if (propVertCount > 0)
		{
			container.PropAoBytes.AsSpan(0, propVertCount).CopyTo(span.Slice(cursor, propVertCount));
			cursor += propVertCount;
		}

		if (terrainByteCount > 0 && container.TerrainAoBytes.Length >= terrainByteCount)
		{
			container.TerrainAoBytes.AsSpan(0, terrainByteCount).CopyTo(span.Slice(cursor, terrainByteCount));
		}
		else if (terrainByteCount > 0 && container.TerrainAoBytes.Length == terrainW * terrainH)
		{
			// Backwards-compatible packing if a test constructs a 1-byte-per-texel array
			Span<byte> dst = span.Slice(cursor, terrainByteCount);
			for (int i = 0; i < terrainW * terrainH; i++)
			{
				dst[i * 2] = container.TerrainAoBytes[i];
				dst[i * 2 + 1] = 0;
			}
		}

		return bytes;
	}

	/// <summary>
	/// Deserializes a binary <c>.ao</c> buffer into a <see cref="StaticAoContainer"/>.
	/// </summary>
	public static bool TryDeserialize(ReadOnlySpan<byte> bytes, out StaticAoContainer? container)
	{
		container = null;
		const int headerBytes = 48;
		if (bytes.Length < headerBytes)
		{
			return false;
		}

		uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0, 4));
		uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
		if (magic != Magic || version != Version)
		{
			return false;
		}

		int placementCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4));
		int propVertCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12, 4));
		int terrainW = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16, 4));
		int terrainH = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(20, 4));
		if (placementCount < 0 || placementCount > 500_000 ||
		    propVertCount < 0 || propVertCount > MaxPropAtlasVertices ||
		    terrainW < 0 || terrainW > 8192 ||
		    terrainH < 0 || terrainH > 8192)
		{
			return false;
		}

		long dirBytes = (long)placementCount * 12L;
		long terrainByteCount = (long)terrainW * terrainH * 2L;
		long expectedTotal = headerBytes + dirBytes + propVertCount + terrainByteCount;
		if (bytes.Length < expectedTotal)
		{
			return false;
		}

		Vector4 terrainRect = new(
			BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(24, 4)),
			BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(28, 4)),
			BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(32, 4)),
			BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(36, 4)));
		float terrainMinH = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(40, 4));
		float terrainMaxH = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(44, 4));

		var offsets = new Dictionary<ulong, int>(placementCount);
		int cursor = headerBytes;
		for (int i = 0; i < placementCount; i++)
		{
			ulong key = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(cursor, 8));
			int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(cursor + 8, 4));
			offsets[key] = offset;
			cursor += 12;
		}

		var propBytes = propVertCount > 0
			? bytes.Slice(cursor, propVertCount).ToArray()
			: Array.Empty<byte>();
		cursor += propVertCount;

		var terrainBytes = terrainByteCount > 0
			? bytes.Slice(cursor, (int)terrainByteCount).ToArray()
			: Array.Empty<byte>();

		container = new StaticAoContainer
		{
			PlacementOffsets = offsets,
			PlacementAoFactors = ComputePlacementAoFactors(offsets, propBytes),
			PropAoBytes = propBytes,
			TerrainWidth = terrainW,
			TerrainHeight = terrainH,
			TerrainWorldRect = terrainRect,
			TerrainMinHeight = terrainMinH,
			TerrainMaxHeight = terrainMaxH,
			TerrainAoBytes = terrainBytes,
		};
		return true;
	}

	/// <summary>
	/// Fast header-only check verifying whether an existing <c>.ao</c> file on disk has valid magic,
	/// version, and expected file size without reading the payload into memory.
	/// </summary>
	public static bool IsFileValid(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return false;
			}

			using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
			if (fs.Length < 48)
			{
				return false;
			}

			Span<byte> header = stackalloc byte[48];
			if (fs.Read(header) != 48)
			{
				return false;
			}

			uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4));
			uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
			if (magic != Magic || version != Version)
			{
				return false;
			}

			int placementCount = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
			int propVertCount = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));
			int terrainW = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
			int terrainH = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(20, 4));
			if (placementCount < 0 || propVertCount < 0 || terrainW < 0 || terrainH < 0)
			{
				return false;
			}

			// Invalidate legacy low-res (257/513/1024) or whole-prop-darkened .ao files and re-bake when
			// the user changes the ground AO resolution preset (2048 / 4096 / 8192).
			if ((terrainW >= 128 && terrainW != TerrainAoResolution) ||
			    (placementCount > 16 && propVertCount > 0))
			{
				return false;
			}

			long expected = 48L + (long)placementCount * 12L + propVertCount + (long)terrainW * terrainH * 2L;
			return fs.Length >= expected;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static void ReleasePreviousBoundTerrainTexture(ImageTexture? replacement)
	{
		// Do not call Dispose() immediately on the previous ImageTexture while D3D12 command lists
		// from the separate render thread may still reference its RID; let RefCounted release it.
		_boundTerrainTexture = replacement;
	}

	/// <summary>
	/// Ensures fallback global shader parameters (<c>fable_terrain_contact_ao</c>, <c>fable_terrain_ao_rect</c>,
	/// <c>fable_ao_strength</c>, <c>fable_ao_light_affect</c>) are bound on the main thread before shader warmup.
	/// </summary>
	public static void EnsureFallbackTextureBound()
	{
		if (Refii.Core.Engine_AppRoot.IsTestHost || OS.GetThreadCallerId() != OS.GetMainThreadId())
		{
			return;
		}

		PublishAoParametersOnMainThread();
		if (_boundTerrainTexture is null)
		{
			RenderingServer.GlobalShaderParameterSet(ParamTerrainContactAo, EnsureFallbackTerrainTexture());
			RenderingServer.GlobalShaderParameterSet(ParamTerrainAoRect, Vector4.Zero);
			RenderingServer.GlobalShaderParameterSet(ParamTerrainAoHeight, new Vector4(0.0f, 1.0f, 0.0f, 0.0f));
		}
	}

	private static readonly object ActiveScenarioGate = new();
	private static string _activeScenarioKey = string.Empty;

	/// <summary>
	/// Loads the pre-baked <c>.ao</c> container for <paramref name="level"/> and <paramref name="gdbPath"/>
	/// into <see cref="Current"/> and binds <c>fable_terrain_contact_ao</c>.
	/// Returns <c>true</c> when no background bake is needed (disabled or an up-to-date cache hit was bound).
	/// </summary>
	public static bool LoadOrClear(LevelEntry level, string gdbPath)
	{
		if (!BakedAoEnabled || !Engine_LightingFrame.ModernModel)
		{
			Clear();
			return true;
		}

		string diskPath = GetScenarioAoDiskPath(level, gdbPath);
		bool upToDate = IsFileValid(diskPath);
		lock (ActiveScenarioGate)
		{
			_activeScenarioKey = diskPath;
		}

		if (!File.Exists(diskPath))
		{
			Clear(preserveActiveScenario: true);
			if (!Refii.Core.Engine_AppRoot.IsTestHost)
			{
				GD.Print($"[ao] {level}: no baked static AO at {diskPath} (queuing background GPU bake)");
			}
			return false;
		}

		try
		{
			byte[] bytes = File.ReadAllBytes(diskPath);
			if (!TryDeserialize(bytes, out StaticAoContainer? container) || container is null ||
			    (container.PlacementOffsets.Count > 16 && container.PropAoBytes.Length > 0))
			{
				Clear(preserveActiveScenario: true);
				GD.Print($"[ao] {level}: stale or unreadable AO at {diskPath} (queuing background GPU bake)");
				return false;
			}

			int propBytes = container.PropAoBytes.Length;
			UploadAndBind(container);
			GD.Print(
				$"[ao] {level}: loaded static AO ({container.PlacementOffsets.Count} placements, " +
				$"{propBytes:N0} prop verts, terrain {container.TerrainWidth}x{container.TerrainHeight}) from {diskPath}");
			return upToDate;
		}
		catch (Exception ex)
		{
			GD.PushWarning($"[ao] Failed to load '{diskPath}': {ex.Message}");
			Clear(preserveActiveScenario: true);
			return false;
		}
	}

	/// <summary>
	/// Hot-binds a freshly baked <paramref name="container"/> into the live scene if <paramref name="level"/>
	/// and <paramref name="gdbPath"/> are still the active loaded scenario.
	/// </summary>
	public static void HotBindIfActive(LevelEntry level, string gdbPath, StaticAoContainer container)
	{
		string expectedKey = GetScenarioAoDiskPath(level, gdbPath);
		bool stillActive;
		lock (ActiveScenarioGate)
		{
			stillActive = BakedAoEnabled &&
				Engine_LightingFrame.ModernModel &&
				string.Equals(_activeScenarioKey, expectedKey, StringComparison.OrdinalIgnoreCase);
		}

		if (!stillActive)
		{
			container.PropAoBytes = Array.Empty<byte>();
			container.TerrainAoBytes = Array.Empty<byte>();
			return;
		}

		int w = container.TerrainWidth;
		int h = container.TerrainHeight;
		UploadAndBind(container);
		if (!Refii.Core.Engine_AppRoot.IsTestHost)
		{
			GD.Print($"[ao] {level}: hot-bound background GPU ground AO ({w}x{h})");
		}
	}

	/// <summary>
	/// Uploads <paramref name="container"/> to GPU textures on the main thread and binds global shader parameters.
	/// </summary>
	public static void UploadAndBind(StaticAoContainer container)
	{
		if (container.PlacementAoFactors.Count == 0 &&
		    container.PlacementOffsets.Count > 0 &&
		    container.PropAoBytes.Length > 0)
		{
			container.PlacementAoFactors = ComputePlacementAoFactors(
				container.PlacementOffsets,
				container.PropAoBytes);
		}

		container.PropAoBytes = Array.Empty<byte>();

		StaticAoContainer? previous = Current;
		Current = container;
		if (previous is not null && !ReferenceEquals(previous, container))
		{
			previous.PropAoBytes = Array.Empty<byte>();
			previous.TerrainAoBytes = Array.Empty<byte>();
			previous.RuntimeTerrainTexture = null;
		}

		if (Refii.Core.Engine_AppRoot.IsTestHost)
		{
			container.TerrainAoBytes = Array.Empty<byte>();
			return;
		}

		BindAoTextures(container);
	}

	/// <summary>
	/// Clears the active baked AO container, frees bake caches, and binds fallback white 4x4 textures.
	/// </summary>
	public static void Clear() => Clear(preserveActiveScenario: false);

	private static void Clear(bool preserveActiveScenario)
	{
		if (!preserveActiveScenario)
		{
			lock (ActiveScenarioGate)
			{
				_activeScenarioKey = string.Empty;
			}
		}

		ModelAoSourceCache.Clear();
		StaticAoContainer? previous = Current;
		Current = null;
		if (previous is not null)
		{
			previous.PropAoBytes = Array.Empty<byte>();
			previous.TerrainAoBytes = Array.Empty<byte>();
			previous.RuntimeTerrainTexture = null;
		}

		if (!Refii.Core.Engine_AppRoot.IsTestHost)
		{
			BindAoTextures(null);
		}
	}

	private static void BindAoTextures(StaticAoContainer? container)
	{
		long uploadBytes = container?.TerrainAoBytes is { Length: > 0 } bytes ? bytes.LongLength : 4096L;
		Refii.Core.Engine_ServerSyncs.RunGpuUpload(uploadBytes, () => BindAoTexturesOnMainThread(container));
	}

	private static void BindAoTexturesOnMainThread(StaticAoContainer? container)
	{
		PublishAoParametersOnMainThread();

		if (container is null)
		{
			RenderingServer.GlobalShaderParameterSet(ParamTerrainContactAo, EnsureFallbackTerrainTexture());
			RenderingServer.GlobalShaderParameterSet(ParamTerrainAoRect, Vector4.Zero);
			RenderingServer.GlobalShaderParameterSet(ParamTerrainAoHeight, new Vector4(0.0f, 1.0f, 0.0f, 0.0f));
			ReleasePreviousBoundTerrainTexture(null);
			return;
		}

		// Upload 2D terrain contact AO + normalized terrain height texture (W x H, RG8).
		byte[] terrainSource = container.TerrainAoBytes;
		container.TerrainAoBytes = Array.Empty<byte>();
		if (container.TerrainWidth >= 2 &&
		    container.TerrainHeight >= 2 &&
		    terrainSource.Length >= container.TerrainWidth * container.TerrainHeight * 2)
		{
			Image tImg = Image.CreateFromData(
				container.TerrainWidth,
				container.TerrainHeight,
				false,
				Image.Format.Rg8,
				terrainSource);
			ImageTexture terrainTex = ImageTexture.CreateFromImage(tImg);
			GC.KeepAlive(tImg);
			container.RuntimeTerrainTexture = terrainTex;
			float spanH = MathF.Max(0.01f, container.TerrainMaxHeight - container.TerrainMinHeight);
			RenderingServer.GlobalShaderParameterSet(ParamTerrainContactAo, terrainTex);
			RenderingServer.GlobalShaderParameterSet(ParamTerrainAoRect, container.TerrainWorldRect);
			RenderingServer.GlobalShaderParameterSet(ParamTerrainAoHeight, new Vector4(container.TerrainMinHeight, spanH, 1.0f, 0.0f));
			ReleasePreviousBoundTerrainTexture(terrainTex);
		}
		else
		{
			RenderingServer.GlobalShaderParameterSet(ParamTerrainContactAo, EnsureFallbackTerrainTexture());
			RenderingServer.GlobalShaderParameterSet(ParamTerrainAoRect, Vector4.Zero);
			RenderingServer.GlobalShaderParameterSet(ParamTerrainAoHeight, new Vector4(0.0f, 1.0f, 0.0f, 0.0f));
			ReleasePreviousBoundTerrainTexture(null);
		}
	}

	private static ImageTexture EnsureFallbackTerrainTexture()
	{
		if (_fallbackTerrainTexture is not null && GodotObject.IsInstanceValid(_fallbackTerrainTexture))
		{
			return _fallbackTerrainTexture;
		}

		var white = new byte[32];
		Array.Fill(white, (byte)255);
		Image img = Image.CreateFromData(4, 4, false, Image.Format.Rg8, white);
		_fallbackTerrainTexture = ImageTexture.CreateFromImage(img);
		GC.KeepAlive(img);
		return _fallbackTerrainTexture;
	}

	private sealed record CachedModelAoSource(
		int SourceVertexTotal,
		Vector3[] Positions,
		Vector3[] Normals,
		bool[] HasVertex,
		float[] SelfAo);

	private static readonly ConcurrentDictionary<string, CachedModelAoSource?> ModelAoSourceCache =
		new(StringComparer.OrdinalIgnoreCase);

	public static void ClearBakeCaches() => ModelAoSourceCache.Clear();

	private static CachedModelAoSource? GetOrLoadModelAoSource(string modelPath, Func<string, byte[]?> read)
	{
		if (string.IsNullOrEmpty(modelPath))
		{
			return null;
		}

		return ModelAoSourceCache.GetOrAdd(modelPath, path =>
		{
			byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(path) ?? read(path);
			if (bytes is null ||
			    !Engine_MdlData.TryParseInfo(bytes, path, out MdlInfo info, out _) ||
			    info.IsStub ||
			    info.SkinnedCount > 0)
			{
				return null;
			}

			int sourceVertexTotal = 0;
			foreach (MdlMeshBuffer buffer in info.MeshBuffers)
			{
				sourceVertexTotal += (int)buffer.VertexCount;
			}

			if (sourceVertexTotal <= 0 || sourceVertexTotal > MaxVerticesPerPlacement)
			{
				return null;
			}

			if (!Engine_MdlGeometry.TryBuild(bytes, info, out List<MdlGeometry> geometries))
			{
				return null;
			}

			if (!Engine_ModelSelfAo.TryExtractCanonicalSourceVertices(
				geometries,
				sourceVertexTotal,
				out Vector3[] positions,
				out Vector3[] normals,
				out bool[] hasVertex))
			{
				return null;
			}

			float[] selfAo = Engine_ModelSelfAo.GetOrComputeModelSelfAo(
				bytes, info, geometries, sourceVertexTotal, path);

			return new CachedModelAoSource(sourceVertexTotal, positions, normals, hasVertex, selfAo);
		});
	}

	private readonly record struct PropPlacementWorkItem(
		ulong Key,
		Transform3D Transform,
		CachedModelAoSource Source,
		int BaseVertexOffset);

	/// <summary>
	/// Bakes a <see cref="StaticAoContainer"/> from a pre-built world <see cref="Engine_LevelShadowMaps.BvhTree"/>,
	/// static prop blocks, and optional primary <see cref="GhfHeights"/> terrain heightfield.
	/// </summary>
	public static StaticAoContainer BakeFromBvh(
		Engine_LevelShadowMaps.BvhTree worldBvh,
		IReadOnlyList<Engine_LevelProps.StaticShadowPropBlock> propBlocks,
		IReadOnlySet<uint>? lightHosts,
		IReadOnlySet<string>? fixtureModelPaths,
		GhfHeights? primaryTerrain,
		Func<string, byte[]?> read,
		Func<bool>? isCancelled = null)
	{
		const float cellSize = 16.0f;
		const float invCellSize = 1.0f / cellSize;
		var cellBvhCache = new ConcurrentDictionary<(int cx, int cz), Engine_LevelShadowMaps.BvhTree>();

		Engine_LevelShadowMaps.BvhTree GetLocalBvh(Vector3 worldPos, float margin)
		{
			int cx = (int)MathF.Floor(worldPos.X * invCellSize);
			int cz = (int)MathF.Floor(worldPos.Z * invCellSize);
			return cellBvhCache.GetOrAdd((cx, cz), key =>
			{
				Vector3 bMin = new(key.cx * cellSize - margin, -10000.0f, key.cz * cellSize - margin);
				Vector3 bMax = new((key.cx + 1) * cellSize + margin, 10000.0f, (key.cz + 1) * cellSize + margin);
				var localTris = new List<Engine_LevelShadowMaps.BvhTriangle>(256);
				worldBvh.CollectInBounds(bMin, bMax, localTris);
				return new Engine_LevelShadowMaps.BvhTree(localTris);
			});
		}

		// 1. Per-model .selfao is computed lazily in parallel by Engine_MdlMesh when each model loads;
		// avoid any single-threaded sweep over propBlocks here.
		var placementOffsets = new Dictionary<ulong, int>();
		byte[] propAoBytes = Array.Empty<byte>();

		// 2. Bake 2D Terrain Contact AO map if a heightfield is present.
		int terrainW = 0;
		int terrainH = 0;
		Vector4 terrainRect = Vector4.Zero;
		float terrainMinH = 0.0f;
		float terrainMaxH = 0.0f;
		byte[] terrainAoBytes = Array.Empty<byte>();

		if (primaryTerrain is { Width: >= 2, Height: >= 2, Spacing: > 0.0f } ghf &&
		    ghf.Values.Length >= ghf.Width * ghf.Height)
		{
			float fullSpanX = (ghf.Width - 1) * ghf.Spacing;
			float fullSpanZ = (ghf.Height - 1) * ghf.Spacing;
			float cropOriginX = ghf.OriginX;
			float cropOriginZ = ghf.OriginZ;
			float cropSpanX = fullSpanX;
			float cropSpanZ = fullSpanZ;

			if (ghf.Width < 32 && ghf.Height < 32)
			{
				// Small synthetic test grids (e.g., 9x9 unit test) use 2x + 1 resolution over full bounds.
				terrainW = Math.Clamp((ghf.Width - 1) * 2 + 1, 2, 1024);
				terrainH = Math.Clamp((ghf.Height - 1) * 2 + 1, 2, 1024);
			}
			else
			{
				int targetRes = TerrainAoResolution;
				terrainW = targetRes;
				terrainH = targetRes;

				// Crop the 2K / 4K / 8K ground contact AO map tightly to the XZ bounding box of placed
				// static geometry (+ 8m margin) so texel density is concentrated in the playable area
				// (~5-10 cm/texel) instead of wasted across 1 km of outer sea/hill vista.
				if (worldBvh.TriangleCount > 0)
				{
					Vector3 bvhMin = worldBvh.RootMin;
					Vector3 bvhMax = worldBvh.RootMax;
					// World X is mirrored (-localX), so localX spans [-bvhMax.X .. -bvhMin.X].
					float propMinLocalX = MathF.Max(ghf.OriginX, -bvhMax.X - 8.0f);
					float propMaxLocalX = MathF.Min(ghf.OriginX + fullSpanX, -bvhMin.X + 8.0f);
					float propMinLocalZ = MathF.Max(ghf.OriginZ, bvhMin.Z - 8.0f);
					float propMaxLocalZ = MathF.Min(ghf.OriginZ + fullSpanZ, bvhMax.Z + 8.0f);

					if (propMaxLocalX - propMinLocalX >= 16.0f && propMaxLocalZ - propMinLocalZ >= 16.0f)
					{
						cropOriginX = propMinLocalX;
						cropOriginZ = propMinLocalZ;
						cropSpanX = propMaxLocalX - propMinLocalX;
						cropSpanZ = propMaxLocalZ - propMinLocalZ;
					}
				}
			}

			terrainRect = new Vector4(
				cropOriginX,
				cropOriginZ,
				cropSpanX > 1e-5f ? 1.0f / cropSpanX : 0.0f,
				cropSpanZ > 1e-5f ? 1.0f / cropSpanZ : 0.0f);

			bool gpuBaked = !Refii.Core.Engine_AppRoot.IsTestHost &&
				worldBvh.TriangleCount > 0 &&
				Engine_GpuBvhBaker.TryBakeGroundAoGpu(
					worldBvh,
					in ghf,
					terrainW,
					terrainH,
					cropOriginX,
					cropOriginZ,
					cropSpanX,
					cropSpanZ,
					TerrainAoMaxRadius,
					MinTerrainContactAo,
					out terrainAoBytes,
					out terrainMinH,
					out terrainMaxH,
					isCancelled);

			if (!gpuBaked && isCancelled?.Invoke() != true)
			{
				var rawAo = new float[terrainW * terrainH];
				var sampleHeights = new float[terrainW * terrainH];
				const float terrainTMin = 0.04f;
				const float invTerrainRadius = 1.0f / TerrainAoMaxRadius;
				float invRayCount = 1.0f / HemisphereKernel.Length;
				float invFullSpanX = fullSpanX > 1e-5f ? 1.0f / fullSpanX : 0.0f;
				float invFullSpanZ = fullSpanZ > 1e-5f ? 1.0f / fullSpanZ : 0.0f;

				Parallel.For(0, terrainH, ty =>
				{
					float vCrop = terrainH > 1 ? (float)ty / (terrainH - 1) : 0.0f;
					float localZ = cropOriginZ + vCrop * cropSpanZ;
					float worldZ = localZ;
					float vGhf = Math.Clamp((localZ - ghf.OriginZ) * invFullSpanZ, 0.0f, 1.0f);

					for (int tx = 0; tx < terrainW; tx++)
					{
						float uCrop = terrainW > 1 ? (float)tx / (terrainW - 1) : 0.0f;
						float localX = cropOriginX + uCrop * cropSpanX;
						float worldX = -localX;
						float uGhf = Math.Clamp((localX - ghf.OriginX) * invFullSpanX, 0.0f, 1.0f);

						SampleHeightAndWorldNormal(in ghf, uGhf, vGhf, out float wy, out Vector3 worldNormal);
						int idx = ty * terrainW + tx;
						sampleHeights[idx] = wy;

						Vector3 worldPos = new(worldX, wy, worldZ);
						Engine_LevelShadowMaps.BvhTree localBvh = GetLocalBvh(worldPos, TerrainAoMaxRadius + 1.0f);

						Engine_ModelSelfAo.BuildOrthonormalBasis(worldNormal, out Vector3 tangent, out Vector3 bitangent);
						Vector3 rayOrigin = worldPos + worldNormal * 0.03f;
						float occSum = 0.0f;
						int hitCount = 0;
						int farRoofHits = 0;

						for (int r = 0; r < HemisphereKernel.Length; r++)
						{
							Vector3 local = HemisphereKernel[r];
							Vector3 dir = (tangent * local.X + bitangent * local.Y + worldNormal * local.Z).Normalized();
							if (localBvh.TraceClosestHit(rayOrigin, dir, terrainTMin, TerrainAoMaxRadius, out float hitDist, out _))
							{
								float falloff = MathF.Max(0.0f, 1.0f - hitDist * invTerrainRadius);
								occSum += falloff * falloff;
								hitCount++;
								if (local.Z > 0.70f && hitDist > 2.25f)
								{
									farRoofHits++;
								}
							}
						}

						float aoVal = Math.Clamp(1.0f - occSum * invRayCount, MinTerrainContactAo, 1.0f);
						if (hitCount >= 22 && farRoofHits >= 5)
						{
							aoVal = Mathf.Lerp(aoVal, 0.85f, 0.75f);
						}

						rawAo[idx] = aoVal;
					}
				});

				terrainMinH = float.MaxValue;
				terrainMaxH = float.MinValue;
				for (int i = 0; i < sampleHeights.Length; i++)
				{
					float h = sampleHeights[i];
					if (float.IsFinite(h))
					{
						if (h < terrainMinH) terrainMinH = h;
						if (h > terrainMaxH) terrainMaxH = h;
					}
				}

				if (terrainMinH > terrainMaxH)
				{
					terrainMinH = 0.0f;
					terrainMaxH = 1.0f;
				}

				float invHeightSpan = (terrainMaxH - terrainMinH) > 1e-4f ? 1.0f / (terrainMaxH - terrainMinH) : 0.0f;

				// Parallel 3x3 height-aware bilateral filter to soften contact rays without bleeding across cliffs.
				terrainAoBytes = new byte[terrainW * terrainH * 2];
				int wLocal = terrainW;
				int hLocal = terrainH;
				float minHLocal = terrainMinH;
				Parallel.For(0, hLocal, ty =>
				{
					for (int tx = 0; tx < wLocal; tx++)
					{
						int centerIdx = ty * wLocal + tx;
						float centerH = sampleHeights[centerIdx];
						float sumAo = 0.0f;
						float sumWeight = 0.0f;

						for (int dy = -1; dy <= 1; dy++)
						{
							int ny = Math.Clamp(ty + dy, 0, hLocal - 1);
							int rowBase = ny * wLocal;
							for (int dx = -1; dx <= 1; dx++)
							{
								int nx = Math.Clamp(tx + dx, 0, wLocal - 1);
								int nIdx = rowBase + nx;
								float dh = sampleHeights[nIdx] - centerH;
								float dh2 = dh * dh * 2.0f;
								float spatialW = (dx == 0 && dy == 0) ? 1.0f : (dx == 0 || dy == 0) ? 0.6f : 0.36f;
								float heightW = 1.0f / (1.0f + dh2 + 0.5f * dh2 * dh2);
								float w = spatialW * heightW;
								sumAo += rawAo[nIdx] * w;
								sumWeight += w;
							}
						}

						float filtered = sumWeight > 1e-6f ? sumAo / sumWeight : rawAo[centerIdx];
						float normH = Math.Clamp((centerH - minHLocal) * invHeightSpan, 0.0f, 1.0f);
						terrainAoBytes[centerIdx * 2] = (byte)Math.Clamp((int)MathF.Round(filtered * 255.0f), 0, 255);
						terrainAoBytes[centerIdx * 2 + 1] = (byte)Math.Clamp((int)MathF.Round(normH * 255.0f), 0, 255);
					}
				});
			}
		}

		return new StaticAoContainer
		{
			PlacementOffsets = placementOffsets,
			PlacementAoFactors = new Dictionary<ulong, float>(),
			PropAoBytes = propAoBytes,
			TerrainWidth = terrainW,
			TerrainHeight = terrainH,
			TerrainWorldRect = terrainRect,
			TerrainMinHeight = terrainMinH,
			TerrainMaxHeight = terrainMaxH,
			TerrainAoBytes = terrainAoBytes,
		};
	}

	private static void SampleHeightAndWorldNormal(
		in GhfHeights ghf,
		float u,
		float v,
		out float height,
		out Vector3 worldNormal)
	{
		float gx = Math.Clamp(u * (ghf.Width - 1), 0.0f, ghf.Width - 1);
		float gy = Math.Clamp(v * (ghf.Height - 1), 0.0f, ghf.Height - 1);

		int x0 = (int)MathF.Floor(gx);
		int y0 = (int)MathF.Floor(gy);
		int x1 = Math.Min(x0 + 1, ghf.Width - 1);
		int y1 = Math.Min(y0 + 1, ghf.Height - 1);
		float fx = gx - x0;
		float fy = gy - y0;

		float h00 = GetFiniteHeight(in ghf, x0, y0);
		float h10 = GetFiniteHeight(in ghf, x1, y0);
		float h01 = GetFiniteHeight(in ghf, x0, y1);
		float h11 = GetFiniteHeight(in ghf, x1, y1);

		float h0 = h00 + (h10 - h00) * fx;
		float h1 = h01 + (h11 - h01) * fx;
		height = h0 + (h1 - h0) * fy;

		int cx = Math.Clamp((int)MathF.Round(gx), 0, ghf.Width - 1);
		int cy = Math.Clamp((int)MathF.Round(gy), 0, ghf.Height - 1);
		int xm = Math.Max(0, cx - 1);
		int xp = Math.Min(ghf.Width - 1, cx + 1);
		int ym = Math.Max(0, cy - 1);
		int yp = Math.Min(ghf.Height - 1, cy + 1);

		float hL = GetFiniteHeight(in ghf, xm, cy);
		float hR = GetFiniteHeight(in ghf, xp, cy);
		float hD = GetFiniteHeight(in ghf, cx, ym);
		float hU = GetFiniteHeight(in ghf, cx, yp);

		float dx = Math.Max(1, xp - xm) * ghf.Spacing;
		float dz = Math.Max(1, yp - ym) * ghf.Spacing;
		// In local space, nLocal = (hL - hR, dx/dz, hD - hU). Under MirrorX (worldX = -localX), X is negated.
		Vector3 n = new((hR - hL) / dx, 1.0f, (hD - hU) / dz);
		worldNormal = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Up;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static float GetFiniteHeight(in GhfHeights ghf, int x, int y)
	{
		float h = ghf.Values[y * ghf.Width + x];
		return float.IsFinite(h) ? h : 0.0f;
	}

	/// <summary>
	/// Extracts all static world triangles, static props, and terrain heightfields for <paramref name="level"/>
	/// and bakes a <see cref="StaticAoContainer"/>.
	/// </summary>
	public static bool BakeScenario(
		LevelEntry level,
		Func<string, byte[]?> read,
		out StaticAoContainer? container,
		out string gdbPath,
		out int triangleCount,
		out string error)
	{
		container = null;
		if (!Engine_LevelShadowMaps.TryBuildWorldGeometry(
			level,
			read,
			out Engine_LevelShadowMaps.BvhTree? worldBvh,
			out List<Engine_LevelProps.StaticShadowPropBlock> propBlocks,
			out HashSet<uint> lightHosts,
			out HashSet<string> fixtureModelPaths,
			out _,
			out List<GhfHeights> terrainHeights,
			out gdbPath,
			out triangleCount,
			out error) || worldBvh is null)
		{
			return false;
		}

		GhfHeights? primaryTerrain = SelectPrimaryTerrain(terrainHeights);
		container = BakeFromBvh(worldBvh, propBlocks, lightHosts, fixtureModelPaths, primaryTerrain, read);
		return true;
	}

	internal static GhfHeights? SelectPrimaryTerrain(IReadOnlyList<GhfHeights> terrains)
	{
		GhfHeights? best = null;
		int bestSamples = 0;
		for (int i = 0; i < terrains.Count; i++)
		{
			GhfHeights candidate = terrains[i];
			int samples = candidate.Width * candidate.Height;
			if (samples > bestSamples)
			{
				bestSamples = samples;
				best = candidate;
			}
		}

		return best;
	}
}

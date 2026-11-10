using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using Refii.Level;
using Refii.Models;
using Refii.Textures;

namespace Refii.Lighting;

/// <summary>
/// Deserialized static octahedral shadow depth atlas for one level scenario.
/// </summary>
public sealed class StaticShadowMapContainer
{
	public required int AtlasSize { get; init; }

	/// <summary>Number of slices/layers in the octahedral shadow depth atlas array.</summary>
	public int SliceCount { get; init; } = 1;

	/// <summary>Max lights packed per map slice.</summary>
	public int LightsPerMap { get; init; } = 16;

	/// <summary>The <c>placed_light_reach_scale</c> CVar multiplier used during baking.</summary>
	public float PlacedReachScale { get; init; } = 1.0f;

	/// <summary>The <c>building_interior_reach_scale</c> CVar multiplier used during baking.</summary>
	public float BuildingInteriorReachScale { get; init; } = 1.0f;

	/// <summary>Maps 64-bit light key ((entity &lt;&lt; 32) | index) to (AtlasU, AtlasV, AtlasScale, SliceIndex).</summary>
	public required Dictionary<ulong, Vector4> LightRects { get; init; }

	/// <summary>IEEE 754 binary16 (half-float) depth texels, row-major (SliceCount * AtlasSize * AtlasSize).</summary>
	public required ushort[] Texels { get; set; }

	public Texture2DArray? RuntimeTextureArray { get; set; }
}

/// <summary>
/// Offline octahedral static shadow map baker, binary <c>.shadows</c> serializer, and runtime
/// global shader atlas binder (<c>fable_static_shadow_atlas</c>).
/// </summary>
public static class Engine_LevelShadowMaps
{
	public const uint Magic = 0x4D534652u; // "RFSM" little-endian
	public const uint Version = 2u;
	public const int DefaultAtlasResolution = 2048;
	public const int DefaultLightsPerMap = 36;

	private static int? _configuredAtlasResolution;
	private static int? _configuredLightsPerMap;

	/// <summary>
	/// Automatically selects the optimal number of static fixture lights packed per shadow map
	/// slice for a given <paramref name="atlasResolution"/> preset (2K -> 36 [6x6], 4K/8K -> 64 [8x8]).
	/// </summary>
	public static int GetLightsPerMapForResolution(int atlasResolution)
	{
		if (atlasResolution >= 4096)
		{
			return 64; // 8x8 grid: 512x512 per light at 4K, 1024x1024 per light at 8K
		}

		if (atlasResolution >= 2048)
		{
			return 36; // 6x6 grid: 341x341 per light at 2K
		}

		return 16; // 4x4 grid for sub-2K test atlases
	}

	/// <summary>
	/// Returns the effective lights-per-map for <paramref name="atlasResolution"/>, honouring the
	/// optional <c>REFII_SHADOW_LIGHTS_PER_MAP</c> environment override when present.
	/// </summary>
	public static int GetConfiguredLightsPerMapForResolution(int atlasResolution)
	{
		try
		{
			string? env = System.Environment.GetEnvironmentVariable("REFII_SHADOW_LIGHTS_PER_MAP");
			if (!string.IsNullOrEmpty(env) &&
			    int.TryParse(env, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int envLpm) &&
			    envLpm >= 1)
			{
				return Math.Clamp(envLpm, 1, 256);
			}
		}
		catch (Exception)
		{
		}

		return GetLightsPerMapForResolution(atlasResolution);
	}

	/// <summary>
	/// Configured static shadow map resolution read from <c>static_shadow_resolution</c> CVar (defaults to 2048).
	/// </summary>
	public static int ConfiguredAtlasResolution
	{
		get
		{
			if (_configuredAtlasResolution is null)
			{
				try
				{
					string? env = System.Environment.GetEnvironmentVariable("REFII_SHADOW_RES");
					if (!string.IsNullOrEmpty(env) &&
					    int.TryParse(env, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int envRes) &&
					    envRes >= 64)
					{
						_configuredAtlasResolution = Math.Clamp(envRes, 64, 16384);
						return _configuredAtlasResolution.Value;
					}

					int res = Refii.Core.Engine_RefiiConfig.Int(
						Refii.Core.Engine_RefiiConfig.Read(out _),
						"static_shadow_resolution",
						DefaultAtlasResolution);
					_configuredAtlasResolution = Math.Clamp(res, 64, 16384);
				}
				catch (Exception)
				{
					_configuredAtlasResolution = DefaultAtlasResolution;
				}
			}

			return _configuredAtlasResolution.Value;
		}
	}

	/// <summary>
	/// Automatically selected max static shadow lights packed per map slice for <see cref="ConfiguredAtlasResolution"/>.
	/// </summary>
	public static int ConfiguredLightsPerMap
	{
		get
		{
			_configuredLightsPerMap ??= GetConfiguredLightsPerMapForResolution(ConfiguredAtlasResolution);
			return _configuredLightsPerMap.Value;
		}
	}

	public static void ReloadConfig()
	{
		_configuredAtlasResolution = null;
		_configuredLightsPerMap = null;
	}
	public const float MaxHalfDepth = 65504.0f;
	private const ushort MaxHalfDepthBits = 0x7BFF; // 65504.0 in IEEE 754 half-float

	private static Texture2DArray? _fallbackTextureArray;

	public static StaticShadowMapContainer? Current { get; private set; }

	/// <summary>
	/// Maps a unit direction vector to octahedral UV coordinates in [0, 1]^2.
	/// Matches <c>fable_octahedral_uv(vec3 dir)</c> in <c>fable_lighting.gdshaderinc</c>.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 OctahedralUvFromDirection(Vector3 dir)
	{
		float l1 = MathF.Abs(dir.X) + MathF.Abs(dir.Y) + MathF.Abs(dir.Z);
		if (l1 < 1e-8f)
		{
			return new Vector2(0.5f, 0.5f);
		}

		float nx = dir.X / l1;
		float ny = dir.Y / l1;
		float nz = dir.Z / l1;

		if (nz < 0.0f)
		{
			float ox = (1.0f - MathF.Abs(ny)) * (nx >= 0.0f ? 1.0f : -1.0f);
			float oy = (1.0f - MathF.Abs(nx)) * (ny >= 0.0f ? 1.0f : -1.0f);
			nx = ox;
			ny = oy;
		}

		return new Vector2(nx * 0.5f + 0.5f, ny * 0.5f + 0.5f);
	}

	/// <summary>
	/// Inverse mapping from octahedral UV in [0, 1]^2 to a unit 3D direction vector.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector3 DirectionFromOctahedralUv(Vector2 uv)
	{
		float ox = uv.X * 2.0f - 1.0f;
		float oy = uv.Y * 2.0f - 1.0f;
		float oz = 1.0f - MathF.Abs(ox) - MathF.Abs(oy);

		if (oz < 0.0f)
		{
			float oldX = ox;
			ox = (1.0f - MathF.Abs(oy)) * (oldX >= 0.0f ? 1.0f : -1.0f);
			oy = (1.0f - MathF.Abs(oldX)) * (oy >= 0.0f ? 1.0f : -1.0f);
		}

		return new Vector3(ox, oy, oz).Normalized();
	}

	/// <summary>
	/// Wraps octahedral UV coordinates outside [0, 1]^2 across octahedral fold edges.
	/// Used to populate the 1-texel border gutter around each light tile for seamless PCF.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Vector2 WrapOctahedralUv(Vector2 uv)
	{
		float u = uv.X;
		float v = uv.Y;

		if (u < 0.0f)
		{
			u = -u;
			v = 1.0f - v;
		}
		else if (u > 1.0f)
		{
			u = 2.0f - u;
			v = 1.0f - v;
		}

		if (v < 0.0f)
		{
			v = -v;
			u = 1.0f - u;
		}
		else if (v > 1.0f)
		{
			v = 2.0f - v;
			u = 1.0f - u;
		}

		return new Vector2(Mathf.Clamp(u, 0.0f, 1.0f), Mathf.Clamp(v, 0.0f, 1.0f));
	}

	/// <summary>
	/// Directory under which generated static shadow depth atlases are cached.
	/// Located under <c>&lt;app&gt;/cache/&lt;Title&gt;/shadows</c> (same derived cache container as textures).
	/// </summary>
	public static string CacheShadowsDirectory =>
		Path.Combine(Refii.Core.Engine_Title.Active.CacheDirectory, "shadows");

	/// <summary>
	/// Computes the relative path segment for a level and scenario (e.g. <c>albion/bwsmarket/defaultscenario.shadows</c>).
	/// </summary>
	public static string GetShadowMapRelativePath(LevelEntry level, string gdbPath = "")
	{
		string world = SanitizeSegment(level.World.Length != 0 ? level.World : "albion");
		string levelPath = level.LevelPath.Replace('\\', '/').Trim('/');
		if (levelPath.Length == 0)
		{
			levelPath = SanitizeSegment(level.Name);
		}

		string scenario = SanitizeSegment(level.Scenario.Length != 0 ? level.Scenario : "default");
		if (gdbPath.Length != 0)
		{
			string stem = SanitizeSegment(Path.GetFileNameWithoutExtension(gdbPath));
			string leaf = SanitizeSegment(Path.GetFileName(levelPath));
			if (stem.Length != 0 &&
			    !stem.Equals(leaf, StringComparison.OrdinalIgnoreCase) &&
			    !stem.Equals(scenario, StringComparison.OrdinalIgnoreCase))
			{
				scenario = $"{scenario}_{stem}";
			}
		}

		return $"{world}/{levelPath}/{scenario}.shadows";
	}

	/// <summary>
	/// Computes the disk path for a cached static shadow map under <see cref="CacheShadowsDirectory"/>.
	/// </summary>
	public static string GetShadowMapDiskPath(LevelEntry level, string gdbPath = "")
	{
		string rel = GetShadowMapRelativePath(level, gdbPath).Replace('/', Path.DirectorySeparatorChar);
		return Path.Combine(CacheShadowsDirectory, rel);
	}

	/// <summary>
	/// Computes the legacy canonical <c>res://assets/ShadowMaps/...</c> path for a level and scenario.
	/// </summary>
	public static string GetShadowMapResourcePath(LevelEntry level, string gdbPath = "")
	{
		return $"res://assets/ShadowMaps/{GetShadowMapRelativePath(level, gdbPath)}";
	}

	private static string SanitizeSegment(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "default";
		}

		char[] chars = value.Trim().ToLowerInvariant().ToCharArray();
		for (int i = 0; i < chars.Length; i++)
		{
			char c = chars[i];
			if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '/')
			{
				chars[i] = '_';
			}
		}

		return new string(chars);
	}

	/// <summary>
	/// Serializes a <see cref="StaticShadowMapContainer"/> into the compact <c>.shadows</c> binary format (Version 2).
	/// </summary>
	public static byte[] Serialize(StaticShadowMapContainer container)
	{
		int lightCount = container.LightRects.Count;
		int sliceCount = Math.Max(1, container.SliceCount);
		int texelBytes = sliceCount * container.AtlasSize * container.AtlasSize * sizeof(ushort);
		int totalBytes = 32 + lightCount * 24 + texelBytes;
		byte[] buffer = new byte[totalBytes];
		Span<byte> span = buffer.AsSpan();

		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), Magic);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), Version);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), (uint)container.AtlasSize);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), (uint)sliceCount);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), (uint)lightCount);
		BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20, 4), (uint)container.LightsPerMap);
		BinaryPrimitives.WriteSingleLittleEndian(span.Slice(24, 4), container.PlacedReachScale);
		BinaryPrimitives.WriteSingleLittleEndian(span.Slice(28, 4), container.BuildingInteriorReachScale);

		var sortedKeys = new List<ulong>(container.LightRects.Keys);
		sortedKeys.Sort();

		int offset = 32;
		foreach (ulong key in sortedKeys)
		{
			Vector4 rect = container.LightRects[key];
			BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset, 8), key);
			BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), rect.X);
			BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 12, 4), rect.Y);
			BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 16, 4), rect.Z);
			BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 20, 4), rect.W);
			offset += 24;
		}

		ReadOnlySpan<byte> rawTexels = MemoryMarshal.AsBytes(container.Texels.AsSpan());
		rawTexels.CopyTo(span.Slice(offset, texelBytes));
		return buffer;
	}

	/// <summary>
	/// Deserializes a <see cref="StaticShadowMapContainer"/> from raw <c>.shadows</c> bytes.
	/// Supports both Version 2 (32-byte header with multi-slice) and Version 1 (16-byte header legacy).
	/// </summary>
	public static bool TryDeserialize(
		ReadOnlySpan<byte> bytes,
		out StaticShadowMapContainer container,
		out string error)
	{
		container = null!;
		error = string.Empty;

		if (bytes.Length < 16)
		{
			error = "file truncated (< 16-byte header)";
			return false;
		}

		uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0, 4));
		if (magic != Magic)
		{
			error = $"invalid magic 0x{magic:X8}";
			return false;
		}

		uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
		if (version != 1u && version != 2u)
		{
			error = $"unsupported version {version}";
			return false;
		}

		if (version == 2u)
		{
			if (bytes.Length < 32)
			{
				error = "file truncated (< 32-byte header v2)";
				return false;
			}

			int atlasSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
			int sliceCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4));
			int lightCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(16, 4));
			int lightsPerMap = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(20, 4));
			float placedReach = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(24, 4));
			float interiorReach = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(28, 4));

			if (atlasSize <= 0 || atlasSize > 16384 || sliceCount <= 0 || sliceCount > 256 || lightCount < 0 || lightCount > 65536)
			{
				error = $"invalid header dimensions (atlas={atlasSize}, slices={sliceCount}, lights={lightCount})";
				return false;
			}

			long expectedBytes = 32L + lightCount * 24L + (long)sliceCount * atlasSize * atlasSize * sizeof(ushort);
			if (bytes.Length < expectedBytes)
			{
				error = $"truncated payload (got {bytes.Length} bytes, expected {expectedBytes})";
				return false;
			}

			var rects = new Dictionary<ulong, Vector4>(lightCount);
			int offset = 32;
			for (int i = 0; i < lightCount; i++)
			{
				ulong key = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
				float u = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 8, 4));
				float v = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 12, 4));
				float scale = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 16, 4));
				float layer = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 20, 4));
				rects[key] = new Vector4(u, v, scale, layer);
				offset += 24;
			}

			int totalTexels = sliceCount * atlasSize * atlasSize;
			var texels = new ushort[totalTexels];
			bytes.Slice(offset, totalTexels * sizeof(ushort)).CopyTo(MemoryMarshal.AsBytes(texels.AsSpan()));

			container = new StaticShadowMapContainer
			{
				AtlasSize = atlasSize,
				SliceCount = sliceCount,
				LightsPerMap = lightsPerMap,
				PlacedReachScale = placedReach,
				BuildingInteriorReachScale = interiorReach,
				LightRects = rects,
				Texels = texels,
			};
			return true;
		}
		else
		{
			// Version 1 legacy format
			int atlasSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(8, 4));
			int lightCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4));
			if (atlasSize <= 0 || atlasSize > 16384 || lightCount < 0 || lightCount > 65536)
			{
				error = $"invalid header dimensions (atlas={atlasSize}, lights={lightCount})";
				return false;
			}

			long expectedBytes = 16L + lightCount * 20L + (long)atlasSize * atlasSize * sizeof(ushort);
			if (bytes.Length < expectedBytes)
			{
				error = $"truncated payload (got {bytes.Length} bytes, expected {expectedBytes})";
				return false;
			}

			var rects = new Dictionary<ulong, Vector4>(lightCount);
			int offset = 16;
			for (int i = 0; i < lightCount; i++)
			{
				ulong key = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
				float u = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 8, 4));
				float v = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 12, 4));
				float scale = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset + 16, 4));
				rects[key] = new Vector4(u, v, scale, 0.0f);
				offset += 20;
			}

			int texelCount = atlasSize * atlasSize;
			var texels = new ushort[texelCount];
			bytes.Slice(offset, texelCount * sizeof(ushort)).CopyTo(MemoryMarshal.AsBytes(texels.AsSpan()));

			container = new StaticShadowMapContainer
			{
				AtlasSize = atlasSize,
				SliceCount = 1,
				LightsPerMap = Math.Max(1, lightCount),
				PlacedReachScale = 1.0f,
				BuildingInteriorReachScale = 1.0f,
				LightRects = rects,
				Texels = texels,
			};
			return true;
		}
	}

	/// <summary>
	/// Reads the binary header from a <c>.shadows</c> file on disk without allocating or loading texels.
	/// </summary>
	public static bool TryReadHeader(
		string filePath,
		out uint magic,
		out uint version,
		out int atlasSize,
		out int sliceCount,
		out int lightCount,
		out int lightsPerMap,
		out float placedReach,
		out float interiorReach)
	{
		magic = 0;
		version = 0;
		atlasSize = 0;
		sliceCount = 0;
		lightCount = 0;
		lightsPerMap = 0;
		placedReach = 0f;
		interiorReach = 0f;

		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
		{
			return false;
		}

		try
		{
			using var fs = new FileStream(filePath, FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
			if (fs.Length < 16)
			{
				return false;
			}

			Span<byte> header = stackalloc byte[32];
			int bytesRead = fs.Read(header);
			if (bytesRead < 16)
			{
				return false;
			}

			magic = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4));
			version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
			atlasSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));

			if (version == 1u)
			{
				sliceCount = 1;
				lightCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
				lightsPerMap = Math.Max(1, lightCount);
				placedReach = 1.0f;
				interiorReach = 1.0f;
				return true;
			}

			if (bytesRead < 32)
			{
				return false;
			}

			sliceCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12, 4));
			lightCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
			lightsPerMap = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20, 4));
			placedReach = BinaryPrimitives.ReadSingleLittleEndian(header.Slice(24, 4));
			interiorReach = BinaryPrimitives.ReadSingleLittleEndian(header.Slice(28, 4));
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// Backward-compatible 4-field header reader.
	/// </summary>
	public static bool TryReadHeader(
		string filePath,
		out uint magic,
		out uint version,
		out int atlasSize,
		out int lightCount)
	{
		bool ok = TryReadHeader(
			filePath,
			out magic,
			out version,
			out atlasSize,
			out _,
			out lightCount,
			out _,
			out _,
			out _);
		return ok;
	}

	/// <summary>
	/// Checks whether a <c>.shadows</c> file exists and was baked with the exact specified settings.
	/// Inspects the header and verifies exact file length to guarantee payload integrity.
	/// </summary>
	public static bool IsFileValid(
		string filePath,
		int expectedResolution,
		int expectedLightsPerMap,
		float expectedPlacedReach,
		float expectedInteriorReach,
		uint expectedVersion = Version)
	{
		if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
		{
			return false;
		}

		try
		{
			var fi = new FileInfo(filePath);
			if (fi.Length < 32)
			{
				return false;
			}

			if (!TryReadHeader(
				filePath,
				out uint magic,
				out uint version,
				out int atlasSize,
				out int sliceCount,
				out int lightCount,
				out int lightsPerMap,
				out float placedReach,
				out float interiorReach))
			{
				return false;
			}

			if (magic != Magic || version != expectedVersion ||
			    atlasSize != expectedResolution ||
			    lightsPerMap != expectedLightsPerMap ||
			    sliceCount <= 0 ||
			    MathF.Abs(placedReach - expectedPlacedReach) >= 1e-3f ||
			    MathF.Abs(interiorReach - expectedInteriorReach) >= 1e-3f)
			{
				return false;
			}

			long expectedBytes = 32L + lightCount * 24L + (long)sliceCount * atlasSize * atlasSize * sizeof(ushort);
			return fi.Length == expectedBytes;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// Checks whether a <c>.shadows</c> file exists and matches current CVar settings.
	/// </summary>
	public static bool IsFileValid(string filePath, int expectedResolution, uint expectedVersion = Version)
		=> IsFileValid(
			filePath,
			expectedResolution,
			GetConfiguredLightsPerMapForResolution(expectedResolution),
			Engine_LevelLights.PlacedRange,
			Engine_LevelLights.BuildingInteriorReachScale,
			expectedVersion);

	/// <summary>
	/// Loads and binds the scenario's <c>.shadows</c> file if present on disk, or binds a neutral
	/// unoccluded fallback atlas when absent. Safe to call from any thread (defers GPU upload if
	/// called off the main thread).
	/// </summary>
	public static void LoadOrClear(LevelEntry level, string gdbPath)
	{
		if (Refii.Core.Engine_AppRoot.IsTestHost)
		{
			return;
		}

		if (Refii.AndroidRuntime.AndroidBakedShadows.LevelsIgnoreBakes) { Clear(); return; }

		// 1. Try cache directory first (<app>/cache/shadows/...)
		string diskPath = GetShadowMapDiskPath(level, gdbPath);
		byte[]? raw = File.Exists(diskPath) ? File.ReadAllBytes(diskPath) : null;
		string resolvedLocation = diskPath;

		if (raw is null && gdbPath.Length != 0)
		{
			string fallbackDiskPath = GetShadowMapDiskPath(level, string.Empty);
			if (!fallbackDiskPath.Equals(diskPath, StringComparison.OrdinalIgnoreCase) && File.Exists(fallbackDiskPath))
			{
				raw = File.ReadAllBytes(fallbackDiskPath);
				resolvedLocation = fallbackDiskPath;
			}
		}

		// 2. Fallback to shipped/project res://assets/ShadowMaps/...
		if (raw is null)
		{
			string resPath = GetShadowMapResourcePath(level, gdbPath);
			raw = TryReadShadowMapBytes(resPath);
			resolvedLocation = resPath;

			if (raw is null && gdbPath.Length != 0)
			{
				string fallbackResPath = GetShadowMapResourcePath(level, string.Empty);
				if (!fallbackResPath.Equals(resPath, StringComparison.OrdinalIgnoreCase))
				{
					raw = TryReadShadowMapBytes(fallbackResPath);
					if (raw is not null)
					{
						resolvedLocation = fallbackResPath;
					}
				}
			}
		}

		if (raw is null)
		{
			Clear();
			GD.Print($"[shadow-map] {level}: no baked static shadow map at {resolvedLocation}");
			return;
		}

		if (!TryDeserialize(raw, out StaticShadowMapContainer container, out string error))
		{
			Clear();
			GD.Print($"[shadow-map] {level}: failed to read {resolvedLocation} ({error})");
			return;
		}

		StaticShadowMapContainer? previous = Current;
		Current = container;
		if (previous is not null && !ReferenceEquals(previous, container))
		{
			previous.Texels = Array.Empty<ushort>();
			previous.RuntimeTextureArray = null;
		}

		Engine_LightTable.RebindShadowMap(container);
		BindAtlasTexture(container);
		GD.Print(
			$"[shadow-map] {level}: loaded {container.AtlasSize}x{container.AtlasSize} static shadow atlas " +
			$"({container.LightRects.Count} lights) from {resolvedLocation}");
	}

	/// <summary>
	/// Releases the active static shadow map container, unbinds its GPU texture array in favour of
	/// the 4x4 fallback atlas, and clears cached model triangles.
	/// </summary>
	public static void Clear()
	{
		ModelTriangleCache.Clear();
		StaticShadowMapContainer? previous = Current;
		Current = null;
		if (previous is not null)
		{
			previous.Texels = Array.Empty<ushort>();
			previous.RuntimeTextureArray = null;
		}

		Engine_LightTable.RebindShadowMap(null);
		if (!Refii.Core.Engine_AppRoot.IsTestHost)
		{
			BindAtlasTexture(null);
		}
	}

	private static byte[]? TryReadShadowMapBytes(string resPath)
	{
		try
		{
			string diskPath = ProjectSettings.GlobalizePath(resPath);
			if (File.Exists(diskPath))
			{
				return File.ReadAllBytes(diskPath);
			}
		}
		catch (Exception)
		{
			// Fall back to Godot FileAccess for packed .pck exports.
		}

		if (Godot.FileAccess.FileExists(resPath))
		{
			return Godot.FileAccess.GetFileAsBytes(resPath);
		}

		return null;
	}

	private static Texture2DArray? _boundAtlasTextureArray;

	private static void ReleasePreviousBoundAtlas(Texture2DArray? replacement, Rid texRid, int atlasSize)
	{
		Texture2DArray? previous = _boundAtlasTextureArray;
		_boundAtlasTextureArray = replacement;
		ActiveAtlasSize = atlasSize;
		AtlasGeneration++;
		RenderingServer.CallOnRenderThread(Callable.From(() =>
		{
			ActiveAtlasRdRid = texRid.IsValid ? RenderingServer.TextureGetRdTexture(texRid) : default;
			if (previous is not null &&
			    !ReferenceEquals(previous, replacement) &&
			    !ReferenceEquals(previous, _fallbackTextureArray))
			{
				Callable.From(() =>
				{
					if (GodotObject.IsInstanceValid(previous))
					{
						previous.Dispose();
					}
				}).CallDeferred();
			}
		}));
	}

	private static void BindAtlasTexture(StaticShadowMapContainer? container)
	{
		long uploadBytes = container is null
			? 4096L
			: System.Math.Max(4096L, (long)System.Math.Max(1, container.SliceCount) * container.AtlasSize * container.AtlasSize * sizeof(ushort));
		Refii.Core.Engine_ServerSyncs.RunGpuUpload(uploadBytes, () => BindAtlasTextureOnMainThread(container));
	}

	private static void BindAtlasTextureOnMainThread(StaticShadowMapContainer? container)
	{
		if (container is null)
		{
			if (_fallbackTextureArray is null || !GodotObject.IsInstanceValid(_fallbackTextureArray))
			{
				ushort[] fill = new ushort[16];
				Array.Fill(fill, MaxHalfDepthBits);
				byte[] bytes = new byte[32];
				MemoryMarshal.AsBytes(fill.AsSpan()).CopyTo(bytes);
				using var img = Image.CreateFromData(4, 4, false, Image.Format.Rh, bytes);
				var array = new Texture2DArray();
				var images = new Godot.Collections.Array<Image> { img };
				array.CreateFromImages(images);
				images.Clear();
				_fallbackTextureArray = array;
			}

			RenderingServer.GlobalShaderParameterSet(
				Engine_LightTable.ShadowAtlasGlobalName, _fallbackTextureArray);
			ReleasePreviousBoundAtlas(null, _fallbackTextureArray.GetRid(), 4);
			return;
		}

		int sliceCount = Math.Max(1, container.SliceCount);
		int sliceTexelCount = container.AtlasSize * container.AtlasSize;
		int sliceByteCount = sliceTexelCount * sizeof(ushort);
		if (container.Texels.Length < sliceCount * sliceTexelCount)
		{
			return;
		}

		var sliceImages = new Godot.Collections.Array<Image>();
		var textureArray = new Texture2DArray();
		Error err;
		try
		{
			byte[] rawBytes = new byte[sliceByteCount];
			for (int s = 0; s < sliceCount; s++)
			{
				MemoryMarshal.AsBytes(container.Texels.AsSpan(s * sliceTexelCount, sliceTexelCount)).CopyTo(rawBytes);
				var img = Image.CreateFromData(
					container.AtlasSize, container.AtlasSize, false, Image.Format.Rh, rawBytes);
				sliceImages.Add(img);
			}

			err = textureArray.CreateFromImages(sliceImages);
		}
		finally
		{
			for (int i = 0; i < sliceImages.Count; i++)
			{
				sliceImages[i]?.Dispose();
			}

			sliceImages.Clear();
		}

		// Free the CPU half-float array (up to 256+ MB at 4K) now that slices are on the GPU.
		container.Texels = Array.Empty<ushort>();

		if (err == Error.Ok)
		{
			container.RuntimeTextureArray = textureArray;
			RenderingServer.GlobalShaderParameterSet(Engine_LightTable.ShadowAtlasGlobalName, textureArray);
			ReleasePreviousBoundAtlas(textureArray, textureArray.GetRid(), Math.Max(1, container.AtlasSize));
		}
		else
		{
			textureArray.Dispose();
			GD.PrintErr($"[shadow-map] failed to create Texture2DArray from {sliceCount} slices: {err}");
		}
	}

	/// <summary>RenderingDevice texture RID of the currently bound <c>fable_static_shadow_atlas</c> <see cref="Texture2DArray"/>.</summary>
	public static Rid ActiveAtlasRdRid { get; private set; }

	/// <summary>Width/height in texels of the currently bound <c>fable_static_shadow_atlas</c> <see cref="Texture2DArray"/>.</summary>
	public static int ActiveAtlasSize { get; private set; } = 4;

	/// <summary>Incremented whenever the bound static shadow atlas changes.</summary>
	public static int AtlasGeneration { get; private set; }

	// ============================================================================================
	// Offline BVH & Parallel Octahedral Raytracer
	// ============================================================================================

	public readonly struct BvhTriangle
	{
		public readonly Vector3 V0;
		public readonly Vector3 Edge1;
		public readonly Vector3 Edge2;
		public readonly Vector3 Normal;
		public readonly Vector3 Min;
		public readonly Vector3 Max;
		public readonly Vector3 Centroid;

		public BvhTriangle(Vector3 a, Vector3 b, Vector3 c)
		{
			V0 = a;
			Edge1 = b - a;
			Edge2 = c - a;
			Vector3 cross = Edge1.Cross(Edge2);
			float lenSq = cross.LengthSquared();
			Normal = lenSq > 1e-12f ? cross / MathF.Sqrt(lenSq) : Vector3.Up;
			Min = new Vector3(
				MathF.Min(a.X, MathF.Min(b.X, c.X)),
				MathF.Min(a.Y, MathF.Min(b.Y, c.Y)),
				MathF.Min(a.Z, MathF.Min(b.Z, c.Z)));
			Max = new Vector3(
				MathF.Max(a.X, MathF.Max(b.X, c.X)),
				MathF.Max(a.Y, MathF.Max(b.Y, c.Y)),
				MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
			Centroid = (a + b + c) * (1.0f / 3.0f);
		}
	}

	public sealed class BvhTree
	{
		private struct Node
		{
			public Vector3 Min;
			public Vector3 Max;
			public int LeftOrFirst;
			public int Count; // > 0 for leaf, 0 for internal node (Right = LeftOrFirst + 1)
		}

		private readonly BvhTriangle[] _triangles;
		private readonly Node[] _nodes;
		private int _nodeCount;

		public int TriangleCount => _triangles.Length;

		public BvhTree(List<BvhTriangle> sourceTriangles)
		{
			_triangles = sourceTriangles.ToArray();
			if (_triangles.Length == 0)
			{
				_nodes = Array.Empty<Node>();
				return;
			}

			_nodes = new Node[Math.Max(1, _triangles.Length * 2)];
			_nodeCount = 1;
			BuildRecursive(0, 0, _triangles.Length);
		}

		private void BuildRecursive(int nodeIdx, int start, int count)
		{
			Vector3 bMin = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
			Vector3 bMax = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
			Vector3 cMin = bMin;
			Vector3 cMax = bMax;

			int end = start + count;
			for (int i = start; i < end; i++)
			{
				ref readonly BvhTriangle tri = ref _triangles[i];
				bMin = bMin.Min(tri.Min);
				bMax = bMax.Max(tri.Max);
				cMin = cMin.Min(tri.Centroid);
				cMax = cMax.Max(tri.Centroid);
			}

			_nodes[nodeIdx].Min = bMin;
			_nodes[nodeIdx].Max = bMax;

			if (count <= 4)
			{
				_nodes[nodeIdx].LeftOrFirst = start;
				_nodes[nodeIdx].Count = count;
				return;
			}

			Vector3 extent = cMax - cMin;
			int axis = 0;
			if (extent.Y > extent.X && extent.Y >= extent.Z)
			{
				axis = 1;
			}
			else if (extent.Z > extent.X && extent.Z >= extent.Y)
			{
				axis = 2;
			}

			float splitPos = axis == 0
				? (cMin.X + cMax.X) * 0.5f
				: axis == 1
					? (cMin.Y + cMax.Y) * 0.5f
					: (cMin.Z + cMax.Z) * 0.5f;

			int mid = start;
			for (int i = start; i < end; i++)
			{
				float c = axis == 0
					? _triangles[i].Centroid.X
					: axis == 1
						? _triangles[i].Centroid.Y
						: _triangles[i].Centroid.Z;
				if (c < splitPos)
				{
					(_triangles[i], _triangles[mid]) = (_triangles[mid], _triangles[i]);
					mid++;
				}
			}

			if (mid == start || mid == end)
			{
				mid = start + (count >> 1);
			}

			int leftIdx = _nodeCount;
			_nodeCount += 2;
			if (_nodeCount > _nodes.Length)
			{
				_nodes[nodeIdx].LeftOrFirst = start;
				_nodes[nodeIdx].Count = count;
				return;
			}

			_nodes[nodeIdx].LeftOrFirst = leftIdx;
			_nodes[nodeIdx].Count = 0;

			BuildRecursive(leftIdx, start, mid - start);
			BuildRecursive(leftIdx + 1, mid, end - mid);
		}

		/// <summary>
		/// Collects all triangles whose AABB intersects the axis-aligned bounding box [boxMin, boxMax].
		/// Used to build a tiny per-light local BVH before tracing that light's octahedral tile.
		/// </summary>
		public void CollectInBounds(Vector3 boxMin, Vector3 boxMax, List<BvhTriangle> into)
		{
			if (_triangles.Length == 0)
			{
				return;
			}

			Span<int> stack = stackalloc int[64];
			int sp = 0;
			stack[sp++] = 0;

			while (sp > 0)
			{
				ref readonly Node node = ref _nodes[stack[--sp]];
				if (node.Max.X < boxMin.X || node.Min.X > boxMax.X ||
				    node.Max.Y < boxMin.Y || node.Min.Y > boxMax.Y ||
				    node.Max.Z < boxMin.Z || node.Min.Z > boxMax.Z)
				{
					continue;
				}

				if (node.Count > 0)
				{
					int end = node.LeftOrFirst + node.Count;
					for (int i = node.LeftOrFirst; i < end; i++)
					{
						ref readonly BvhTriangle tri = ref _triangles[i];
						if (tri.Max.X >= boxMin.X && tri.Min.X <= boxMax.X &&
						    tri.Max.Y >= boxMin.Y && tri.Min.Y <= boxMax.Y &&
						    tri.Max.Z >= boxMin.Z && tri.Min.Z <= boxMax.Z)
						{
							into.Add(tri);
						}
					}
				}
				else if (sp + 2 <= stack.Length)
				{
					stack[sp++] = node.LeftOrFirst + 1;
					stack[sp++] = node.LeftOrFirst;
				}
			}
		}

		/// <summary>
		/// Traces a ray from <paramref name="origin"/> along unit <paramref name="dir"/> over
		/// [tMin, tMax] and returns the closest hit distance and geometric surface normal.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool TraceClosestHit(
			Vector3 origin,
			Vector3 dir,
			float tMin,
			float tMax,
			out float hitDist,
			out Vector3 hitNormal)
		{
			hitDist = tMax;
			hitNormal = Vector3.Up;
			if (_triangles.Length == 0)
			{
				return false;
			}

			float invX = MathF.Abs(dir.X) > 1e-8f ? 1.0f / dir.X : MathF.CopySign(1e8f, dir.X);
			float invY = MathF.Abs(dir.Y) > 1e-8f ? 1.0f / dir.Y : MathF.CopySign(1e8f, dir.Y);
			float invZ = MathF.Abs(dir.Z) > 1e-8f ? 1.0f / dir.Z : MathF.CopySign(1e8f, dir.Z);

			Span<int> stack = stackalloc int[64];
			int sp = 0;
			stack[sp++] = 0;
			bool anyHit = false;

			while (sp > 0)
			{
				ref readonly Node node = ref _nodes[stack[--sp]];
				if (!IntersectAabb(origin, invX, invY, invZ, node.Min, node.Max, tMin, hitDist))
				{
					continue;
				}

				if (node.Count > 0)
				{
					int end = node.LeftOrFirst + node.Count;
					for (int i = node.LeftOrFirst; i < end; i++)
					{
						ref readonly BvhTriangle tri = ref _triangles[i];
						if (IntersectTriangleTwoSided(origin, dir, in tri, tMin, hitDist, out float t))
						{
							hitDist = t;
							hitNormal = tri.Normal;
							anyHit = true;
						}
					}
				}
				else if (sp + 2 <= stack.Length)
				{
					stack[sp++] = node.LeftOrFirst + 1;
					stack[sp++] = node.LeftOrFirst;
				}
			}

			return anyHit;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool IntersectAabb(
			Vector3 origin,
			float invX,
			float invY,
			float invZ,
			Vector3 bMin,
			Vector3 bMax,
			float tMin,
			float tMax)
		{
			float tx1 = (bMin.X - origin.X) * invX;
			float tx2 = (bMax.X - origin.X) * invX;
			float tmin = MathF.Max(tMin, MathF.Min(tx1, tx2));
			float tmax = MathF.Min(tMax, MathF.Max(tx1, tx2));

			float ty1 = (bMin.Y - origin.Y) * invY;
			float ty2 = (bMax.Y - origin.Y) * invY;
			tmin = MathF.Max(tmin, MathF.Min(ty1, ty2));
			tmax = MathF.Min(tmax, MathF.Max(ty1, ty2));

			float tz1 = (bMin.Z - origin.Z) * invZ;
			float tz2 = (bMax.Z - origin.Z) * invZ;
			tmin = MathF.Max(tmin, MathF.Min(tz1, tz2));
			tmax = MathF.Min(tmax, MathF.Max(tz1, tz2));

			return tmax >= tmin;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool IntersectTriangleTwoSided(
			Vector3 origin,
			Vector3 dir,
			in BvhTriangle tri,
			float tMin,
			float tMax,
			out float t)
		{
			t = 0.0f;
			Vector3 pvec = dir.Cross(tri.Edge2);
			float det = tri.Edge1.Dot(pvec);
			if (MathF.Abs(det) < 1e-8f)
			{
				return false;
			}

			float invDet = 1.0f / det;
			Vector3 tvec = origin - tri.V0;
			float u = tvec.Dot(pvec) * invDet;
			if (u < 0.0f || u > 1.0f)
			{
				return false;
			}

			Vector3 qvec = tvec.Cross(tri.Edge1);
			float v = dir.Dot(qvec) * invDet;
			if (v < 0.0f || u + v > 1.0f)
			{
				return false;
			}

			float hit = tri.Edge2.Dot(qvec) * invDet;
			if (hit <= tMin || hit >= tMax)
			{
				return false;
			}

			t = hit;
			return true;
		}
	}

	public readonly record struct BakeLightSpec(
		ulong Key,
		Vector3 WorldOrigin,
		float MaxRange,
		bool IsSpot,
		Vector3 SpotForward,
		float SpotCosLimit);

	/// <summary>
	/// Bakes a <see cref="StaticShadowMapContainer"/> from a pre-built <see cref="BvhTree"/> and a
	/// list of shadow-casting lights, partitioned across multiple atlas slices according to <paramref name="lightsPerMap"/>.
	/// </summary>
	public static StaticShadowMapContainer BakeFromBvh(
		BvhTree worldBvh,
		IReadOnlyList<BakeLightSpec> lights,
		int atlasResolution,
		int lightsPerMap,
		float placedReachScale,
		float interiorReachScale)
	{
		atlasResolution = Math.Clamp(atlasResolution, 64, 16384);
		lightsPerMap = Math.Clamp(lightsPerMap, 1, 256);

		int sliceCount = Math.Max(1, (int)MathF.Ceiling((float)lights.Count / lightsPerMap));
		int sliceTexelCount = atlasResolution * atlasResolution;
		var texels = new ushort[sliceCount * sliceTexelCount];
		Array.Fill(texels, MaxHalfDepthBits);
		var rects = new Dictionary<ulong, Vector4>(lights.Count);

		if (lights.Count == 0)
		{
			return new StaticShadowMapContainer
			{
				AtlasSize = atlasResolution,
				SliceCount = 1,
				LightsPerMap = lightsPerMap,
				PlacedReachScale = placedReachScale,
				BuildingInteriorReachScale = interiorReachScale,
				LightRects = rects,
				Texels = texels,
			};
		}

		int tilesPerRow = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(lightsPerMap)));
		int tileTotalSize = atlasResolution / tilesPerRow;
		int innerSize = Math.Max(2, tileTotalSize - 2);

		float invAtlas = 1.0f / atlasResolution;
		float rectScale = innerSize * invAtlas;

		for (int i = 0; i < lights.Count; i++)
		{
			int sliceIndex = i / lightsPerMap;
			int localIndex = i % lightsPerMap;
			int tileX = (localIndex % tilesPerRow) * tileTotalSize;
			int tileY = (localIndex / tilesPerRow) * tileTotalSize;

			rects[lights[i].Key] = new Vector4(
				(tileX + 1.0f) * invAtlas,
				(tileY + 1.0f) * invAtlas,
				rectScale,
				sliceIndex);
		}

		float texelAngle = 2.0f / Math.Max(1, innerSize);

		Parallel.For(0, lights.Count, i =>
		{
			int sliceIndex = i / lightsPerMap;
			int localIndex = i % lightsPerMap;
			int tileX = (localIndex % tilesPerRow) * tileTotalSize;
			int tileY = (localIndex / tilesPerRow) * tileTotalSize;
			int sliceOffset = sliceIndex * sliceTexelCount;

			BakeLightSpec light = lights[i];
			Vector3 rangeVec = new(light.MaxRange, light.MaxRange, light.MaxRange);
			var localTris = new List<BvhTriangle>(256);
			worldBvh.CollectInBounds(light.WorldOrigin - rangeVec, light.WorldOrigin + rangeVec, localTris);
			if (localTris.Count == 0)
			{
				return;
			}

			var localBvh = new BvhTree(localTris);
			float invInner = 1.0f / innerSize;

			// 1. Trace inner texels [1 .. innerSize].
			for (int iy = 0; iy < innerSize; iy++)
			{
				int py = tileY + 1 + iy;
				int rowOffset = sliceOffset + py * atlasResolution;
				float v = (iy + 0.5f) * invInner;

				for (int ix = 0; ix < innerSize; ix++)
				{
					float u = (ix + 0.5f) * invInner;
					Vector3 dir = DirectionFromOctahedralUv(new Vector2(u, v));

					if (light.IsSpot && dir.Dot(light.SpotForward) < light.SpotCosLimit)
					{
						continue;
					}

					if (localBvh.TraceClosestHit(
						light.WorldOrigin, dir, 0.20f, light.MaxRange, out float hitDist, out Vector3 hitNormal))
					{
						float cosTheta = MathF.Abs(hitNormal.Dot(dir));
						float sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
						float tanTheta = MathF.Min(sinTheta / MathF.Max(cosTheta, 0.08f), 8.0f);
						float biased = hitDist + 0.025f + hitDist * texelAngle * (0.75f + tanTheta);
						ushort halfBits = BitConverter.HalfToUInt16Bits((Half)MathF.Min(biased, MaxHalfDepth));
						texels[rowOffset + tileX + 1 + ix] = halfBits;
					}
				}
			}

			// 2. Populate the 1-texel octahedral wrapped border gutter around the tile.
			for (int by = 0; by < tileTotalSize; by++)
			{
				for (int bx = 0; bx < tileTotalSize; bx++)
				{
					bool isBorder = bx == 0 || bx == tileTotalSize - 1 || by == 0 || by == tileTotalSize - 1;
					if (!isBorder)
					{
						continue;
					}

					float rawU = (bx - 1 + 0.5f) * invInner;
					float rawV = (by - 1 + 0.5f) * invInner;
					Vector2 wrapped = WrapOctahedralUv(new Vector2(rawU, rawV));
					int srcIx = Math.Clamp((int)MathF.Floor(wrapped.X * innerSize), 0, innerSize - 1);
					int srcIy = Math.Clamp((int)MathF.Floor(wrapped.Y * innerSize), 0, innerSize - 1);

					int srcIdx = sliceOffset + (tileY + 1 + srcIy) * atlasResolution + (tileX + 1 + srcIx);
					int dstIdx = sliceOffset + (tileY + by) * atlasResolution + (tileX + bx);
					texels[dstIdx] = texels[srcIdx];
				}
			}
		});

		return new StaticShadowMapContainer
		{
			AtlasSize = atlasResolution,
			SliceCount = sliceCount,
			LightsPerMap = lightsPerMap,
			PlacedReachScale = placedReachScale,
			BuildingInteriorReachScale = interiorReachScale,
			LightRects = rects,
			Texels = texels,
		};
	}

	/// <summary>
	/// Bakes a <see cref="StaticShadowMapContainer"/> from a pre-built <see cref="BvhTree"/> using configured CVars.
	/// </summary>
	public static StaticShadowMapContainer BakeFromBvh(
		BvhTree worldBvh,
		IReadOnlyList<BakeLightSpec> lights,
		int atlasResolution)
		=> BakeFromBvh(
			worldBvh,
			lights,
			atlasResolution,
			GetConfiguredLightsPerMapForResolution(atlasResolution),
			Engine_LevelLights.PlacedRange,
			Engine_LevelLights.BuildingInteriorReachScale);

	private static readonly ConcurrentDictionary<string, Vector3[]?> ModelTriangleCache =
		new(StringComparer.OrdinalIgnoreCase);

	public static void ClearBakeCaches() => ModelTriangleCache.Clear();

	/// <summary>Drops every model's triangles read so far, which a sweep otherwise holds for all its areas.</summary>
	internal static void ClearBakeTriangles() => ModelTriangleCache.Clear();

	/// <summary>The bound static shadow atlas's texels, for the heap census.</summary>
	internal static (long Atlases, long Bytes) AtlasCensus() =>
		Current is { } atlas ? (1, atlas.Texels.LongLength * 2) : (0, 0);

	/// <summary>The bake's per-model triangle soups still cached, for the heap census.</summary>
	internal static (long Models, long Bytes) TriangleCensus()
	{
		long models = 0, bytes = 0;
		foreach (Vector3[]? triangles in ModelTriangleCache.Values)
		{
			++models;
			bytes += (triangles?.LongLength ?? 0) * 12;
		}

		return (models, bytes);
	}

	/// <summary>
	/// Extracts all static world triangles (terrain + static shadow-casting props), resolved
	/// static prop blocks, terrain heightfields, and placed light specs for <paramref name="level"/>,
	/// building a shared <see cref="BvhTree"/> that both shadow-map baking and level AO baking can use.
	/// </summary>
	public static bool TryBuildWorldGeometry(
		LevelEntry level,
		Func<string, byte[]?> read,
		out BvhTree? worldBvh,
		out List<Engine_LevelProps.StaticShadowPropBlock> propBlocks,
		out HashSet<uint> lightHosts,
		out HashSet<string> fixtureModelPaths,
		out List<BakeLightSpec> bakeLights,
		out List<GhfHeights> terrainHeights,
		out string gdbPath,
		out int triangleCount,
		out string error)
	{
		worldBvh = null;
		propBlocks = new List<Engine_LevelProps.StaticShadowPropBlock>();
		lightHosts = new HashSet<uint>();
		fixtureModelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		bakeLights = new List<BakeLightSpec>();
		terrainHeights = new List<GhfHeights>();
		gdbPath = string.Empty;
		triangleCount = 0;
		error = string.Empty;

		Engine_LevelVfsConfig.Apply(level, read);
		if (Engine_LevelManifest.TryResolve(
			level, read, out ScenarioManifest manifest, out _, out string scenarioUsed) &&
		    level.Scenario.Length == 0)
		{
			level.Scenario = scenarioUsed;
		}

		if (!Engine_LevelProps.TryCollectStaticGeometryAndLights(
			level, read,
			out propBlocks,
			out lightHosts,
			out HashSet<string> lightHostModels,
			out List<Engine_LevelLights.ResolvedLightInstance> resolvedLights,
			out gdbPath))
		{
			error = "no GDB or static props resolved";
			return false;
		}

		// Filter to active placed lights and compute their world-space origins & ranges.
		// In modern lighting mode (Engine_LevelLights.PlacedShadowsEnabled), all placed lights cast shadows,
		// not just the 1-2 fixtures per region that had CastShadows=true on Xbox 360.
		bakeLights = new List<BakeLightSpec>(resolvedLights.Count);
		foreach (Engine_LevelLights.ResolvedLightInstance item in resolvedLights)
		{
			if (item.Light.Range <= 0.05f || item.Light.Intensity <= 0.0f)
			{
				continue;
			}

			// Match runtime range envelope via the CVar-backed ComputeMaxBakeRange helper.
			float maxRange = Engine_LevelLights.ComputeMaxBakeRange(item);
			Vector3 localOrigin = item.Transform.Origin;
			// Root node carries Scale = (-1, 1, 1), so world X is negated relative to region-local X.
			Vector3 worldOrigin = new(-localOrigin.X, localOrigin.Y, localOrigin.Z);

			bool isSpot = item.Light.Type == Engine_LevelLights.LightTypeSpot;
			Vector3 localForward = -item.Transform.Basis.Z;
			Vector3 worldForward = new Vector3(-localForward.X, localForward.Y, localForward.Z).Normalized();
			float halfAngle = Mathf.Clamp(item.Light.HalfAngleRadians, 0.05f, MathF.PI * 0.49f);
			float spotCosLimit = MathF.Cos(MathF.Min(MathF.PI * 0.5f, halfAngle + 0.08f));

			bakeLights.Add(new BakeLightSpec(
				item.LightKey,
				worldOrigin,
				maxRange,
				isSpot,
				worldForward,
				spotCosLimit));
		}

		// Collect model paths of any prop block that hosts a light (so .engine_level or GMD layout
		// duplicates of streetlamps/lanterns with instanceHash == 0 are also excluded).
		foreach (string hostModel in lightHostModels)
		{
			if (!IsStructuralLightHostModel(hostModel))
			{
				fixtureModelPaths.Add(hostModel);
			}
		}

		foreach (Engine_LevelProps.StaticShadowPropBlock block in propBlocks)
		{
			if (IsLightFixtureModel(block.ModelPath))
			{
				fixtureModelPaths.Add(block.ModelPath);
				continue;
			}

			for (int i = 0; i < block.InstanceHashes.Count; i++)
			{
				uint h = block.InstanceHashes[i];
				if (h != 0u && lightHosts.Contains(h) && !IsStructuralLightHostModel(block.ModelPath))
				{
					fixtureModelPaths.Add(block.ModelPath);
					break;
				}
			}
		}

		var worldTriangles = new List<BvhTriangle>(65536);

		// 1. Add main terrain triangles.
		AppendTerrainTriangles(level, manifest, read, worldTriangles, terrainHeights);

		// 2. Add static shadow-casting prop triangles.
		foreach (Engine_LevelProps.StaticShadowPropBlock block in propBlocks)
		{
			if (block.Foliage || fixtureModelPaths.Contains(block.ModelPath))
			{
				continue;
			}

			bool isTree = block.ModelClass == PropModelClass.Tree;
			string casterPath = isTree ? block.ShadowModelPath : block.ModelPath;
			if (casterPath.Length == 0 || IsLightFixtureModel(casterPath))
			{
				continue;
			}

			Vector3[]? modelVerts = GetShadowCasterTriangles(casterPath, read, isTreeShadowModel: isTree);
			if (modelVerts is null || modelVerts.Length < 3)
			{
				continue;
			}

			for (int i = 0; i < block.Instances.Count; i++)
			{
				byte casting = i < block.InstanceCasting.Count
					? block.InstanceCasting[i]
					: Engine_ClassicShadows.CastsDefault;
				if (casting == 0)
				{
					continue;
				}

				uint instanceHash = i < block.InstanceHashes.Count ? block.InstanceHashes[i] : 0u;
				if (instanceHash != 0u && lightHosts.Contains(instanceHash))
				{
					continue;
				}

				Transform3D inst = block.Instances[i];
				for (int v = 0; v < modelVerts.Length; v += 3)
				{
					Vector3 a = MirrorX(inst * modelVerts[v]);
					Vector3 b = MirrorX(inst * modelVerts[v + 1]);
					Vector3 c = MirrorX(inst * modelVerts[v + 2]);
					worldTriangles.Add(new BvhTriangle(a, b, c));
				}
			}
		}

		triangleCount = worldTriangles.Count;
		worldBvh = new BvhTree(worldTriangles);
		return true;
	}

	/// <summary>
	/// Extracts all static world triangles (terrain + static shadow-casting props) and placed
	/// lights for <paramref name="level"/> and bakes a <see cref="StaticShadowMapContainer"/>.
	/// </summary>
	public static bool BakeScenario(
		LevelEntry level,
		Func<string, byte[]?> read,
		int atlasResolution,
		int lightsPerMap,
		float placedReachScale,
		float interiorReachScale,
		out StaticShadowMapContainer? container,
		out string gdbPath,
		out int triangleCount,
		out string error)
	{
		container = null;
		if (!TryBuildWorldGeometry(
			level, read,
			out BvhTree? bvh,
			out _,
			out _,
			out _,
			out List<BakeLightSpec> bakeLights,
			out _,
			out gdbPath,
			out triangleCount,
			out error) || bvh is null)
		{
			return false;
		}

		container = BakeFromBvh(bvh, bakeLights, atlasResolution, lightsPerMap, placedReachScale, interiorReachScale);
		return true;
	}

	/// <summary>
	/// Extracts all static world triangles and placed lights using configured CVars.
	/// </summary>
	public static bool BakeScenario(
		LevelEntry level,
		Func<string, byte[]?> read,
		int atlasResolution,
		out StaticShadowMapContainer? container,
		out string gdbPath,
		out int triangleCount,
		out string error)
		=> BakeScenario(
			level,
			read,
			atlasResolution,
			GetConfiguredLightsPerMapForResolution(atlasResolution),
			Engine_LevelLights.PlacedRange,
			Engine_LevelLights.BuildingInteriorReachScale,
			out container,
			out gdbPath,
			out triangleCount,
			out error);

	public static bool IsStructuralLightHostModel(string modelPath)
	{
		if (string.IsNullOrEmpty(modelPath))
		{
			return false;
		}

		return modelPath.Contains("marketstall", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("building", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("interior", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("chamber", StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsLightFixtureModel(string modelPath)
	{
		if (string.IsNullOrEmpty(modelPath))
		{
			return false;
		}

		return modelPath.Contains("bs_lights_", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("light_ceiling", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("lightfixing", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("streetlight", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("street_light", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("lamp", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("lantern", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("sconce", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("chandelier", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("candelabra", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("candle", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("brazier", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("torch", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("fireplacegrate", StringComparison.OrdinalIgnoreCase) ||
		       modelPath.Contains("firegrate", StringComparison.OrdinalIgnoreCase);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal static Vector3 MirrorX(Vector3 v) => new(-v.X, v.Y, v.Z);

	private static void AppendTerrainTriangles(
		LevelEntry level,
		ScenarioManifest? manifest,
		Func<string, byte[]?> read,
		List<BvhTriangle> into,
		List<GhfHeights>? decodedTerrains = null)
	{
		var ghfPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (level.HeightfieldPath.Length != 0)
		{
			ghfPaths.Add(level.HeightfieldPath);
		}

		if (manifest is not null)
		{
			foreach (string line in manifest.Ghf)
			{
				if (Engine_LevelManifest.TryResolvePath(line, read, out string resolved))
				{
					ghfPaths.Add(resolved);
				}
			}
		}

		foreach (string ghfPath in ghfPaths)
		{
			if (read(ghfPath) is not { } ghfBytes ||
			    !Engine_GhfTerrain.TryDecode(ghfBytes, out GhfHeights heights, out _))
			{
				continue;
			}

			decodedTerrains?.Add(heights);
			TerrainMesh mesh = Engine_GhfTerrain.BuildMesh(heights);
			float[] pos = mesh.Positions;
			int[] idx = mesh.Indices;
			for (int i = 0; i + 2 < idx.Length; i += 3)
			{
				int i0 = idx[i] * 3;
				int i1 = idx[i + 1] * 3;
				int i2 = idx[i + 2] * 3;
				Vector3 a = new(-pos[i0], pos[i0 + 1], pos[i0 + 2]);
				Vector3 b = new(-pos[i1], pos[i1 + 1], pos[i1 + 2]);
				Vector3 c = new(-pos[i2], pos[i2 + 1], pos[i2 + 2]);
				into.Add(new BvhTriangle(a, b, c));
			}
		}
	}

	private static Vector3[]? GetShadowCasterTriangles(
		string modelPath,
		Func<string, byte[]?> read,
		bool isTreeShadowModel)
	{
		string cacheKey = isTreeShadowModel ? $"tree_shadow|{modelPath}" : modelPath;
		return ModelTriangleCache.GetOrAdd(cacheKey, _ =>
		{
			byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(modelPath) ?? read(modelPath);
			if (bytes is null)
			{
				return null;
			}

			if (!Engine_MdlData.TryParseInfo(bytes, modelPath, out MdlInfo info, out string _) ||
			    info.IsStub)
			{
				return null;
			}

			// Swaying low plants (mode 1 with wind amplitude > 0) cast no shadows.
			if (!isTreeShadowModel && info.SwaysInWind && info.MovementMode == 1 && info.WindAmplitude > 0.0f)
			{
				return null;
			}

			if (!Engine_MdlGeometry.TryBuild(bytes, info, out List<MdlGeometry> geometries))
			{
				return null;
			}

			bool vegetation = info.MeshKind is 1 or 4;
			if (!vegetation)
			{
				foreach (MdlMeshBuffer mb in info.MeshBuffers)
				{
					if (mb.IsFoliagePath || mb.IsEngineTree)
					{
						vegetation = true;
						break;
					}
				}
			}

			var tris = new List<Vector3>(512);
			foreach (MdlGeometry geo in geometries)
			{
				if (geo.VertexCount == 0 || geo.TriangleCount == 0)
				{
					continue;
				}

				// Skip alpha-cutout (0x20, e.g. cobwebs, ivy cards) and translucent (0x80, e.g. window glass)
				// surfaces on standard meshes so light shines through windows and cobweb cards do not cast
				// solid triangles. Dedicated tree _shadow models are untextured/cast-only proxies and are kept.
				if (!isTreeShadowModel)
				{
					if (vegetation)
					{
						continue;
					}

					string diffuse = geo.Material?.Diffuse ?? string.Empty;
					if (diffuse.Length != 0)
					{
						int flags = Engine_TexLibrary.HeaderFlags(
							diffuse, p => Engine_BnkMeshLoader.ReadBytes(p) ?? read(p));
						if ((flags & 0xA0) != 0)
						{
							continue;
						}
					}
				}

				float[] pos = geo.Positions;
				uint[] indices = geo.Indices;
				for (int i = 0; i + 2 < indices.Length; i += 3)
				{
					int i0 = (int)indices[i] * 3;
					int i1 = (int)indices[i + 1] * 3;
					int i2 = (int)indices[i + 2] * 3;
					if (i0 + 2 >= pos.Length || i1 + 2 >= pos.Length || i2 + 2 >= pos.Length)
					{
						continue;
					}

					Vector3 a = new(pos[i0], pos[i0 + 1], pos[i0 + 2]);
					Vector3 b = new(pos[i1], pos[i1 + 1], pos[i1 + 2]);
					Vector3 c = new(pos[i2], pos[i2 + 1], pos[i2 + 2]);
					if (!a.IsFinite() || !b.IsFinite() || !c.IsFinite())
					{
						continue;
					}

					tris.Add(a);
					tris.Add(b);
					tris.Add(c);
				}
			}

			return tris.Count == 0 ? null : tris.ToArray();
		});
	}
}

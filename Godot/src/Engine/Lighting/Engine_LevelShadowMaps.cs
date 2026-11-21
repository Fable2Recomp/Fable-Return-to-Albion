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
	/// slice for a given <paramref name="atlasResolution"/> preset (2K -> 36 [6x6], 4K -> 64 [8x8], 8K -> 121 [11x11]).
	/// </summary>
	public static int GetLightsPerMapForResolution(int atlasResolution)
	{
		if (atlasResolution >= 8192)
		{
			return 121; // 11x11 grid: 744x744 per light at 8K (halves slice count & VRAM vs 8x8 while keeping >2.1x linear res over 2K)
		}

		if (atlasResolution >= 4096)
		{
			return 64; // 8x8 grid: 512x512 per light at 4K
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
		using var ms = new MemoryStream(buffer, writable: true);
		WriteToStream(ms, container);
		return buffer;
	}

	/// <summary>
	/// Streams a <see cref="StaticShadowMapContainer"/> in compact <c>.shadows</c> Version 2 format directly
	/// to <paramref name="stream"/> without allocating a duplicate multi-megabyte Large Object Heap byte array.
	/// </summary>
	public static void WriteToStream(Stream stream, StaticShadowMapContainer container)
	{
		int lightCount = container.LightRects.Count;
		int sliceCount = Math.Max(1, container.SliceCount);
		int texelCount = sliceCount * container.AtlasSize * container.AtlasSize;
		int headerAndTableBytes = 32 + lightCount * 24;
		byte[] headerBuffer = new byte[headerAndTableBytes];
		Span<byte> span = headerBuffer.AsSpan();

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

		stream.Write(span);
		ReadOnlySpan<ushort> texelSpan = container.Texels.AsSpan(0, Math.Min(container.Texels.Length, texelCount));
		ReadOnlySpan<byte> rawTexels = MemoryMarshal.AsBytes(texelSpan);
		const int chunkSize = 256 * 1024;
		int pos = 0;
		while (pos < rawTexels.Length)
		{
			int len = Math.Min(chunkSize, rawTexels.Length - pos);
			stream.Write(rawTexels.Slice(pos, len));
			pos += len;
		}
	}

	/// <summary>
	/// Streams <paramref name="container"/> to a temporary sibling file and atomically renames it onto
	/// <paramref name="diskPath"/> so readers never observe truncated <c>.shadows</c> files and no LOH copy is allocated.
	/// </summary>
	public static void WriteToFileAtomic(string diskPath, StaticShadowMapContainer container)
	{
		string? dir = Path.GetDirectoryName(diskPath);
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}

		string tempPath = $"{diskPath}.{Guid.NewGuid():N}.tmp";
		try
		{
			using (var fs = new FileStream(tempPath, FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, bufferSize: 65536))
			{
				WriteToStream(fs, container);
			}

			File.Move(tempPath, diskPath, overwrite: true);
		}
		catch
		{
			try
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
			catch
			{
			}

			throw;
		}
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

	private static readonly object ActiveScenarioGate = new();
	private static string _activeScenarioKey = string.Empty;

	/// <summary>
	/// Loads and binds the scenario's <c>.shadows</c> file if present on disk, or binds a neutral
	/// unoccluded fallback atlas when absent. Safe to call from any thread (defers GPU upload if
	/// called off the main thread).
	/// Returns <c>true</c> when no background bake is needed (disabled or an up-to-date cache hit was bound).
	/// </summary>
	public static bool LoadOrClear(LevelEntry level, string gdbPath)
	{
		if (Refii.Core.Engine_AppRoot.IsTestHost)
		{
			return true;
		}

		if (Refii.AndroidRuntime.AndroidBakedShadows.LevelsIgnoreBakes)
		{
			Clear();
			return true;
		}

		// 1. Try cache directory first (<app>/cache/shadows/...)
		string diskPath = GetShadowMapDiskPath(level, gdbPath);
		bool upToDate = IsFileValid(
			diskPath,
			ConfiguredAtlasResolution,
			ConfiguredLightsPerMap,
			Engine_LevelLights.PlacedRange,
			Engine_LevelLights.BuildingInteriorReachScale);

		lock (ActiveScenarioGate)
		{
			_activeScenarioKey = diskPath;
		}

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
			Clear(preserveActiveScenario: true);
			GD.Print($"[shadow-map] {level}: no baked static shadow map at {resolvedLocation} (queuing background GPU bake)");
			return false;
		}

		if (!TryDeserialize(raw, out StaticShadowMapContainer container, out string error))
		{
			Clear(preserveActiveScenario: true);
			GD.Print($"[shadow-map] {level}: failed to read {resolvedLocation} ({error})");
			return false;
		}

		StaticShadowMapContainer? previous = Current;
		Current = container;
		if (previous is not null && !ReferenceEquals(previous, container))
		{
			previous.Texels = Array.Empty<ushort>();
			previous.RuntimeTextureArray = null;
		}

		BindAtlasTexture(container);
		Engine_LightTable.RebindShadowMap(container);
		GD.Print(
			$"[shadow-map] {level}: loaded {container.AtlasSize}x{container.AtlasSize} static shadow atlas " +
			$"({container.LightRects.Count} lights) from {resolvedLocation}");
		return upToDate;
	}

	/// <summary>
	/// Hot-binds a freshly baked <paramref name="container"/> into the live scene if <paramref name="level"/>
	/// and <paramref name="gdbPath"/> are still the active loaded scenario.
	/// </summary>
	public static void HotBindIfActive(LevelEntry level, string gdbPath, StaticShadowMapContainer container)
	{
		string expectedKey = GetShadowMapDiskPath(level, gdbPath);
		bool stillActive;
		lock (ActiveScenarioGate)
		{
			stillActive = !Refii.AndroidRuntime.AndroidBakedShadows.LevelsIgnoreBakes &&
				string.Equals(_activeScenarioKey, expectedKey, StringComparison.OrdinalIgnoreCase);
		}

		if (!stillActive || Refii.Core.Engine_AppRoot.IsTestHost)
		{
			container.Texels = Array.Empty<ushort>();
			return;
		}

		StaticShadowMapContainer? previous = Current;
		Current = container;
		if (previous is not null && !ReferenceEquals(previous, container))
		{
			previous.Texels = Array.Empty<ushort>();
			previous.RuntimeTextureArray = null;
		}

		int atlasSize = container.AtlasSize;
		int lightCount = container.LightRects.Count;
		BindAtlasTexture(container);
		Engine_LightTable.RebindShadowMap(container);
		GD.Print($"[shadow-map] {level}: hot-bound background GPU static shadow atlas ({atlasSize}x{atlasSize}, {lightCount} lights)");
	}

	/// <summary>
	/// Releases the active static shadow map container, unbinds its GPU texture array in favour of
	/// the 4x4 fallback atlas, and clears cached model triangles.
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
		if (container is null)
		{
			Refii.Core.Engine_ServerSyncs.RunGpuUpload(4096L, () => BindAtlasTextureOnMainThread(null, null));
			return;
		}

		int sliceCount = Math.Max(1, container.SliceCount);
		int sliceTexelCount = container.AtlasSize * container.AtlasSize;
		int sliceByteCount = sliceTexelCount * sizeof(ushort);
		if (container.Texels.Length < sliceCount * sliceTexelCount)
		{
			return;
		}

		// Construct temporary Image slices on the calling worker thread before marshaling the final
		// Texture2DArray upload onto the main thread, minimizing main-thread time.
		var preparedSlices = new Image[sliceCount];
		long uploadBytes = System.Math.Max(4096L, (long)sliceCount * sliceByteCount);
		try
		{
			byte[] rawBytes = new byte[sliceByteCount];
			for (int s = 0; s < sliceCount; s++)
			{
				MemoryMarshal.AsBytes(container.Texels.AsSpan(s * sliceTexelCount, sliceTexelCount)).CopyTo(rawBytes);
				preparedSlices[s] = Image.CreateFromData(
					container.AtlasSize, container.AtlasSize, false, Image.Format.Rh, rawBytes);
			}

			// Free the CPU half-float array (up to 256+ MB at 4K) before waiting on main-thread GPU upload.
			container.Texels = Array.Empty<ushort>();
			Refii.Core.Engine_ServerSyncs.RunGpuUpload(uploadBytes, () => BindAtlasTextureOnMainThread(container, preparedSlices));
		}
		finally
		{
			for (int i = 0; i < preparedSlices.Length; i++)
			{
				preparedSlices[i]?.Dispose();
			}
		}
	}

	private static void BindAtlasTextureOnMainThread(StaticShadowMapContainer? container, Image[]? preparedSlices)
	{
		if (container is null || preparedSlices is null || preparedSlices.Length == 0)
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

		var sliceImages = new Godot.Collections.Array<Image>();
		var textureArray = new Texture2DArray();
		Error err;
		try
		{
			for (int s = 0; s < preparedSlices.Length; s++)
			{
				sliceImages.Add(preparedSlices[s]);
			}

			err = textureArray.CreateFromImages(sliceImages);
		}
		finally
		{
			sliceImages.Clear();
		}

		if (err == Error.Ok)
		{
			container.RuntimeTextureArray = textureArray;
			RenderingServer.GlobalShaderParameterSet(Engine_LightTable.ShadowAtlasGlobalName, textureArray);
			ReleasePreviousBoundAtlas(textureArray, textureArray.GetRid(), Math.Max(1, container.AtlasSize));
		}
		else
		{
			textureArray.Dispose();
			GD.PrintErr($"[shadow-map] failed to create Texture2DArray from {preparedSlices.Length} slices: {err}");
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
		public const uint FlagShadowCaster = 1u;
		public const uint FlagTerrain = 2u;
		public const uint FlagAoOccluder = 4u;
		public const uint DefaultFlags = FlagShadowCaster | FlagAoOccluder;

		// Only the corners and flags are stored (40 bytes, not 88): a large town's bake holds millions of these, and
		// the derived values below are worked out with the same arithmetic the constructor used to store them.
		public readonly Vector3 V0;
		private readonly Vector3 _b;
		private readonly Vector3 _c;
		public readonly uint Flags;

		public Vector3 Edge1 => _b - V0;
		public Vector3 Edge2 => _c - V0;

		public Vector3 Normal
		{
			get
			{
				Vector3 cross = Edge1.Cross(Edge2);
				float lenSq = cross.LengthSquared();
				return lenSq > 1e-12f ? cross / MathF.Sqrt(lenSq) : Vector3.Up;
			}
		}

		public Vector3 Min => new(
			MathF.Min(V0.X, MathF.Min(_b.X, _c.X)),
			MathF.Min(V0.Y, MathF.Min(_b.Y, _c.Y)),
			MathF.Min(V0.Z, MathF.Min(_b.Z, _c.Z)));

		public Vector3 Max => new(
			MathF.Max(V0.X, MathF.Max(_b.X, _c.X)),
			MathF.Max(V0.Y, MathF.Max(_b.Y, _c.Y)),
			MathF.Max(V0.Z, MathF.Max(_b.Z, _c.Z)));

		public Vector3 Centroid => (V0 + _b + _c) * (1.0f / 3.0f);

		public BvhTriangle(Vector3 a, Vector3 b, Vector3 c, uint flags = DefaultFlags)
		{
			V0 = a;
			_b = b;
			_c = c;
			Flags = flags;
		}
	}

	public sealed class BvhTree
	{
		/// <summary>Subtree presence bit: at least one descendant triangle carries <see cref="BvhTriangle.FlagShadowCaster"/>.</summary>
		public const uint SubtreeHasShadowCaster = 1u;

		/// <summary>Subtree presence bit: at least one descendant triangle carries both <see cref="BvhTriangle.FlagAoOccluder"/> and <see cref="BvhTriangle.FlagTerrain"/>.</summary>
		public const uint SubtreeHasTerrainAo = 2u;

		/// <summary>Subtree presence bit: at least one non-terrain descendant triangle carries <see cref="BvhTriangle.FlagAoOccluder"/>.</summary>
		public const uint SubtreeHasPropAo = 4u;

		private struct Node
		{
			public Vector3 Min;
			public Vector3 Max;
			public int LeftOrFirst;
			public int Count; // > 0 for leaf (1..4), <= 0 for internal node (-subtreeMask; Right = LeftOrFirst + 1)
		}

		private readonly record struct SubtreeSummary(uint Mask, bool HasProps, Vector3 PropMin, Vector3 PropMax);

		private readonly BvhTriangle[] _triangles;
		private Node[] _nodes;
		private int _nodeCount;

		public int TriangleCount => _triangles.Length;
		public int NodeCount => _nodeCount;
		public Vector3 RootMin => _nodeCount > 0 ? _nodes[0].Min : Vector3.Zero;
		public Vector3 RootMax => _nodeCount > 0 ? _nodes[0].Max : Vector3.Zero;

		/// <summary>Whether the tree contains at least one non-terrain triangle (<c>(Flags &amp; FlagTerrain) == 0</c>).</summary>
		public bool HasPropBounds { get; }

		/// <summary>Minimum world-space AABB corner across all non-terrain triangles in this BVH.</summary>
		public Vector3 PropMin { get; }

		/// <summary>Maximum world-space AABB corner across all non-terrain triangles in this BVH.</summary>
		public Vector3 PropMax { get; }

		/// <summary>Maps a triangle's <see cref="BvhTriangle.Flags"/> to its subtree presence bitmask (<c>1u | 2u | 4u</c>).</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static uint SubtreeBitForTriangleFlags(uint triFlags)
		{
			uint mask = (triFlags & BvhTriangle.FlagShadowCaster) != 0u ? SubtreeHasShadowCaster : 0u;
			if ((triFlags & BvhTriangle.FlagAoOccluder) != 0u)
			{
				mask |= (triFlags & BvhTriangle.FlagTerrain) != 0u ? SubtreeHasTerrainAo : SubtreeHasPropAo;
			}

			return mask;
		}

		/// <summary>Bytes of the GPU node buffer: 32 a node (min, first-or-left, max, count).</summary>
		public int GpuNodeBytes => _nodeCount > 0 && _triangles.Length > 0 ? _nodeCount * 32 : 32;

		/// <summary>Bytes of the GPU triangle buffer: 64 a triangle (v0, edge1, edge2, normal and flags).</summary>
		public int GpuTriangleBytes => _nodeCount > 0 && _triangles.Length > 0 ? _triangles.Length * 64 : 64;

		public void ExportGpuBuffers(out byte[] nodeBytes, out byte[] triBytes)
		{
			nodeBytes = new byte[GpuNodeBytes];
			triBytes = new byte[GpuTriangleBytes];
			WriteGpuNodes(0, nodeBytes);
			WriteGpuTriangles(0, triBytes);
		}

		/// <summary>Writes nodes from <paramref name="first"/> into <paramref name="dest"/> (32 bytes a node), so a
		/// buffer can be uploaded in pieces instead of from one array of its full size.</summary>
		public void WriteGpuNodes(int first, Span<byte> dest)
		{
			if (_nodeCount <= 0 || _triangles.Length == 0)
			{
				dest.Clear();
				return;
			}

			Span<float> nFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(dest);
			Span<int> nInts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(dest);
			int count = dest.Length / 32;
			for (int k = 0; k < count; k++)
			{
				ref readonly Node n = ref _nodes[first + k];
				int o = k * 8;
				nFloats[o] = n.Min.X;
				nFloats[o + 1] = n.Min.Y;
				nFloats[o + 2] = n.Min.Z;
				nInts[o + 3] = n.LeftOrFirst;
				nFloats[o + 4] = n.Max.X;
				nFloats[o + 5] = n.Max.Y;
				nFloats[o + 6] = n.Max.Z;
				nInts[o + 7] = n.Count;
			}
		}

		/// <summary>Writes triangles from <paramref name="first"/> into <paramref name="dest"/> (64 bytes a triangle).</summary>
		public void WriteGpuTriangles(int first, Span<byte> dest)
		{
			if (_nodeCount <= 0 || _triangles.Length == 0)
			{
				dest.Clear();
				return;
			}

			Span<float> tFloats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(dest);
			Span<uint> tUints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dest);
			int count = dest.Length / 64;
			for (int k = 0; k < count; k++)
			{
				ref readonly BvhTriangle t = ref _triangles[first + k];
				int o = k * 16;
				Vector3 edge1 = t.Edge1;
				Vector3 edge2 = t.Edge2;
				Vector3 normal = t.Normal;
				tFloats[o] = t.V0.X;
				tFloats[o + 1] = t.V0.Y;
				tFloats[o + 2] = t.V0.Z;
				tFloats[o + 3] = 0.0f;
				tFloats[o + 4] = edge1.X;
				tFloats[o + 5] = edge1.Y;
				tFloats[o + 6] = edge1.Z;
				tFloats[o + 7] = 0.0f;
				tFloats[o + 8] = edge2.X;
				tFloats[o + 9] = edge2.Y;
				tFloats[o + 10] = edge2.Z;
				tFloats[o + 11] = 0.0f;
				tFloats[o + 12] = normal.X;
				tFloats[o + 13] = normal.Y;
				tFloats[o + 14] = normal.Z;
				tUints[o + 15] = t.Flags;
			}
		}

		public BvhTree(List<BvhTriangle> sourceTriangles)
			: this(sourceTriangles.ToArray())
		{
		}

		/// <summary>Builds over <paramref name="triangles"/>, which the tree takes over and reorders.</summary>
		public BvhTree(BvhTriangle[] triangles)
		{
			_triangles = triangles;
			if (_triangles.Length == 0)
			{
				_nodes = Array.Empty<Node>();
				return;
			}

			_nodes = new Node[Math.Max(1, _triangles.Length * 2)];
			_nodeCount = 1;
			SubtreeSummary rootSummary = BuildRecursive(0, 0, _triangles.Length, 0);
			HasPropBounds = rootSummary.HasProps;
			PropMin = rootSummary.HasProps ? rootSummary.PropMin : Vector3.Zero;
			PropMax = rootSummary.HasProps ? rootSummary.PropMax : Vector3.Zero;

			// The build reserves the worst case, two nodes a triangle; give back what it did not use.
			if (_nodeCount < _nodes.Length)
			{
				Array.Resize(ref _nodes, _nodeCount);
			}
		}

		private SubtreeSummary MakeLeaf(int nodeIdx, int start, int count)
		{
			_nodes[nodeIdx].LeftOrFirst = start;
			_nodes[nodeIdx].Count = count;

			uint mask = 0u;
			bool hasProps = false;
			Vector3 pMin = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
			Vector3 pMax = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
			int end = start + count;
			for (int i = start; i < end; i++)
			{
				ref readonly BvhTriangle tri = ref _triangles[i];
				mask |= SubtreeBitForTriangleFlags(tri.Flags);
				if ((tri.Flags & BvhTriangle.FlagTerrain) == 0u)
				{
					hasProps = true;
					pMin = pMin.Min(tri.Min);
					pMax = pMax.Max(tri.Max);
				}
			}

			return new SubtreeSummary(mask, hasProps, pMin, pMax);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static float HalfSurfaceArea(Vector3 bMin, Vector3 bMax)
		{
			Vector3 d = bMax - bMin;
			if (d.X < 0.0f || d.Y < 0.0f || d.Z < 0.0f)
			{
				return 0.0f;
			}

			return (d.X * d.Y) + (d.Y * d.Z) + (d.Z * d.X);
		}

		private SubtreeSummary BuildRecursive(int nodeIdx, int start, int count, int depth)
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

			if (count <= 4 || depth >= 30)
			{
				return MakeLeaf(nodeIdx, start, count);
			}

			Vector3 extent = cMax - cMin;
			float maxExtent = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
			if (maxExtent <= 1e-6f)
			{
				if (count <= 8)
				{
					return MakeLeaf(nodeIdx, start, count);
				}

				int halfMid = start + (count >> 1);
				return SplitAtMid(nodeIdx, start, end, count, depth, halfMid);
			}

			int bestAxis = 0;
			if (extent.Y > extent.X && extent.Y >= extent.Z)
			{
				bestAxis = 1;
			}
			else if (extent.Z > extent.X && extent.Z >= extent.Y)
			{
				bestAxis = 2;
			}

			float bestSplitPos = bestAxis == 0
				? (cMin.X + cMax.X) * 0.5f
				: bestAxis == 1
					? (cMin.Y + cMax.Y) * 0.5f
					: (cMin.Z + cMax.Z) * 0.5f;

			// 8-bin Surface Area Heuristic (SAH) across active centroid axes to isolate large architectural/terrain
			// triangles from dense prop clusters, cutting GPU shadow and AO ray-node intersections by ~2-3x.
			if (count >= 10)
			{
				const int Bins = 8;
				Span<Vector3> binMin = stackalloc Vector3[Bins];
				Span<Vector3> binMax = stackalloc Vector3[Bins];
				Span<int> binCount = stackalloc int[Bins];
				Span<float> leftArea = stackalloc float[Bins - 1];
				Span<int> leftSum = stackalloc int[Bins - 1];

				float bestSahCost = float.PositiveInfinity;
				Vector3 posInf = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
				Vector3 negInf = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

				for (int candAxis = 0; candAxis < 3; candAxis++)
				{
					float axisExtent = candAxis == 0 ? extent.X : candAxis == 1 ? extent.Y : extent.Z;
					if (axisExtent < maxExtent * 0.35f || axisExtent <= 1e-5f)
					{
						continue;
					}

					float axisMin = candAxis == 0 ? cMin.X : candAxis == 1 ? cMin.Y : cMin.Z;
					float scale = Bins / axisExtent;

					for (int b = 0; b < Bins; b++)
					{
						binMin[b] = posInf;
						binMax[b] = negInf;
						binCount[b] = 0;
					}

					for (int i = start; i < end; i++)
					{
						ref readonly BvhTriangle tri = ref _triangles[i];
						float c = candAxis == 0 ? tri.Centroid.X : candAxis == 1 ? tri.Centroid.Y : tri.Centroid.Z;
						int bin = (int)((c - axisMin) * scale);
						if (bin < 0) bin = 0;
						else if (bin >= Bins) bin = Bins - 1;

						binCount[bin]++;
						binMin[bin] = binMin[bin].Min(tri.Min);
						binMax[bin] = binMax[bin].Max(tri.Max);
					}

					Vector3 curLMin = posInf;
					Vector3 curLMax = negInf;
					int curLCount = 0;
					for (int s = 0; s < Bins - 1; s++)
					{
						if (binCount[s] > 0)
						{
							curLCount += binCount[s];
							curLMin = curLMin.Min(binMin[s]);
							curLMax = curLMax.Max(binMax[s]);
						}

						leftSum[s] = curLCount;
						leftArea[s] = curLCount > 0 ? HalfSurfaceArea(curLMin, curLMax) : 0.0f;
					}

					Vector3 curRMin = posInf;
					Vector3 curRMax = negInf;
					int curRCount = 0;
					for (int s = Bins - 2; s >= 0; s--)
					{
						int rBin = s + 1;
						if (binCount[rBin] > 0)
						{
							curRCount += binCount[rBin];
							curRMin = curRMin.Min(binMin[rBin]);
							curRMax = curRMax.Max(binMax[rBin]);
						}

						int lCount = leftSum[s];
						if (lCount > 0 && curRCount > 0)
						{
							float rArea = HalfSurfaceArea(curRMin, curRMax);
							float cost = (leftArea[s] * lCount) + (rArea * curRCount);
							if (cost < bestSahCost)
							{
								bestSahCost = cost;
								bestAxis = candAxis;
								bestSplitPos = axisMin + ((s + 1) * (axisExtent * (1.0f / Bins)));
							}
						}
					}
				}

				if (count <= 6)
				{
					float parentArea = MathF.Max(HalfSurfaceArea(bMin, bMax), 1e-8f);
					float leafCost = parentArea * count;
					if (bestSahCost >= leafCost * 0.95f)
					{
						return MakeLeaf(nodeIdx, start, count);
					}
				}
			}

			int mid = start;
			for (int i = start; i < end; i++)
			{
				float c = bestAxis == 0
					? _triangles[i].Centroid.X
					: bestAxis == 1
						? _triangles[i].Centroid.Y
						: _triangles[i].Centroid.Z;
				if (c < bestSplitPos)
				{
					(_triangles[i], _triangles[mid]) = (_triangles[mid], _triangles[i]);
					mid++;
				}
			}

			if (mid == start || mid == end)
			{
				mid = start + (count >> 1);
			}

			return SplitAtMid(nodeIdx, start, end, count, depth, mid);
		}

		private SubtreeSummary SplitAtMid(int nodeIdx, int start, int end, int count, int depth, int mid)
		{
			int leftIdx = System.Threading.Interlocked.Add(ref _nodeCount, 2) - 2;
			if (leftIdx + 2 > _nodes.Length)
			{
				System.Threading.Interlocked.Add(ref _nodeCount, -2);
				return MakeLeaf(nodeIdx, start, count);
			}

			SubtreeSummary leftSummary = default;
			SubtreeSummary rightSummary = default;
			if (count >= 32768 && depth < 4 && System.Environment.ProcessorCount > 2)
			{
				Parallel.Invoke(
					() => leftSummary = BuildRecursive(leftIdx, start, mid - start, depth + 1),
					() => rightSummary = BuildRecursive(leftIdx + 1, mid, end - mid, depth + 1));
			}
			else
			{
				leftSummary = BuildRecursive(leftIdx, start, mid - start, depth + 1);
				rightSummary = BuildRecursive(leftIdx + 1, mid, end - mid, depth + 1);
			}

			uint combinedMask = leftSummary.Mask | rightSummary.Mask;
			_nodes[nodeIdx].LeftOrFirst = leftIdx;
			_nodes[nodeIdx].Count = -(int)combinedMask;

			bool hasProps = leftSummary.HasProps || rightSummary.HasProps;
			Vector3 pMin = leftSummary.HasProps && rightSummary.HasProps
				? leftSummary.PropMin.Min(rightSummary.PropMin)
				: leftSummary.HasProps ? leftSummary.PropMin : rightSummary.PropMin;
			Vector3 pMax = leftSummary.HasProps && rightSummary.HasProps
				? leftSummary.PropMax.Max(rightSummary.PropMax)
				: leftSummary.HasProps ? leftSummary.PropMax : rightSummary.PropMax;
			return new SubtreeSummary(combinedMask, hasProps, pMin, pMax);
		}

		/// <summary>
		/// Builds a compact cropped SAH <see cref="BvhTree"/> containing only AO-participating triangles
		/// (<c>FlagAoOccluder | FlagTerrain</c>) whose XZ bounds overlap the cropped AO rectangle expanded by
		/// <paramref name="margin"/> meters. Returns <see langword="this"/> when pruning saves fewer than 15% of triangles.
		/// </summary>
		public BvhTree BuildCroppedAoBvh(
			float cropOriginLocalX,
			float cropOriginLocalZ,
			float cropSpanX,
			float cropSpanZ,
			float margin)
		{
			if (_triangles.Length < 4096)
			{
				return this;
			}

			// World X is mirrored (-localX), so local [origin .. origin + span] maps to world [-(origin + span) .. -origin].
			float minWX = -(cropOriginLocalX + cropSpanX) - margin;
			float maxWX = -cropOriginLocalX + margin;
			float minWZ = cropOriginLocalZ - margin;
			float maxWZ = cropOriginLocalZ + cropSpanZ + margin;
			const uint aoMask = BvhTriangle.FlagAoOccluder | BvhTriangle.FlagTerrain;

			var kept = new List<BvhTriangle>(_triangles.Length / 2);
			for (int i = 0; i < _triangles.Length; i++)
			{
				ref readonly BvhTriangle tri = ref _triangles[i];
				if ((tri.Flags & aoMask) == 0u)
				{
					continue;
				}

				if (tri.Max.X < minWX || tri.Min.X > maxWX || tri.Max.Z < minWZ || tri.Min.Z > maxWZ)
				{
					continue;
				}

				kept.Add(tri);
			}

			if (kept.Count == 0 || kept.Count > (_triangles.Length * 85) / 100)
			{
				return this;
			}

			return new BvhTree(kept.ToArray());
		}

		/// <summary>
		/// Precomputes a <paramref name="gridRes"/>x<paramref name="gridRes"/> 2D XZ lookup table of
		/// <c>(aoSubtreeRoot, propColSubtreeRoot)</c> pairs over the cropped AO rectangle so the GPU compute
		/// shader never performs global top-down <c>find_local_root</c> walks per texel.
		/// </summary>
		public int[] BuildXzSubtreeGrid(
			float cropOriginLocalX,
			float cropOriginLocalZ,
			float cropSpanX,
			float cropSpanZ,
			float aoRadius,
			int gridRes = 256)
		{
			int res = Math.Clamp(gridRes, 8, 512);
			var grid = new int[res * res * 2];
			if (_triangles.Length == 0 || _nodeCount <= 0)
			{
				Array.Fill(grid, -1);
				return grid;
			}

			float minY = RootMin.Y - 1.0f;
			float maxY = RootMax.Y + 1.0f;
			float cellSpanX = MathF.Max(cropSpanX, 1e-4f) / res;
			float cellSpanZ = MathF.Max(cropSpanZ, 1e-4f) / res;
			float aoPad = MathF.Max(0.1f, aoRadius + 0.06f);

			Parallel.For(0, res, gz =>
			{
				float lz0 = cropOriginLocalZ + (gz * cellSpanZ);
				float lz1 = lz0 + cellSpanZ;
				int rowBase = gz * res * 2;
				for (int gx = 0; gx < res; gx++)
				{
					float lx0 = cropOriginLocalX + (gx * cellSpanX);
					float lx1 = lx0 + cellSpanX;
					float wx0 = -lx1;
					float wx1 = -lx0;

					int aoRoot = FindLocalSubtreeRoot(
						new Vector3(wx0 - aoPad, minY, lz0 - aoPad),
						new Vector3(wx1 + aoPad, maxY, lz1 + aoPad),
						SubtreeHasPropAo | SubtreeHasTerrainAo);
					int colRoot = aoRoot >= 0
						? FindLocalSubtreeRoot(
							new Vector3(wx0 - 0.04f, minY, lz0 - 0.04f),
							new Vector3(wx1 + 0.04f, maxY, lz1 + 0.04f),
							SubtreeHasPropAo)
						: -1;

					int cellIdx = rowBase + (gx * 2);
					grid[cellIdx] = aoRoot;
					grid[cellIdx + 1] = colRoot;
				}
			});

			return grid;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool OverlapsAabb(in Node node, Vector3 boxMin, Vector3 boxMax) =>
			node.Max.X >= boxMin.X && node.Min.X <= boxMax.X &&
			node.Max.Y >= boxMin.Y && node.Min.Y <= boxMax.Y &&
			node.Max.Z >= boxMin.Z && node.Min.Z <= boxMax.Z;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private bool NodeMatchesSubtreeMask(in Node node, uint requiredSubtreeMask)
		{
			if (requiredSubtreeMask == 0u)
			{
				return true;
			}

			if (node.Count <= 0)
			{
				return ((uint)(-node.Count) & requiredSubtreeMask) != 0u;
			}

			int end = node.LeftOrFirst + node.Count;
			for (int i = node.LeftOrFirst; i < end; i++)
			{
				if ((SubtreeBitForTriangleFlags(_triangles[i].Flags) & requiredSubtreeMask) != 0u)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Descends from the root while at most one child overlaps <c>[boxMin, boxMax]</c> and carries
		/// <paramref name="requiredSubtreeMask"/>. Returns <c>-1</c> if no matching triangles overlap the box,
		/// or the deepest single enclosing node index (<c>&gt;= 0</c>) from which all bounded rays can start.
		/// </summary>
		public int FindLocalSubtreeRoot(Vector3 boxMin, Vector3 boxMax, uint requiredSubtreeMask = 0u)
		{
			if (_triangles.Length == 0 || _nodeCount <= 0)
			{
				return -1;
			}

			if (!OverlapsAabb(in _nodes[0], boxMin, boxMax) || !NodeMatchesSubtreeMask(in _nodes[0], requiredSubtreeMask))
			{
				return -1;
			}

			int cur = 0;
			for (int step = 0; step < 64; step++)
			{
				ref readonly Node node = ref _nodes[cur];
				if (node.Count > 0)
				{
					return cur;
				}

				int leftIdx = node.LeftOrFirst;
				int rightIdx = leftIdx + 1;
				bool hitL = OverlapsAabb(in _nodes[leftIdx], boxMin, boxMax) &&
				            NodeMatchesSubtreeMask(in _nodes[leftIdx], requiredSubtreeMask);
				bool hitR = OverlapsAabb(in _nodes[rightIdx], boxMin, boxMax) &&
				            NodeMatchesSubtreeMask(in _nodes[rightIdx], requiredSubtreeMask);
				if (hitL && hitR)
				{
					return cur;
				}

				if (hitL)
				{
					cur = leftIdx;
					continue;
				}

				if (hitR)
				{
					cur = rightIdx;
					continue;
				}

				return -1;
			}

			return cur;
		}

		/// <summary>
		/// Collects all triangles whose AABB intersects the axis-aligned bounding box [boxMin, boxMax].
		/// Used to build a tiny per-light local BVH before tracing that light's octahedral tile.
		/// </summary>
		public void CollectInBounds(Vector3 boxMin, Vector3 boxMax, List<BvhTriangle> into, uint requiredFlags = 0u)
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
						if (requiredFlags != 0u && (tri.Flags & requiredFlags) != requiredFlags)
						{
							continue;
						}

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
		/// [tMin, tMax] and returns the closest hit distance and geometric surface normal for triangles
		/// satisfying <c>(tri.Flags &amp; flagMask) == flagExpected</c>, optionally starting at a narrowed
		/// <paramref name="startNode"/> and pruning internal nodes with <paramref name="requiredSubtreeMask"/>.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool TraceClosestHit(
			Vector3 origin,
			Vector3 dir,
			float tMin,
			float tMax,
			out float hitDist,
			out Vector3 hitNormal,
			uint flagMask = BvhTriangle.FlagShadowCaster,
			uint flagExpected = BvhTriangle.FlagShadowCaster,
			int startNode = 0,
			uint requiredSubtreeMask = 0u)
		{
			hitDist = tMax;
			hitNormal = Vector3.Up;
			if (_triangles.Length == 0 || _nodeCount <= 0 || startNode < 0 || startNode >= _nodeCount)
			{
				return false;
			}

			float invX = MathF.Abs(dir.X) > 1e-8f ? 1.0f / dir.X : MathF.CopySign(1e8f, dir.X);
			float invY = MathF.Abs(dir.Y) > 1e-8f ? 1.0f / dir.Y : MathF.CopySign(1e8f, dir.Y);
			float invZ = MathF.Abs(dir.Z) > 1e-8f ? 1.0f / dir.Z : MathF.CopySign(1e8f, dir.Z);

			if (!IntersectAabb(origin, invX, invY, invZ, _nodes[startNode].Min, _nodes[startNode].Max, tMin, hitDist, out _))
			{
				return false;
			}

			Span<int> stack = stackalloc int[64];
			Span<float> tstack = stackalloc float[64];
			int sp = 0;
			int nodeIdx = startNode;
			bool anyHit = false;

			while (true)
			{
				ref readonly Node node = ref _nodes[nodeIdx];
				if (node.Count > 0)
				{
					int end = node.LeftOrFirst + node.Count;
					for (int i = node.LeftOrFirst; i < end; i++)
					{
						ref readonly BvhTriangle tri = ref _triangles[i];
						if ((tri.Flags & flagMask) != flagExpected)
						{
							continue;
						}

						if (IntersectTriangleTwoSided(origin, dir, in tri, tMin, hitDist, out float t))
						{
							hitDist = t;
							hitNormal = tri.Normal;
							anyHit = true;
						}
					}
				}
				else
				{
					int leftIdx = node.LeftOrFirst;
					int rightIdx = leftIdx + 1;
					bool maskL = requiredSubtreeMask == 0u || NodeMatchesSubtreeMask(in _nodes[leftIdx], requiredSubtreeMask);
					bool maskR = requiredSubtreeMask == 0u || NodeMatchesSubtreeMask(in _nodes[rightIdx], requiredSubtreeMask);
					float tminL = 0.0f;
					float tminR = 0.0f;
					bool hitL = maskL && IntersectAabb(origin, invX, invY, invZ, _nodes[leftIdx].Min, _nodes[leftIdx].Max, tMin, hitDist, out tminL);
					bool hitR = maskR && IntersectAabb(origin, invX, invY, invZ, _nodes[rightIdx].Min, _nodes[rightIdx].Max, tMin, hitDist, out tminR);

					if (hitL && hitR)
					{
						if (tminL <= tminR)
						{
							if (sp < stack.Length)
							{
								stack[sp] = rightIdx;
								tstack[sp] = tminR;
								sp++;
							}
							nodeIdx = leftIdx;
						}
						else
						{
							if (sp < stack.Length)
							{
								stack[sp] = leftIdx;
								tstack[sp] = tminL;
								sp++;
							}
							nodeIdx = rightIdx;
						}
						continue;
					}

					if (hitL)
					{
						nodeIdx = leftIdx;
						continue;
					}

					if (hitR)
					{
						nodeIdx = rightIdx;
						continue;
					}
				}

				bool popped = false;
				while (sp > 0)
				{
					--sp;
					if (tstack[sp] < hitDist)
					{
						nodeIdx = stack[sp];
						popped = true;
						break;
					}
				}

				if (!popped)
				{
					break;
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
			float tMax,
			out float entryT)
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

			entryT = tmin;
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
			Vector3 edge1 = tri.Edge1;
			Vector3 edge2 = tri.Edge2;
			Vector3 pvec = dir.Cross(edge2);
			float det = edge1.Dot(pvec);
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

			Vector3 qvec = tvec.Cross(edge1);
			float v = dir.Dot(qvec) * invDet;
			if (v < 0.0f || u + v > 1.0f)
			{
				return false;
			}

			float hit = edge2.Dot(qvec) * invDet;
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
	/// Computes the tightest axis-aligned world bounding box enclosing every active ray emitted by
	/// <paramref name="light"/> (full sphere for omni lights, exact spherical-cap cone AABB for acute spotlights).
	/// </summary>
	public static void ComputeBakeLightBounds(in BakeLightSpec light, out Vector3 boxMin, out Vector3 boxMax)
	{
		float r = MathF.Max(0.0f, light.MaxRange);
		if (!light.IsSpot || light.SpotCosLimit <= 0.0f)
		{
			Vector3 rVec = new(r, r, r);
			boxMin = light.WorldOrigin - rVec;
			boxMax = light.WorldOrigin + rVec;
			return;
		}

		Vector3 fwd = light.SpotForward.Normalized();
		float cosTheta = Math.Clamp(light.SpotCosLimit, 0.0f, 1.0f);
		float sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
		const float pad = 0.05f;

		static void ConeAxisExtrema(float fa, float cosLimit, float sinLimit, out float minDir, out float maxDir)
		{
			maxDir = fa >= cosLimit ? 1.0f : MathF.Max(0.0f, fa * cosLimit + MathF.Sqrt(MathF.Max(0.0f, 1.0f - fa * fa)) * sinLimit);
			float negFa = -fa;
			float maxNeg = negFa >= cosLimit ? 1.0f : MathF.Max(0.0f, negFa * cosLimit + MathF.Sqrt(MathF.Max(0.0f, 1.0f - negFa * negFa)) * sinLimit);
			minDir = -maxNeg;
		}

		ConeAxisExtrema(fwd.X, cosTheta, sinTheta, out float minDx, out float maxDx);
		ConeAxisExtrema(fwd.Y, cosTheta, sinTheta, out float minDy, out float maxDy);
		ConeAxisExtrema(fwd.Z, cosTheta, sinTheta, out float minDz, out float maxDz);

		boxMin = new Vector3(
			light.WorldOrigin.X + r * minDx - pad,
			light.WorldOrigin.Y + r * minDy - pad,
			light.WorldOrigin.Z + r * minDz - pad);
		boxMax = new Vector3(
			light.WorldOrigin.X + r * maxDx + pad,
			light.WorldOrigin.Y + r * maxDy + pad,
			light.WorldOrigin.Z + r * maxDz + pad);
	}

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
		float interiorReachScale,
		Func<bool>? isCancelled = null) =>
		BakeFromBvh(worldBvh, lights, atlasResolution, lightsPerMap, placedReachScale, interiorReachScale, isCancelled, liveGameplayPaced: false, gpuSession: null);

	/// <summary>
	/// Bakes a <see cref="StaticShadowMapContainer"/> from a pre-built <see cref="BvhTree"/>, optionally sharing
	/// <paramref name="gpuSession"/> with a subsequent ground-AO pass and using gentle frame yields when
	/// <paramref name="liveGameplayPaced"/> is true.
	/// </summary>
	internal static StaticShadowMapContainer BakeFromBvh(
		BvhTree worldBvh,
		IReadOnlyList<BakeLightSpec> lights,
		int atlasResolution,
		int lightsPerMap,
		float placedReachScale,
		float interiorReachScale,
		Func<bool>? isCancelled,
		bool liveGameplayPaced,
		Engine_GpuBvhBaker.GpuBakeSession? gpuSession)
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

		if (!Refii.Core.Engine_AppRoot.IsTestHost &&
		    worldBvh.TriangleCount > 0 &&
		    Engine_GpuBvhBaker.TryBakeShadowAtlasGpu(
		        worldBvh,
		        lights,
		        atlasResolution,
		        lightsPerMap,
		        tilesPerRow,
		        tileTotalSize,
		        innerSize,
		        sliceTexelCount,
		        texelAngle,
		        texels,
		        isCancelled,
		        liveGameplayPaced,
		        gpuSession))
		{
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

		if (isCancelled?.Invoke() == true)
		{
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
			worldBvh.CollectInBounds(light.WorldOrigin - rangeVec, light.WorldOrigin + rangeVec, localTris, BvhTriangle.FlagShadowCaster);
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
	/// Builds the shared static world <see cref="BvhTree"/> and bake inputs directly from an already-parsed
	/// <see cref="Engine_LevelProps.StaticLightingBakeSnapshot"/> captured during <c>level-build</c>, avoiding
	/// duplicate GDB/GMD/GHF parsing while decoding <c>.mdl</c> caster meshes and assembling world triangles in parallel.
	/// </summary>
	public static bool TryBuildWorldGeometryFromSnapshot(
		LevelEntry level,
		Engine_LevelProps.StaticLightingBakeSnapshot snapshot,
		Func<string, byte[]?> read,
		Func<bool>? isCancelled,
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
		propBlocks = snapshot.PropBlocks;
		lightHosts = snapshot.LightHosts;
		fixtureModelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		bakeLights = new List<BakeLightSpec>();
		terrainHeights = new List<GhfHeights>();
		gdbPath = snapshot.GdbPath;
		triangleCount = 0;
		error = string.Empty;

		if (isCancelled?.Invoke() == true)
		{
			error = "cancelled";
			return false;
		}

		var terrainTriangles = new List<BvhTriangle>(65536);
		if (snapshot.TerrainHeights.Count != 0)
		{
			foreach (GhfHeights heights in snapshot.TerrainHeights)
			{
				terrainHeights.Add(heights);
				AppendDecodedTerrainTriangles(heights, terrainTriangles);
			}
		}
		else
		{
			Engine_LevelVfsConfig.Apply(level, read);
			Engine_LevelManifest.TryResolve(level, read, out ScenarioManifest? manifest, out _, out _);
			AppendTerrainTriangles(level, manifest, read, terrainTriangles, terrainHeights);
		}

		return AssembleWorldBvhFromResolvedData(
			propBlocks,
			lightHosts,
			snapshot.LightHostModels,
			snapshot.ResolvedLights,
			terrainTriangles,
			read,
			isCancelled,
			out worldBvh,
			out fixtureModelPaths,
			out bakeLights,
			out triangleCount,
			out error);
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

		var terrainTriangles = new List<BvhTriangle>(65536);
		AppendTerrainTriangles(level, manifest, read, terrainTriangles, terrainHeights);

		return AssembleWorldBvhFromResolvedData(
			propBlocks,
			lightHosts,
			lightHostModels,
			resolvedLights,
			terrainTriangles,
			read,
			isCancelled: null,
			out worldBvh,
			out fixtureModelPaths,
			out bakeLights,
			out triangleCount,
			out error);
	}

	private static bool AssembleWorldBvhFromResolvedData(
		List<Engine_LevelProps.StaticShadowPropBlock> propBlocks,
		HashSet<uint> lightHosts,
		HashSet<string> lightHostModels,
		List<Engine_LevelLights.ResolvedLightInstance> resolvedLights,
		List<BvhTriangle> terrainTriangles,
		Func<string, byte[]?> read,
		Func<bool>? isCancelled,
		out BvhTree? worldBvh,
		out HashSet<string> fixtureModelPaths,
		out List<BakeLightSpec> bakeLights,
		out int triangleCount,
		out string error)
	{
		worldBvh = null;
		fixtureModelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		bakeLights = new List<BakeLightSpec>(resolvedLights.Count);
		triangleCount = 0;
		error = string.Empty;

		// Filter to active placed lights and compute their world-space origins & ranges.
		foreach (Engine_LevelLights.ResolvedLightInstance item in resolvedLights)
		{
			if (item.Light.Range <= 0.05f || item.Light.Intensity <= 0.0f)
			{
				continue;
			}

			float maxRange = Engine_LevelLights.ComputeMaxBakeRange(item);
			Vector3 localOrigin = item.Transform.Origin;
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

		// Decode unique shadow-caster .mdl meshes in parallel across cores.
		int workerDop = Math.Max(1, System.Environment.ProcessorCount - 2);
		var uniqueCasterJobs = new Dictionary<string, (string Path, bool IsTree)>(StringComparer.OrdinalIgnoreCase);
		for (int b = 0; b < propBlocks.Count; b++)
		{
			Engine_LevelProps.StaticShadowPropBlock block = propBlocks[b];
			if (block.Foliage)
			{
				continue;
			}

			bool isTree = block.ModelClass == PropModelClass.Tree;
			string casterPath = isTree ? block.ShadowModelPath : block.ModelPath;
			if (casterPath.Length == 0)
			{
				continue;
			}

			string key = isTree ? $"tree_shadow|{casterPath}" : casterPath;
			uniqueCasterJobs.TryAdd(key, (casterPath, isTree));
		}

		if (uniqueCasterJobs.Count > 1 && workerDop > 1)
		{
			Parallel.ForEach(
				uniqueCasterJobs.Values,
				new ParallelOptions { MaxDegreeOfParallelism = workerDop },
				job =>
				{
					if (isCancelled?.Invoke() == true)
					{
						return;
					}

					_ = GetShadowCasterTriangles(job.Path, read, isTreeShadowModel: job.IsTree);
				});
		}

		if (isCancelled?.Invoke() == true)
		{
			error = "cancelled";
			return false;
		}

		var casterVerts = new Vector3[]?[propBlocks.Count];
		var blockTriOffsets = new int[propBlocks.Count];
		long total = terrainTriangles.Count;
		for (int b = 0; b < propBlocks.Count; b++)
		{
			Engine_LevelProps.StaticShadowPropBlock block = propBlocks[b];
			if (block.Foliage)
			{
				continue;
			}

			bool isTree = block.ModelClass == PropModelClass.Tree;
			string casterPath = isTree ? block.ShadowModelPath : block.ModelPath;
			if (casterPath.Length == 0)
			{
				continue;
			}

			Vector3[]? modelVerts = GetShadowCasterTriangles(casterPath, read, isTreeShadowModel: isTree);
			if (modelVerts is null || modelVerts.Length < 3)
			{
				continue;
			}

			casterVerts[b] = modelVerts;
			blockTriOffsets[b] = (int)total;
			total += (long)(modelVerts.Length / 3) * block.Instances.Count;
		}

		var worldTriangles = new BvhTriangle[total];
		terrainTriangles.CopyTo(worldTriangles);
		int terrainCount = terrainTriangles.Count;
		terrainTriangles.Clear();

		HashSet<string> localFixturePaths = fixtureModelPaths;
		Action<int> populateBlock = b =>
		{
			Vector3[]? modelVerts = casterVerts[b];
			if (modelVerts is null || isCancelled?.Invoke() == true)
			{
				return;
			}

			Engine_LevelProps.StaticShadowPropBlock block = propBlocks[b];
			bool isTree = block.ModelClass == PropModelClass.Tree;
			string casterPath = isTree ? block.ShadowModelPath : block.ModelPath;
			bool isFixtureModel = localFixturePaths.Contains(block.ModelPath) || IsLightFixtureModel(casterPath);
			int writeIdx = blockTriOffsets[b];
			int modelTriVerts = (modelVerts.Length / 3) * 3;

			for (int i = 0; i < block.Instances.Count; i++)
			{
				byte casting = i < block.InstanceCasting.Count
					? block.InstanceCasting[i]
					: Engine_ClassicShadows.CastsDefault;
				uint instanceHash = i < block.InstanceHashes.Count ? block.InstanceHashes[i] : 0u;
				bool castsLampShadow = casting != 0 &&
					!isFixtureModel &&
					!(instanceHash != 0u && lightHosts.Contains(instanceHash));
				uint triFlags = BvhTriangle.FlagAoOccluder | (castsLampShadow ? BvhTriangle.FlagShadowCaster : 0u);

				Transform3D inst = block.Instances[i];
				for (int v = 0; v < modelTriVerts; v += 3)
				{
					Vector3 a = MirrorX(inst * modelVerts[v]);
					Vector3 b2 = MirrorX(inst * modelVerts[v + 1]);
					Vector3 c = MirrorX(inst * modelVerts[v + 2]);
					worldTriangles[writeIdx++] = new BvhTriangle(a, b2, c, triFlags);
				}
			}
		};

		if (total - terrainCount >= 16384 && propBlocks.Count > 1 && workerDop > 1)
		{
			Parallel.For(0, propBlocks.Count, new ParallelOptions { MaxDegreeOfParallelism = workerDop }, populateBlock);
		}
		else
		{
			for (int b = 0; b < propBlocks.Count; b++)
			{
				populateBlock(b);
			}
		}

		if (isCancelled?.Invoke() == true)
		{
			error = "cancelled";
			return false;
		}

		triangleCount = (int)total;
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

	/// <summary>
	/// Returns true when <paramref name="path"/> refers to a distant non-playable vista backdrop heightfield
	/// (<c>/vistas/</c> or <c>\vistas\</c>) outside the playable region.
	/// </summary>
	public static bool IsVistaHeightfieldPath(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}

		return path.Contains("/vistas/", StringComparison.OrdinalIgnoreCase) ||
		       path.Contains("\\vistas\\", StringComparison.OrdinalIgnoreCase);
	}

	private static void AppendDecodedTerrainTriangles(GhfHeights heights, List<BvhTriangle> into)
	{
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
			into.Add(new BvhTriangle(a, b, c, BvhTriangle.FlagShadowCaster | BvhTriangle.FlagTerrain | BvhTriangle.FlagAoOccluder));
		}
	}

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
				if (Engine_LevelManifest.TryResolvePath(line, read, out string resolved) &&
				    !IsVistaHeightfieldPath(resolved))
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
			AppendDecodedTerrainTriangles(heights, into);
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

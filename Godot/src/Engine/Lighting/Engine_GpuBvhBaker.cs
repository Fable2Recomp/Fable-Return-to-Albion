using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Refii.Core;
using Refii.Level;

namespace Refii.Lighting;

/// <summary>
/// GPU compute BVH ray-tracer for on-demand static lamp shadow atlases (<see cref="Engine_LevelShadowMaps"/>)
/// and high-resolution cropped surface-aware ground contact AO maps (<see cref="Engine_LevelAmbientOcclusion"/>).
/// Executes entirely on a worker-owned local <see cref="RenderingDevice"/> off the main thread so scene loading
/// and live rendering never stall in <c>gpu_upload_pump</c>.
/// </summary>
public static class Engine_GpuBvhBaker
{
	private static readonly object SyncRoot = new();
	private static readonly object SpirvCacheLock = new();

	private static byte[]? _cachedShadowSpirv;
	private static byte[]? _cachedGroundAoSpirv;
	private static byte[]? _cachedGroundAoFilterSpirv;

	private static int _gpuShadowBakeCount;
	private static int _gpuAoBakeCount;
	private static int _cpuFallbackCount;

	/// <summary>Total number of shadow-atlas bakes completed on the GPU in this process.</summary>
	public static int GpuShadowBakeCount => Volatile.Read(ref _gpuShadowBakeCount);

	/// <summary>Total number of ground-AO bakes completed on the GPU in this process.</summary>
	public static int GpuAoBakeCount => Volatile.Read(ref _gpuAoBakeCount);

	/// <summary>Total number of GPU bakes that fell back to CPU ray-tracing.</summary>
	public static int CpuFallbackCount => Volatile.Read(ref _cpuFallbackCount);

	/// <summary>Bytes a BVH goes up to the GPU in at a time.</summary>
	private const int BvhUploadChunkBytes = 4 * 1024 * 1024;

	/// <summary>Target GPU dispatch duration (ms) when baking behind an active loading cover.</summary>
	public const double LoadCoverTargetSliceMs = 160.0;

	/// <summary>Target GPU dispatch duration (ms) when baking live during gameplay (e.g. CVar reload).</summary>
	public const double LiveGameplayTargetSliceMs = 6.0;

	/// <summary>Resolution of the 2D XZ local-subtree root lookup grid uploaded for GPU ground AO bakes.</summary>
	public const int XzGridResolution = 256;

	/// <summary>
	/// Reusable worker-thread GPU compute session holding one local <see cref="RenderingDevice"/>
	/// and uploaded world BVH buffers across both shadow-atlas and ground-AO bakes for a scenario.
	/// </summary>
	internal sealed class GpuBakeSession : IDisposable
	{
		private bool _disposed;

		/// <summary>Worker-owned local rendering device.</summary>
		public RenderingDevice Device { get; }

		/// <summary>Uploaded BVH node storage buffer.</summary>
		public Rid NodeBuffer { get; }

		/// <summary>Uploaded BVH triangle storage buffer.</summary>
		public Rid TriangleBuffer { get; }

		internal GpuBakeSession(RenderingDevice device, Rid nodeBuffer, Rid triangleBuffer)
		{
			Device = device;
			NodeBuffer = nodeBuffer;
			TriangleBuffer = triangleBuffer;
		}

		/// <summary>Releases the uploaded BVH buffers and frees the underlying local <see cref="RenderingDevice"/>.</summary>
		public void Dispose()
		{
			lock (SyncRoot)
			{
				if (_disposed)
				{
					return;
				}

				_disposed = true;
				FreeIfValid(Device, TriangleBuffer);
				FreeIfValid(Device, NodeBuffer);
				try
				{
					Device.Free();
				}
				catch
				{
				}
			}
		}
	}

	/// <summary>
	/// Creates a shared <see cref="GpuBakeSession"/> on the calling worker thread when supported by the
	/// current platform (<see cref="Refii.AndroidRuntime.AndroidBakedShadows.SharesLocalBakeDevice"/>).
	/// </summary>
	internal static GpuBakeSession? TryCreateSession(Engine_LevelShadowMaps.BvhTree worldBvh)
	{
		if (worldBvh.TriangleCount <= 0 ||
		    Engine_AppRoot.IsTestHost ||
		    Engine_RdHealth.IsDeviceLost ||
		    !Refii.AndroidRuntime.AndroidBakedShadows.SharesLocalBakeDevice)
		{
			return null;
		}

		lock (SyncRoot)
		{
			if (Engine_RdHealth.IsDeviceLost)
			{
				return null;
			}

			RenderingDevice? rd = null;
			Rid nodeBuf = default;
			Rid triBuf = default;
			try
			{
				rd = RenderingServer.CreateLocalRenderingDevice();
				if (rd is null)
				{
					return null;
				}

				UploadBvh(rd, worldBvh, out nodeBuf, out triBuf);
				if (!nodeBuf.IsValid || !triBuf.IsValid)
				{
					FreeIfValid(rd, triBuf);
					FreeIfValid(rd, nodeBuf);
					rd.Free();
					return null;
				}

				return new GpuBakeSession(rd, nodeBuf, triBuf);
			}
			catch
			{
				if (rd is not null)
				{
					FreeIfValid(rd, triBuf);
					FreeIfValid(rd, nodeBuf);
					try { rd.Free(); } catch { }
				}

				return null;
			}
		}
	}

	/// <summary>
	/// Computes the next adaptive GPU dispatch batch size from the measured wall-time of the previous slice,
	/// clamping scaling steps so sudden sparse or dense regions transition smoothly without overshooting.
	/// </summary>
	public static int ComputeNextSliceUnits(
		int currentUnits,
		double elapsedMs,
		double targetSliceMs,
		int minUnits,
		int maxUnits)
	{
		int safeMin = Math.Max(1, minUnits);
		int safeMax = Math.Max(safeMin, maxUnits);
		int clampedCurrent = Math.Clamp(currentUnits, safeMin, safeMax);
		if (targetSliceMs <= 0.1)
		{
			return safeMin;
		}

		if (elapsedMs <= 0.05)
		{
			return Math.Clamp(clampedCurrent * 2, safeMin, safeMax);
		}

		double ratio = Math.Clamp(targetSliceMs / elapsedMs, 0.25, 2.0);
		int next = (int)Math.Round(clampedCurrent * ratio);
		return Math.Clamp(next, safeMin, safeMax);
	}

	/// <summary>
	/// Computes how many rendered frames a live-gameplay background GPU bake should yield after a slice
	/// that took <paramref name="elapsedMs"/> milliseconds.
	/// </summary>
	public static int ComputeYieldFrames(double elapsedMs, double targetSliceMs = LiveGameplayTargetSliceMs)
	{
		double budget = Math.Max(1.0, targetSliceMs);
		if (elapsedMs <= budget * 1.5)
		{
			return 1;
		}

		if (elapsedMs <= budget * 3.0)
		{
			return 2;
		}

		return 3;
	}

	/// <summary>
	/// Puts a tree's nodes and triangles on <paramref name="rd"/> a few megabytes at a time. Exporting each
	/// whole buffer first held it twice more (the array and its native copy) next to the GPU's own: in a
	/// 5.7-million-triangle area that was over 700 MB at the peak, and a phone stopped the bake for memory.
	/// </summary>
	private static void UploadBvh(RenderingDevice rd, Engine_LevelShadowMaps.BvhTree bvh, out Rid nodeBuf, out Rid triBuf)
	{
		int nodeBytes = bvh.GpuNodeBytes;
		int triBytes = bvh.GpuTriangleBytes;
		nodeBuf = rd.StorageBufferCreate((uint)nodeBytes);
		triBuf = rd.StorageBufferCreate((uint)triBytes);
		byte[] chunk = new byte[Math.Min(BvhUploadChunkBytes, Math.Max(nodeBytes, triBytes))];
		UploadInChunks(rd, nodeBuf, nodeBytes, 32, chunk, bvh.WriteGpuNodes);
		UploadInChunks(rd, triBuf, triBytes, 64, chunk, bvh.WriteGpuTriangles);
	}

	private delegate void ChunkWriter(int first, Span<byte> dest);

	private static void UploadInChunks(RenderingDevice rd, Rid buffer, int totalBytes, int stride, byte[] chunk, ChunkWriter write)
	{
		int perChunk = Math.Max(1, chunk.Length / stride);
		int items = totalBytes / stride;
		for (int first = 0; first < items; first += perChunk)
		{
			int count = Math.Min(perChunk, items - first);
			int bytes = count * stride;
			byte[] block = bytes == chunk.Length ? chunk : new byte[bytes];
			write(first, block);
			rd.BufferUpdate(buffer, (uint)(first * stride), (uint)bytes, block);
		}
	}

	private static Rid CreateComputeShaderCached(
		RenderingDevice rd,
		string glslSource,
		string shaderName,
		ref byte[]? bytecodeCache,
		out string? compileError)
	{
		compileError = null;
		byte[]? cached;
		lock (SpirvCacheLock)
		{
			cached = bytecodeCache;
		}

		if (cached is { Length: > 0 })
		{
			using var cachedSpirv = new RDShaderSpirV
			{
				BytecodeCompute = cached,
			};
			Rid fromCache = rd.Shader(cachedSpirv, shaderName);
			if (fromCache.IsValid)
			{
				return fromCache;
			}
		}

		using RDShaderSpirV compiled = rd.CompileSpirV(new RDShaderSource
		{
			SourceCompute = glslSource,
			Language = RenderingDevice.ShaderLanguage.Glsl,
		});
		if (!string.IsNullOrEmpty(compiled.CompileErrorCompute))
		{
			compileError = compiled.CompileErrorCompute;
			return default;
		}

		byte[] freshBytes = compiled.BytecodeCompute;
		if (freshBytes is { Length: > 0 })
		{
			lock (SpirvCacheLock)
			{
				bytecodeCache ??= freshBytes;
			}
		}

		return rd.Shader(compiled, shaderName);
	}

	/// <summary>GLSL 450 compute shader for baking octahedral static lamp shadow tiles.</summary>
	public const string ShadowAtlasShaderSource = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict readonly buffer BvhNodes {
	vec4 nodes[];
} bvh_nodes;

layout(set = 0, binding = 1, std430) restrict readonly buffer BvhTris {
	vec4 tris[];
} bvh_tris;

layout(set = 0, binding = 2, std430) restrict readonly buffer LightSpecs {
	vec4 lights[];
} light_specs;

layout(set = 0, binding = 3, std430) restrict writeonly buffer OutDepth {
	uint depth_bits[];
} out_buf;

layout(push_constant, std430) uniform Push {
	int inner_size;
	int batch_count;
	int row_offset;
	int row_count;
	float inv_inner;
	float texel_angle;
	int pad0;
	int pad1;
} push;

vec3 direction_from_octahedral_uv(vec2 uv) {
	vec2 o = uv * 2.0 - 1.0;
	vec3 n = vec3(o.x, o.y, 1.0 - abs(o.x) - abs(o.y));
	if (n.z < 0.0) {
		float ox = (1.0 - abs(o.y)) * (o.x >= 0.0 ? 1.0 : -1.0);
		float oy = (1.0 - abs(o.x)) * (o.y >= 0.0 ? 1.0 : -1.0);
		n.x = ox;
		n.y = oy;
	}
	float len_sq = dot(n, n);
	return len_sq > 1e-12 ? n * inversesqrt(len_sq) : vec3(0.0, 0.0, 1.0);
}

bool intersect_aabb(vec3 origin, vec3 inv_dir, vec3 bmin, vec3 bmax, float t_min, float t_max, out float entry_t) {
	vec3 t1 = (bmin - origin) * inv_dir;
	vec3 t2 = (bmax - origin) * inv_dir;
	vec3 tmin3 = min(t1, t2);
	vec3 tmax3 = max(t1, t2);
	float tmin = max(max(t_min, tmin3.x), max(tmin3.y, tmin3.z));
	float tmax = min(min(t_max, tmax3.x), min(tmax3.y, tmax3.z));
	entry_t = tmin;
	return tmax >= tmin;
}

bool trace_closest_hit(int start_node, vec3 origin, vec3 dir, float t_min, float t_max, out float hit_dist, out vec3 hit_normal) {
	hit_dist = t_max;
	hit_normal = vec3(0.0, 1.0, 0.0);
	if (start_node < 0) {
		return false;
	}

	vec3 inv_dir = vec3(
		abs(dir.x) > 1e-8 ? 1.0 / dir.x : (dir.x >= 0.0 ? 1e8 : -1e8),
		abs(dir.y) > 1e-8 ? 1.0 / dir.y : (dir.y >= 0.0 ? 1e8 : -1e8),
		abs(dir.z) > 1e-8 ? 1.0 / dir.z : (dir.z >= 0.0 ? 1e8 : -1e8));

	float root_tmin;
	if (!intersect_aabb(origin, inv_dir, bvh_nodes.nodes[start_node * 2].xyz, bvh_nodes.nodes[start_node * 2 + 1].xyz, t_min, hit_dist, root_tmin)) {
		return false;
	}

	int stack[32];
	int sp = 0;
	int node_idx = start_node;
	bool any_hit = false;
	bool from_pop = false;

	while (true) {
		vec4 n0 = bvh_nodes.nodes[node_idx * 2];
		vec4 n1 = bvh_nodes.nodes[node_idx * 2 + 1];
		float pop_tmin;
		if (from_pop && any_hit && (!intersect_aabb(origin, inv_dir, n0.xyz, n1.xyz, t_min, hit_dist, pop_tmin) || pop_tmin >= hit_dist)) {
			if (sp <= 0) {
				break;
			}
			node_idx = stack[--sp];
			from_pop = true;
			continue;
		}
		from_pop = false;

		int left_or_first = floatBitsToInt(n0.w);
		int count = floatBitsToInt(n1.w);
		if (count > 0) {
			int end = left_or_first + count;
			for (int i = left_or_first; i < end; ++i) {
				int to = i * 4;
				vec4 n_and_flags = bvh_tris.tris[to + 3];
				if ((floatBitsToUint(n_and_flags.w) & 1u) == 0u) {
					continue;
				}
				vec3 v0 = bvh_tris.tris[to].xyz;
				vec3 e1 = bvh_tris.tris[to + 1].xyz;
				vec3 e2 = bvh_tris.tris[to + 2].xyz;
				vec3 pvec = cross(dir, e2);
				float det = dot(e1, pvec);
				if (abs(det) < 1e-8) {
					continue;
				}
				float inv_det = 1.0 / det;
				vec3 tvec = origin - v0;
				float u = dot(tvec, pvec) * inv_det;
				if (u < 0.0 || u > 1.0) {
					continue;
				}
				vec3 qvec = cross(tvec, e1);
				float v = dot(dir, qvec) * inv_det;
				if (v < 0.0 || (u + v) > 1.0) {
					continue;
				}
				float t = dot(e2, qvec) * inv_det;
				if (t > t_min && t < hit_dist) {
					hit_dist = t;
					hit_normal = n_and_flags.xyz;
					any_hit = true;
				}
			}
		} else {
			int left_idx = left_or_first;
			int right_idx = left_or_first + 1;
			vec4 ln0 = bvh_nodes.nodes[left_idx * 2];
			vec4 ln1 = bvh_nodes.nodes[left_idx * 2 + 1];
			vec4 rn0 = bvh_nodes.nodes[right_idx * 2];
			vec4 rn1 = bvh_nodes.nodes[right_idx * 2 + 1];
			int lc = floatBitsToInt(ln1.w);
			int rc = floatBitsToInt(rn1.w);
			bool mask_l = (lc > 0) || ((uint(-lc) & 1u) != 0u);
			bool mask_r = (rc > 0) || ((uint(-rc) & 1u) != 0u);
			float tmin_l = 0.0;
			float tmin_r = 0.0;
			bool hit_l = mask_l && intersect_aabb(origin, inv_dir, ln0.xyz, ln1.xyz, t_min, hit_dist, tmin_l);
			bool hit_r = mask_r && intersect_aabb(origin, inv_dir, rn0.xyz, rn1.xyz, t_min, hit_dist, tmin_r);
			if (hit_l && hit_r) {
				if (tmin_l <= tmin_r) {
					if (sp < 32) {
						stack[sp++] = right_idx;
					}
					node_idx = left_idx;
				} else {
					if (sp < 32) {
						stack[sp++] = left_idx;
					}
					node_idx = right_idx;
				}
				continue;
			}
			if (hit_l) {
				node_idx = left_idx;
				continue;
			}
			if (hit_r) {
				node_idx = right_idx;
				continue;
			}
		}

		if (sp <= 0) {
			break;
		}
		node_idx = stack[--sp];
		from_pop = true;
	}

	return any_hit;
}

vec2 wrap_octahedral_uv(vec2 uv) {
	float u = uv.x;
	float v = uv.y;
	if (u < 0.0) {
		u = -u;
		v = 1.0 - v;
	} else if (u > 1.0) {
		u = 2.0 - u;
		v = 1.0 - v;
	}
	if (v < 0.0) {
		v = -v;
		u = 1.0 - u;
	} else if (v > 1.0) {
		v = 2.0 - v;
		u = 1.0 - u;
	}
	return clamp(vec2(u, v), vec2(0.0), vec2(1.0));
}

void main() {
	int tx = int(gl_GlobalInvocationID.x);
	int local_ty = int(gl_GlobalInvocationID.y);
	int batch_idx = int(gl_GlobalInvocationID.z);
	int tile_total = push.pad0;
	if (tx >= tile_total || local_ty >= push.row_count || batch_idx >= push.batch_count) {
		return;
	}

	int ty = push.row_offset + local_ty;
	int out_idx = (batch_idx * push.row_count + local_ty) * tile_total + tx;
	vec4 l0 = light_specs.lights[batch_idx * 3];
	vec4 l1 = light_specs.lights[batch_idx * 3 + 1];
	vec4 l2 = light_specs.lights[batch_idx * 3 + 2];
	int start_node = floatBitsToInt(l2.x);
	if (start_node < 0) {
		out_buf.depth_bits[out_idx] = 0x7BFFu;
		return;
	}

	vec3 origin = l0.xyz;
	float max_range = l0.w;
	vec3 spot_fwd = l1.xyz;
	float spot_cos = l1.w;

	float raw_u = (float(tx - 1) + 0.5) * push.inv_inner;
	float raw_v = (float(ty - 1) + 0.5) * push.inv_inner;
	float u = raw_u;
	float v = raw_v;
	if (tx == 0 || tx == tile_total - 1 || ty == 0 || ty == tile_total - 1) {
		vec2 wrapped = wrap_octahedral_uv(vec2(raw_u, raw_v));
		float f_inner = float(push.inner_size);
		float src_ix = clamp(floor(wrapped.x * f_inner), 0.0, f_inner - 1.0);
		float src_iy = clamp(floor(wrapped.y * f_inner), 0.0, f_inner - 1.0);
		u = (src_ix + 0.5) * push.inv_inner;
		v = (src_iy + 0.5) * push.inv_inner;
	}

	vec3 dir = direction_from_octahedral_uv(vec2(u, v));

	if (spot_cos > -1.5 && dot(dir, spot_fwd) < spot_cos) {
		out_buf.depth_bits[out_idx] = 0x7BFFu;
		return;
	}

	float hit_dist;
	vec3 hit_normal;
	if (trace_closest_hit(start_node, origin, dir, 0.20, max_range, hit_dist, hit_normal)) {
		float cos_theta = abs(dot(hit_normal, dir));
		float sin_theta = sqrt(max(0.0, 1.0 - cos_theta * cos_theta));
		float tan_theta = min(sin_theta / max(cos_theta, 0.08), 8.0);
		float biased = min(hit_dist + 0.025 + hit_dist * push.texel_angle * (0.75 + tan_theta), 65504.0);
		out_buf.depth_bits[out_idx] = packHalf2x16(vec2(biased, 0.0)) & 0xFFFFu;
	} else {
		out_buf.depth_bits[out_idx] = 0x7BFFu;
	}
}
";

	/// <summary>GLSL 450 compute shader for baking surface-aware ground and walkable-floor contact AO.</summary>
	public const string GroundAoShaderSource = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict readonly buffer BvhNodes {
	vec4 nodes[];
} bvh_nodes;

layout(set = 0, binding = 1, std430) restrict readonly buffer BvhTris {
	vec4 tris[];
} bvh_tris;

layout(set = 0, binding = 2, std430) restrict readonly buffer GhfHeightsBuf {
	float values[];
} ghf_buf;

layout(set = 0, binding = 3, std430) restrict writeonly buffer MidAo {
	vec4 samples[];
} out_buf;

layout(set = 0, binding = 4, std430) restrict readonly buffer XzGridBuf {
	ivec2 cells[];
} xz_grid;

layout(push_constant, std430) uniform Push {
	int terrain_w;
	int terrain_h;
	int row_offset;
	int halo_h;
	int ghf_w;
	int ghf_h;
	float ghf_origin_x;
	float ghf_origin_z;
	float ghf_spacing;
	float crop_origin_x;
	float crop_origin_z;
	float crop_span_x;
	float crop_span_z;
	float max_radius;
	float min_ao;
	int col_offset;
} push;

const vec3 HEMISPHERE_KERNEL[12] = vec3[12](
	vec3(0.07396608, 0.19025128, 0.97894501),
	vec3(-0.31707036, -0.15648128, 0.93541435),
	vec3(0.43825210, -0.12758565, 0.88975652),
	vec3(-0.28047316, 0.46153827, 0.84162541),
	vec3(-0.11906867, -0.60068515, 0.79056942),
	vec3(0.54718928, 0.39881150, 0.73598007),
	vec3(-0.73545516, 0.02779112, 0.67700320),
	vec3(0.52198574, -0.59365889, 0.61237244),
	vec3(0.01701476, 0.84145340, 0.54006172),
	vec3(-0.67103604, -0.58478926, 0.45643546),
	vec3(0.93334901, 0.06212582, 0.35355339),
	vec3(-0.62583606, 0.75263708, 0.20412415)
);

void build_orthonormal_basis(vec3 n, out vec3 tangent, out vec3 bitangent) {
	if (n.z < -0.999999) {
		tangent = vec3(0.0, -1.0, 0.0);
		bitangent = vec3(-1.0, 0.0, 0.0);
		return;
	}
	float a = 1.0 / (1.0 + n.z);
	float b = -n.x * n.y * a;
	tangent = vec3(1.0 - n.x * n.x * a, b, -n.x);
	bitangent = vec3(b, 1.0 - n.y * n.y * a, -n.y);
}

float read_ghf(int x, int z) {
	int cx = clamp(x, 0, push.ghf_w - 1);
	int cz = clamp(z, 0, push.ghf_h - 1);
	return ghf_buf.values[cz * push.ghf_w + cx];
}

void sample_height_and_normal(float local_x, float local_z, out float height, out vec3 world_normal) {
	float inv_span_x = 1.0 / max(float(push.ghf_w - 1) * push.ghf_spacing, 1e-5);
	float inv_span_z = 1.0 / max(float(push.ghf_h - 1) * push.ghf_spacing, 1e-5);
	float u = clamp((local_x - push.ghf_origin_x) * inv_span_x, 0.0, 1.0);
	float v = clamp((local_z - push.ghf_origin_z) * inv_span_z, 0.0, 1.0);

	float fx = u * float(push.ghf_w - 1);
	float fz = v * float(push.ghf_h - 1);
	int x0 = clamp(int(floor(fx)), 0, push.ghf_w - 1);
	int z0 = clamp(int(floor(fz)), 0, push.ghf_h - 1);
	int x1 = min(x0 + 1, push.ghf_w - 1);
	int z1 = min(z0 + 1, push.ghf_h - 1);
	float tx = fx - float(x0);
	float tz = fz - float(z0);

	float h00 = read_ghf(x0, z0);
	float h10 = read_ghf(x1, z0);
	float h01 = read_ghf(x0, z1);
	float h11 = read_ghf(x1, z1);
	height = mix(mix(h00, h10, tx), mix(h01, h11, tx), tz);

	int xm = max(x0 - 1, 0);
	int xp = min(x0 + 1, push.ghf_w - 1);
	int zm = max(z0 - 1, 0);
	int zp = min(z0 + 1, push.ghf_h - 1);
	float dhdx = (read_ghf(xp, z0) - read_ghf(xm, z0)) / (float(max(1, xp - xm)) * max(push.ghf_spacing, 1e-5));
	float dhdz = (read_ghf(x0, zp) - read_ghf(x0, zm)) / (float(max(1, zp - zm)) * max(push.ghf_spacing, 1e-5));

	vec3 n = vec3(dhdx, 1.0, -dhdz);
	world_normal = normalize(n);
}

bool overlaps_box(vec3 nmin, vec3 nmax, vec3 bmin, vec3 bmax) {
	return nmax.x >= bmin.x && nmin.x <= bmax.x &&
	       nmax.y >= bmin.y && nmin.y <= bmax.y &&
	       nmax.z >= bmin.z && nmin.z <= bmax.z;
}

int narrow_local_root(int start_node, vec3 box_min, vec3 box_max, uint req_mask) {
	if (start_node < 0) {
		return -1;
	}
	vec4 r0 = bvh_nodes.nodes[start_node * 2];
	vec4 r1 = bvh_nodes.nodes[start_node * 2 + 1];
	int r_count = floatBitsToInt(r1.w);
	if (!overlaps_box(r0.xyz, r1.xyz, box_min, box_max)) {
		return -1;
	}
	if (r_count <= 0 && (uint(-r_count) & req_mask) == 0u) {
		return -1;
	}

	int cur = start_node;
	for (int step = 0; step < 24; ++step) {
		vec4 n0 = bvh_nodes.nodes[cur * 2];
		vec4 n1 = bvh_nodes.nodes[cur * 2 + 1];
		int left_or_first = floatBitsToInt(n0.w);
		int count = floatBitsToInt(n1.w);
		if (count > 0) {
			int end = left_or_first + count;
			for (int i = left_or_first; i < end; ++i) {
				uint tf = floatBitsToUint(bvh_tris.tris[i * 4 + 3].w);
				uint tmask = ((tf & 1u) != 0u ? 1u : 0u) | ((tf & 4u) != 0u ? (((tf & 2u) != 0u) ? 2u : 4u) : 0u);
				if ((tmask & req_mask) != 0u) {
					return cur;
				}
			}
			return -1;
		}

		int left_idx = left_or_first;
		int right_idx = left_or_first + 1;
		vec4 ln0 = bvh_nodes.nodes[left_idx * 2];
		vec4 ln1 = bvh_nodes.nodes[left_idx * 2 + 1];
		vec4 rn0 = bvh_nodes.nodes[right_idx * 2];
		vec4 rn1 = bvh_nodes.nodes[right_idx * 2 + 1];
		int lc = floatBitsToInt(ln1.w);
		int rc = floatBitsToInt(rn1.w);
		bool hit_l = ((lc > 0) || ((uint(-lc) & req_mask) != 0u)) && overlaps_box(ln0.xyz, ln1.xyz, box_min, box_max);
		bool hit_r = ((rc > 0) || ((uint(-rc) & req_mask) != 0u)) && overlaps_box(rn0.xyz, rn1.xyz, box_min, box_max);
		if (hit_l && hit_r) {
			return cur;
		}
		if (hit_l) {
			cur = left_idx;
			continue;
		}
		if (hit_r) {
			cur = right_idx;
			continue;
		}
		return -1;
	}

	return cur;
}

bool intersect_aabb(vec3 origin, vec3 inv_dir, vec3 bmin, vec3 bmax, float t_min, float t_max, out float entry_t) {
	vec3 t1 = (bmin - origin) * inv_dir;
	vec3 t2 = (bmax - origin) * inv_dir;
	vec3 tmin3 = min(t1, t2);
	vec3 tmax3 = max(t1, t2);
	float tmin = max(max(t_min, tmin3.x), max(tmin3.y, tmin3.z));
	float tmax = min(min(t_max, tmax3.x), min(tmax3.y, tmax3.z));
	entry_t = tmin;
	return tmax >= tmin;
}

bool trace_any_hit(int start_node, uint req_subtree_mask, vec3 origin, vec3 dir, float t_min, float t_max, uint flag_mask, uint flag_expected) {
	if (start_node < 0) {
		return false;
	}

	vec3 inv_dir = vec3(
		abs(dir.x) > 1e-8 ? 1.0 / dir.x : (dir.x >= 0.0 ? 1e8 : -1e8),
		abs(dir.y) > 1e-8 ? 1.0 / dir.y : (dir.y >= 0.0 ? 1e8 : -1e8),
		abs(dir.z) > 1e-8 ? 1.0 / dir.z : (dir.z >= 0.0 ? 1e8 : -1e8));

	float root_tmin;
	if (!intersect_aabb(origin, inv_dir, bvh_nodes.nodes[start_node * 2].xyz, bvh_nodes.nodes[start_node * 2 + 1].xyz, t_min, t_max, root_tmin)) {
		return false;
	}

	int stack[32];
	int sp = 0;
	int node_idx = start_node;

	while (true) {
		vec4 n0 = bvh_nodes.nodes[node_idx * 2];
		vec4 n1 = bvh_nodes.nodes[node_idx * 2 + 1];
		int left_or_first = floatBitsToInt(n0.w);
		int count = floatBitsToInt(n1.w);
		if (count > 0) {
			int end = left_or_first + count;
			for (int i = left_or_first; i < end; ++i) {
				int to = i * 4;
				vec4 n_and_flags = bvh_tris.tris[to + 3];
				if ((floatBitsToUint(n_and_flags.w) & flag_mask) != flag_expected) {
					continue;
				}
				vec3 v0 = bvh_tris.tris[to].xyz;
				vec3 e1 = bvh_tris.tris[to + 1].xyz;
				vec3 e2 = bvh_tris.tris[to + 2].xyz;
				vec3 pvec = cross(dir, e2);
				float det = dot(e1, pvec);
				if (abs(det) < 1e-8) {
					continue;
				}
				float inv_det = 1.0 / det;
				vec3 tvec = origin - v0;
				float u = dot(tvec, pvec) * inv_det;
				if (u < 0.0 || u > 1.0) {
					continue;
				}
				vec3 qvec = cross(tvec, e1);
				float v = dot(dir, qvec) * inv_det;
				if (v < 0.0 || (u + v) > 1.0) {
					continue;
				}
				float t = dot(e2, qvec) * inv_det;
				if (t > t_min && t < t_max) {
					return true;
				}
			}
		} else {
			int left_idx = left_or_first;
			int right_idx = left_or_first + 1;
			vec4 ln0 = bvh_nodes.nodes[left_idx * 2];
			vec4 ln1 = bvh_nodes.nodes[left_idx * 2 + 1];
			vec4 rn0 = bvh_nodes.nodes[right_idx * 2];
			vec4 rn1 = bvh_nodes.nodes[right_idx * 2 + 1];
			int lc = floatBitsToInt(ln1.w);
			int rc = floatBitsToInt(rn1.w);
			bool mask_l = (lc > 0) || ((uint(-lc) & req_subtree_mask) != 0u);
			bool mask_r = (rc > 0) || ((uint(-rc) & req_subtree_mask) != 0u);
			float tmin_l = 0.0;
			float tmin_r = 0.0;
			bool hit_l = mask_l && intersect_aabb(origin, inv_dir, ln0.xyz, ln1.xyz, t_min, t_max, tmin_l);
			bool hit_r = mask_r && intersect_aabb(origin, inv_dir, rn0.xyz, rn1.xyz, t_min, t_max, tmin_r);
			if (hit_l && hit_r) {
				if (tmin_l <= tmin_r) {
					if (sp < 32) {
						stack[sp++] = right_idx;
					}
					node_idx = left_idx;
				} else {
					if (sp < 32) {
						stack[sp++] = left_idx;
					}
					node_idx = right_idx;
				}
				continue;
			}
			if (hit_l) {
				node_idx = left_idx;
				continue;
			}
			if (hit_r) {
				node_idx = right_idx;
				continue;
			}
		}

		if (sp <= 0) {
			break;
		}
		node_idx = stack[--sp];
	}

	return false;
}

bool trace_closest_hit(int start_node, uint req_subtree_mask, vec3 origin, vec3 dir, float t_min, float t_max, uint flag_mask, uint flag_expected, out float hit_dist, out vec3 hit_normal) {
	hit_dist = t_max;
	hit_normal = vec3(0.0, 1.0, 0.0);
	if (start_node < 0) {
		return false;
	}

	vec3 inv_dir = vec3(
		abs(dir.x) > 1e-8 ? 1.0 / dir.x : (dir.x >= 0.0 ? 1e8 : -1e8),
		abs(dir.y) > 1e-8 ? 1.0 / dir.y : (dir.y >= 0.0 ? 1e8 : -1e8),
		abs(dir.z) > 1e-8 ? 1.0 / dir.z : (dir.z >= 0.0 ? 1e8 : -1e8));

	float root_tmin;
	if (!intersect_aabb(origin, inv_dir, bvh_nodes.nodes[start_node * 2].xyz, bvh_nodes.nodes[start_node * 2 + 1].xyz, t_min, hit_dist, root_tmin)) {
		return false;
	}

	int stack[32];
	int sp = 0;
	int node_idx = start_node;
	bool any_hit = false;
	bool from_pop = false;

	while (true) {
		vec4 n0 = bvh_nodes.nodes[node_idx * 2];
		vec4 n1 = bvh_nodes.nodes[node_idx * 2 + 1];
		float pop_tmin;
		if (from_pop && any_hit && (!intersect_aabb(origin, inv_dir, n0.xyz, n1.xyz, t_min, hit_dist, pop_tmin) || pop_tmin >= hit_dist)) {
			if (sp <= 0) {
				break;
			}
			node_idx = stack[--sp];
			from_pop = true;
			continue;
		}
		from_pop = false;

		int left_or_first = floatBitsToInt(n0.w);
		int count = floatBitsToInt(n1.w);
		if (count > 0) {
			int end = left_or_first + count;
			for (int i = left_or_first; i < end; ++i) {
				int to = i * 4;
				vec4 n_and_flags = bvh_tris.tris[to + 3];
				if ((floatBitsToUint(n_and_flags.w) & flag_mask) != flag_expected) {
					continue;
				}
				vec3 v0 = bvh_tris.tris[to].xyz;
				vec3 e1 = bvh_tris.tris[to + 1].xyz;
				vec3 e2 = bvh_tris.tris[to + 2].xyz;
				vec3 pvec = cross(dir, e2);
				float det = dot(e1, pvec);
				if (abs(det) < 1e-8) {
					continue;
				}
				float inv_det = 1.0 / det;
				vec3 tvec = origin - v0;
				float u = dot(tvec, pvec) * inv_det;
				if (u < 0.0 || u > 1.0) {
					continue;
				}
				vec3 qvec = cross(tvec, e1);
				float v = dot(dir, qvec) * inv_det;
				if (v < 0.0 || (u + v) > 1.0) {
					continue;
				}
				float t = dot(e2, qvec) * inv_det;
				if (t > t_min && t < hit_dist) {
					hit_dist = t;
					hit_normal = n_and_flags.xyz;
					any_hit = true;
				}
			}
		} else {
			int left_idx = left_or_first;
			int right_idx = left_or_first + 1;
			vec4 ln0 = bvh_nodes.nodes[left_idx * 2];
			vec4 ln1 = bvh_nodes.nodes[left_idx * 2 + 1];
			vec4 rn0 = bvh_nodes.nodes[right_idx * 2];
			vec4 rn1 = bvh_nodes.nodes[right_idx * 2 + 1];
			int lc = floatBitsToInt(ln1.w);
			int rc = floatBitsToInt(rn1.w);
			bool mask_l = (lc > 0) || ((uint(-lc) & req_subtree_mask) != 0u);
			bool mask_r = (rc > 0) || ((uint(-rc) & req_subtree_mask) != 0u);
			float tmin_l = 0.0;
			float tmin_r = 0.0;
			bool hit_l = mask_l && intersect_aabb(origin, inv_dir, ln0.xyz, ln1.xyz, t_min, hit_dist, tmin_l);
			bool hit_r = mask_r && intersect_aabb(origin, inv_dir, rn0.xyz, rn1.xyz, t_min, hit_dist, tmin_r);
			if (hit_l && hit_r) {
				if (tmin_l <= tmin_r) {
					if (sp < 32) {
						stack[sp++] = right_idx;
					}
					node_idx = left_idx;
				} else {
					if (sp < 32) {
						stack[sp++] = left_idx;
					}
					node_idx = right_idx;
				}
				continue;
			}
			if (hit_l) {
				node_idx = left_idx;
				continue;
			}
			if (hit_r) {
				node_idx = right_idx;
				continue;
			}
		}

		if (sp <= 0) {
			break;
		}
		node_idx = stack[--sp];
		from_pop = true;
	}

	return any_hit;
}

float eval_hemisphere_ao(int cell_ao_root, int tx, int ty, vec3 world_pos, vec3 world_normal, out int hit_count) {
	hit_count = 0;
	if (cell_ao_root < 0) {
		return 1.0;
	}

	vec3 ray_origin = world_pos + world_normal * 0.03;
	vec3 r_vec = vec3(push.max_radius + 0.02);
	int ao_root = narrow_local_root(cell_ao_root, ray_origin - r_vec, ray_origin + r_vec, 6u);
	if (ao_root < 0) {
		return 1.0;
	}

	if (world_normal.y > 0.0) {
		float sin_tilt = sqrt(max(0.0, 1.0 - world_normal.y * world_normal.y));
		float min_ray_y = ray_origin.y - push.max_radius * sin_tilt;
		if (bvh_nodes.nodes[ao_root * 2 + 1].y < min_ray_y) {
			return 1.0;
		}
	}

	vec3 base_t;
	vec3 base_b;
	build_orthonormal_basis(world_normal, base_t, base_b);

	// 2x2 Bayer interleaved azimuth rotation (48 unique stratified directions per 2x2 quad, 12 rays/texel).
	int bx = tx & 1;
	int by = ty & 1;
	int bayer = (bx << 1) | (by ^ bx);
	float rot_angle = float(bayer) * 0.59999081;
	float cr = cos(rot_angle);
	float sr = sin(rot_angle);
	vec3 tangent = base_t * cr + base_b * sr;
	vec3 bitangent = -base_t * sr + base_b * cr;

	float inv_radius = 1.0 / max(push.max_radius, 0.1);
	float max_occ = (1.0 - push.min_ao) * 12.0;
	float occ_sum = 0.0;
	int far_roof_hits = 0;

	for (int r = 0; r < 12; ++r) {
		vec3 local_dir = HEMISPHERE_KERNEL[r];
		vec3 dir = normalize(tangent * local_dir.x + bitangent * local_dir.y + world_normal * local_dir.z);
		float hit_dist;
		vec3 hit_n;
		if (trace_closest_hit(ao_root, 6u, ray_origin, dir, 0.04, push.max_radius, 4u, 4u, hit_dist, hit_n)) {
			float falloff = max(0.0, 1.0 - hit_dist * inv_radius);
			occ_sum += falloff * falloff;
			hit_count++;
			if (local_dir.z > 0.70 && hit_dist > 2.25) {
				far_roof_hits++;
			}
			if (occ_sum >= max_occ && (r + 1 - hit_count) >= 2) {
				break;
			}
		}
	}

	float raw_ao = clamp(1.0 - occ_sum * (1.0 / 12.0), push.min_ao, 1.0);
	if (hit_count >= 11 && far_roof_hits >= 3) {
		raw_ao = mix(raw_ao, 0.85, 0.75);
	}
	return raw_ao;
}

bool has_continuous_2d_floor(int ao_root, float wx, float wz, float cand_y) {
	if (ao_root < 0) {
		return false;
	}
	const float probe_r = 1.15;
	vec3 down_dir = vec3(0.0, -1.0, 0.0);
	float d;
	vec3 n;
	bool hit_px = trace_closest_hit(ao_root, 4u, vec3(wx + probe_r, cand_y + 0.40, wz), down_dir, 0.0, 0.85, 6u, 4u, d, n) && abs(n.y) >= 0.55;
	bool hit_nx = !hit_px && trace_closest_hit(ao_root, 4u, vec3(wx - probe_r, cand_y + 0.40, wz), down_dir, 0.0, 0.85, 6u, 4u, d, n) && abs(n.y) >= 0.55;
	if (!hit_px && !hit_nx) {
		return false;
	}
	bool hit_pz = trace_closest_hit(ao_root, 4u, vec3(wx, cand_y + 0.40, wz + probe_r), down_dir, 0.0, 0.85, 6u, 4u, d, n) && abs(n.y) >= 0.55;
	bool hit_nz = !hit_pz && trace_closest_hit(ao_root, 4u, vec3(wx, cand_y + 0.40, wz - probe_r), down_dir, 0.0, 0.85, 6u, 4u, d, n) && abs(n.y) >= 0.55;
	return hit_pz || hit_nz;
}

void main() {
	int tx = int(gl_GlobalInvocationID.x) + push.col_offset;
	int local_y = int(gl_GlobalInvocationID.y);
	if (tx >= push.terrain_w || local_y >= push.halo_h) {
		return;
	}

	int ty = clamp(push.row_offset + local_y - 2, 0, push.terrain_h - 1);
	float u = push.terrain_w > 1 ? float(tx) / float(push.terrain_w - 1) : 0.0;
	float v = push.terrain_h > 1 ? float(ty) / float(push.terrain_h - 1) : 0.0;
	float local_x = push.crop_origin_x + u * push.crop_span_x;
	float local_z = push.crop_origin_z + v * push.crop_span_z;
	float world_x = -local_x;
	float world_z = local_z;

	int gx = clamp(int(u * 256.0), 0, 255);
	int gz = clamp(int(v * 256.0), 0, 255);
	ivec2 cell_roots = xz_grid.cells[gz * 256 + gx];
	int cell_ao_root = cell_roots.x;
	int cell_col_root = cell_roots.y;

	float bvh_min_y = bvh_nodes.nodes[0].y;
	float bvh_max_y = bvh_nodes.nodes[1].y;
	bool has_ghf = (push.ghf_w >= 2 && push.ghf_h >= 2);
	bool in_ghf = false;

	float ghf_y = bvh_min_y;
	vec3 ghf_normal = vec3(0.0, 1.0, 0.0);
	if (has_ghf) {
		float ghf_max_x = push.ghf_origin_x + float(push.ghf_w - 1) * push.ghf_spacing;
		float ghf_max_z = push.ghf_origin_z + float(push.ghf_h - 1) * push.ghf_spacing;
		in_ghf = (local_x >= push.ghf_origin_x - 0.5 && local_x <= ghf_max_x + 0.5 &&
		          local_z >= push.ghf_origin_z - 0.5 && local_z <= ghf_max_z + 0.5);
		sample_height_and_normal(local_x, local_z, ghf_y, ghf_normal);
	}

	float walk_y = ghf_y;
	float raw_walk_ao = 1.0;
	bool found_arch = false;
	bool has_sealed_floor = false;
	float sealed_floor_y = ghf_y;

	float cur_y = has_ghf ? (ghf_y - 0.60) : (bvh_min_y - 0.25);
	float max_scan_y = bvh_max_y + 0.50;
	vec3 up_dir = vec3(0.0, 1.0, 0.0);

	int prop_col_root = (cell_col_root >= 0 && cur_y <= max_scan_y)
		? narrow_local_root(cell_col_root, vec3(world_x - 0.01, cur_y, world_z - 0.01), vec3(world_x + 0.01, max_scan_y, world_z + 0.01), 4u)
		: -1;

	if (prop_col_root >= 0) {
		for (int step_idx = 0; step_idx < 8; ++step_idx) {
			if (cur_y > max_scan_y) {
				break;
			}
			float cand_dist;
			vec3 cand_n;
			if (!trace_closest_hit(prop_col_root, 4u, vec3(world_x, cur_y, world_z), up_dir, 0.0, max_scan_y - cur_y, 6u, 4u, cand_dist, cand_n)) {
				break;
			}
			float cand_y = cur_y + cand_dist;
			if (abs(cand_n.y) < 0.65) {
				cur_y = cand_y + 0.04;
				continue;
			}

			bool elevated_above_support = (has_ghf && in_ghf && cand_y > ghf_y + 0.45) ||
				(has_sealed_floor && cand_y > sealed_floor_y + 0.35);
			if (elevated_above_support && !has_continuous_2d_floor(cell_ao_root, world_x, world_z, cand_y)) {
				// Reject isolated crate lids, fence pillar caps, gravestone tops, and statue heads
				// so walk_y stays on the underlying terrain or architectural plaza support floor.
				cur_y = cand_y + 0.04;
				continue;
			}

			vec3 up_n = cand_n.y < 0.0 ? -cand_n : cand_n;
			int head_root = narrow_local_root(
				cell_ao_root,
				vec3(world_x - 0.01, cand_y + 0.03, world_z - 0.01),
				vec3(world_x + 0.01, cand_y + 1.83, world_z + 0.01),
				6u);
			bool any_head_hit = head_root >= 0 &&
				trace_any_hit(head_root, 6u, vec3(world_x, cand_y + 0.03, world_z), up_dir, 0.0, 1.80, 4u, 4u);
			if (!any_head_hit) {
				walk_y = cand_y;
				int arch_hits;
				raw_walk_ao = eval_hemisphere_ao(cell_ao_root, tx, ty, vec3(world_x, walk_y, world_z), up_n, arch_hits);
				found_arch = true;
				break;
			}

			float head_dist = 1.80;
			vec3 head_n = vec3(0.0, 1.0, 0.0);
			trace_closest_hit(head_root, 6u, vec3(world_x, cand_y + 0.03, world_z), up_dir, 0.0, 1.80, 4u, 4u, head_dist, head_n);
			if (head_dist < 0.22) {
				if (!has_sealed_floor) {
					has_sealed_floor = true;
					sealed_floor_y = cand_y;
				}
				cur_y = cand_y + 0.015 + head_dist;
				continue;
			}

			int cand_hits;
			float cand_ao = eval_hemisphere_ao(cell_ao_root, tx, ty, vec3(world_x, cand_y, world_z), up_n, cand_hits);
			if (cand_hits < 12) {
				walk_y = cand_y;
				raw_walk_ao = cand_ao;
				found_arch = true;
				break;
			}

			if (!has_sealed_floor) {
				has_sealed_floor = true;
				sealed_floor_y = cand_y;
			}
			cur_y = cand_y + 0.015 + head_dist;
		}
	}

	if (!found_arch && has_sealed_floor) {
		walk_y = sealed_floor_y;
		raw_walk_ao = push.min_ao;
		found_arch = true;
	}

	float raw_terrain_ao = 1.0;
	if (has_ghf && in_ghf) {
		if (found_arch && walk_y > ghf_y + 0.45 && prop_col_root >= 0 &&
		    trace_any_hit(prop_col_root, 4u, vec3(world_x, ghf_y + 0.05, world_z), up_dir, 0.0, min(walk_y - ghf_y + 0.15, 2.60), 6u, 4u)) {
			// Subterranean .ghf sealed beneath an architectural floor/building foundation: skip 12 hemisphere rays.
			raw_terrain_ao = push.min_ao;
		} else {
			int terrain_hits;
			raw_terrain_ao = eval_hemisphere_ao(cell_ao_root, tx, ty, vec3(world_x, ghf_y, world_z), ghf_normal, terrain_hits);
		}
	}

	if (!found_arch) {
		raw_walk_ao = raw_terrain_ao;
	} else if (!has_ghf) {
		ghf_y = walk_y;
		raw_terrain_ao = raw_walk_ao;
	}

	out_buf.samples[local_y * push.terrain_w + tx] = vec4(raw_terrain_ao, ghf_y, raw_walk_ao, walk_y);
}
";

	/// <summary>GLSL 450 compute shader for 5x5 height-aware bilateral filtering and RGBA8 packing of ground AO.</summary>
	public const string GroundAoFilterShaderSource = @"#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict readonly buffer MidAo {
	vec4 samples[];
} mid_buf;

layout(set = 0, binding = 1, std430) restrict writeonly buffer OutRgba8 {
	uint packed_rgba8[];
} out_buf;

layout(push_constant, std430) uniform Push {
	int terrain_w;
	int strip_h;
	int pad0;
	int pad1;
	float min_h;
	float inv_h_span;
	float pad2;
	float pad3;
} push;

float spatial_axis_weight(int d) {
	int ad = abs(d);
	return ad == 0 ? 1.0 : (ad == 1 ? 0.72 : 0.34);
}

void main() {
	int tx = int(gl_GlobalInvocationID.x);
	int out_y = int(gl_GlobalInvocationID.y);
	if (tx >= push.terrain_w || out_y >= push.strip_h) {
		return;
	}

	int mid_cy = out_y + 2;
	vec4 center = mid_buf.samples[mid_cy * push.terrain_w + tx];
	float center_terr_ao = center.x;
	float center_terr_h = center.y;
	float center_walk_ao = center.z;
	float center_walk_h = center.w;

	float sum_terr_ao = 0.0;
	float sum_terr_w = 0.0;
	float sum_walk_ao = 0.0;
	float sum_walk_w = 0.0;

	for (int dy = -2; dy <= 2; ++dy) {
		int ny = mid_cy + dy;
		int row_base = ny * push.terrain_w;
		float wy = spatial_axis_weight(dy);
		for (int dx = -2; dx <= 2; ++dx) {
			int nx = clamp(tx + dx, 0, push.terrain_w - 1);
			vec4 s = mid_buf.samples[row_base + nx];
			float spatial_w = wy * spatial_axis_weight(dx);

			float dt = s.y - center_terr_h;
			float dt2 = dt * dt * 2.5;
			float wt = spatial_w / (1.0 + dt2 + 0.5 * dt2 * dt2);
			sum_terr_ao += s.x * wt;
			sum_terr_w += wt;

			float dw = s.w - center_walk_h;
			float dw2 = dw * dw * 7.5;
			float ww = spatial_w / (1.0 + dw2 + 0.5 * dw2 * dw2);
			sum_walk_ao += s.z * ww;
			sum_walk_w += ww;
		}
	}

	float filt_terr = sum_terr_w > 1e-6 ? (sum_terr_ao / sum_terr_w) : center_terr_ao;
	float filt_walk = sum_walk_w > 1e-6 ? (sum_walk_ao / sum_walk_w) : center_walk_ao;
	float norm_h = clamp((center_walk_h - push.min_h) * push.inv_h_span, 0.0, 1.0);

	uint r_byte = uint(clamp(int(round(filt_terr * 255.0)), 0, 255));
	uint g_byte = uint(clamp(int(round(filt_walk * 255.0)), 0, 255));
	uint h16 = uint(clamp(int(round(norm_h * 65535.0)), 0, 65535));
	uint b_byte = h16 & 0xFFu;
	uint a_byte = (h16 >> 8) & 0xFFu;

	out_buf.packed_rgba8[out_y * push.terrain_w + tx] = r_byte | (g_byte << 8) | (b_byte << 16) | (a_byte << 24);
}
";

	/// <summary>
	/// Ray-traces static shadow tiles on a worker-owned local <see cref="RenderingDevice"/> without stalling the main thread.
	/// </summary>
	public static bool TryBakeShadowAtlasGpu(
		Engine_LevelShadowMaps.BvhTree worldBvh,
		IReadOnlyList<Engine_LevelShadowMaps.BakeLightSpec> lights,
		int atlasResolution,
		int lightsPerMap,
		int tilesPerRow,
		int tileTotalSize,
		int innerSize,
		int sliceTexelCount,
		float texelAngle,
		ushort[] texels,
		Func<bool>? isCancelled = null) =>
		TryBakeShadowAtlasGpu(
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
			liveGameplayPaced: false,
			gpuSession: null);

	/// <summary>
	/// Ray-traces static shadow tiles on a worker-owned local <see cref="RenderingDevice"/>, optionally sharing
	/// <paramref name="gpuSession"/> and adapting slice sizes + inter-frame yields when <paramref name="liveGameplayPaced"/> is active.
	/// </summary>
	internal static bool TryBakeShadowAtlasGpu(
		Engine_LevelShadowMaps.BvhTree worldBvh,
		IReadOnlyList<Engine_LevelShadowMaps.BakeLightSpec> lights,
		int atlasResolution,
		int lightsPerMap,
		int tilesPerRow,
		int tileTotalSize,
		int innerSize,
		int sliceTexelCount,
		float texelAngle,
		ushort[] texels,
		Func<bool>? isCancelled,
		bool liveGameplayPaced,
		GpuBakeSession? gpuSession)
	{
		if (lights.Count == 0 || worldBvh.TriangleCount == 0 || Engine_AppRoot.IsTestHost || Engine_RdHealth.IsDeviceLost)
		{
			return false;
		}

		lock (SyncRoot)
		{
			if (isCancelled?.Invoke() == true || Engine_RdHealth.IsDeviceLost)
			{
				return false;
			}

			long bytesPerLight = (long)tileTotalSize * tileTotalSize * sizeof(uint);
			int hardCapLights = (int)Math.Clamp(
				Math.Min(Refii.AndroidRuntime.AndroidBakedShadows.ShadowBatchLights(64), 32L * 1024L * 1024L / Math.Max(1L, bytesPerLight)),
				1L,
				lights.Count);
			int currentBatchLights = Math.Clamp(
				(liveGameplayPaced && !Engine_ServerSyncs.LoadCoverActive) ? Math.Min(4, hardCapLights) : hardCapLights,
				1,
				hardCapLights);

			// 48 bytes per light (3 x vec4: origin+range, spotFwd+spotCos, startNode+pad).
			const int strideBytes = 48;
			byte[] lightBytes = new byte[hardCapLights * strideBytes];
			int[] activeLightIndices = new int[hardCapLights];
			int outBufferBytes = (int)(hardCapLights * bytesPerLight);
			float invInner = 1.0f / innerSize;
			byte[] push = new byte[32];
			int groupsX = (tileTotalSize + 7) / 8;
			int groupsY = (tileTotalSize + 7) / 8;

			bool ownsDevice = gpuSession is null;
			RenderingDevice? rd = gpuSession?.Device;
			Rid shader = default;
			Rid pipeline = default;
			Rid nodeBuf = gpuSession?.NodeBuffer ?? default;
			Rid triBuf = gpuSession?.TriangleBuffer ?? default;
			Rid lightBuf = default;
			Rid outBuf = default;
			Rid uniformSet = default;
			bool pendingSubmission = false;

			try
			{
				if (ownsDevice)
				{
					rd = RenderingServer.CreateLocalRenderingDevice();
				}

				if (rd is null)
				{
					Interlocked.Increment(ref _cpuFallbackCount);
					return false;
				}

				shader = CreateComputeShaderCached(
					rd,
					ShadowAtlasShaderSource,
					"gpu_shadow_atlas_baker",
					ref _cachedShadowSpirv,
					out string? compileErr);
				if (!shader.IsValid)
				{
					if (!string.IsNullOrEmpty(compileErr) && !Engine_AppRoot.IsTestHost)
					{
						GD.PushWarning($"[gpu-bake] shadow atlas compute shader compile error: {compileErr}");
					}
					Interlocked.Increment(ref _cpuFallbackCount);
					return false;
				}

				pipeline = rd.ComputePipeline(shader);
				if (!pipeline.IsValid)
				{
					Interlocked.Increment(ref _cpuFallbackCount);
					return false;
				}

				if (ownsDevice)
				{
					UploadBvh(rd, worldBvh, out nodeBuf, out triBuf);
				}

				lightBuf = rd.StorageBufferCreate((uint)lightBytes.Length, lightBytes);
				outBuf = rd.StorageBufferCreate((uint)outBufferBytes);
				uniformSet = rd.UniformSet(new RDUniform[]
				{
					CreateStorageUniform(nodeBuf, 0),
					CreateStorageUniform(triBuf, 1),
					CreateStorageUniform(lightBuf, 2),
					CreateStorageUniform(outBuf, 3),
				}, shader, 0);

				if (!uniformSet.IsValid)
				{
					Interlocked.Increment(ref _cpuFallbackCount);
					return false;
				}

				int batchStart = 0;
				while (batchStart < lights.Count)
				{
					if (isCancelled?.Invoke() == true || Engine_RdHealth.IsDeviceLost)
					{
						return false;
					}

					int batchCount = Math.Min(currentBatchLights, lights.Count - batchStart);
					Span<float> lFloats = MemoryMarshal.Cast<byte, float>(lightBytes.AsSpan());
					Span<int> lInts = MemoryMarshal.Cast<byte, int>(lightBytes.AsSpan());
					int activeInBatch = 0;
					for (int k = 0; k < batchCount; k++)
					{
						int originalIdx = batchStart + k;
						Engine_LevelShadowMaps.BakeLightSpec l = lights[originalIdx];
						Engine_LevelShadowMaps.ComputeBakeLightBounds(in l, out Vector3 lMin, out Vector3 lMax);
						int startNode = worldBvh.FindLocalSubtreeRoot(
							lMin,
							lMax,
							Engine_LevelShadowMaps.BvhTree.SubtreeHasShadowCaster);
						if (startNode < 0)
						{
							continue;
						}

						int o = activeInBatch * 12;
						lFloats[o] = l.WorldOrigin.X;
						lFloats[o + 1] = l.WorldOrigin.Y;
						lFloats[o + 2] = l.WorldOrigin.Z;
						lFloats[o + 3] = l.MaxRange;
						lFloats[o + 4] = l.SpotForward.X;
						lFloats[o + 5] = l.SpotForward.Y;
						lFloats[o + 6] = l.SpotForward.Z;
						lFloats[o + 7] = l.IsSpot ? l.SpotCosLimit : -2.0f;
						lInts[o + 8] = startNode;
						lInts[o + 9] = 0;
						lInts[o + 10] = 0;
						lInts[o + 11] = 0;
						activeLightIndices[activeInBatch++] = originalIdx;
					}

					// If zero lights in this batch have any shadow caster in range, texels[] is already
					// pre-filled with MaxHalfDepthBits (0x7BFFu); skip the GPU dispatch completely!
					if (activeInBatch == 0)
					{
						batchStart += batchCount;
						continue;
					}

					BitConverter.TryWriteBytes(push.AsSpan(0), innerSize);
					BitConverter.TryWriteBytes(push.AsSpan(4), activeInBatch);
					BitConverter.TryWriteBytes(push.AsSpan(8), 0); // row_offset
					BitConverter.TryWriteBytes(push.AsSpan(12), tileTotalSize); // row_count (includes 1-texel gutter)
					BitConverter.TryWriteBytes(push.AsSpan(16), invInner);
					BitConverter.TryWriteBytes(push.AsSpan(20), texelAngle);
					BitConverter.TryWriteBytes(push.AsSpan(24), tileTotalSize); // pad0 = tile_total
					BitConverter.TryWriteBytes(push.AsSpan(28), 0);

					uint readBytes = (uint)(activeInBatch * bytesPerLight);
					long sliceStartTick = Stopwatch.GetTimestamp();

					rd.BufferUpdate(lightBuf, 0, (uint)(activeInBatch * strideBytes), lightBytes.AsSpan(0, activeInBatch * strideBytes).ToArray());
					long list = rd.ComputeListBegin();
					rd.ComputeListBindComputePipeline(list, pipeline);
					rd.ComputeListBindUniformSet(list, uniformSet, 0);
					rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
					rd.ComputeListDispatch(list, (uint)groupsX, (uint)groupsY, (uint)activeInBatch);
					rd.ComputeListEnd();
					rd.Submit();
					pendingSubmission = true;

					rd.Sync();
					pendingSubmission = false;
					byte[] rawOut = rd.BufferGetData(outBuf, 0, readBytes);
					double sliceElapsedMs = (Stopwatch.GetTimestamp() - sliceStartTick) * (1000.0 / Stopwatch.Frequency);

					if (rawOut is null || rawOut.Length < readBytes)
					{
						return false;
					}

					byte[] batchBytes = rawOut;
					int tileTexelTotal = tileTotalSize * tileTotalSize;
					Parallel.For(0, activeInBatch, k =>
					{
						int lightIdx = activeLightIndices[k];
						int sliceIndex = lightIdx / lightsPerMap;
						int localIndex = lightIdx % lightsPerMap;
						int tileX = (localIndex % tilesPerRow) * tileTotalSize;
						int tileY = (localIndex / tilesPerRow) * tileTotalSize;
						int sliceOffset = sliceIndex * sliceTexelCount;

						ReadOnlySpan<uint> uintSpan = MemoryMarshal.Cast<byte, uint>(
							batchBytes.AsSpan(k * tileTexelTotal * sizeof(uint), tileTexelTotal * sizeof(uint)));

						for (int ty = 0; ty < tileTotalSize; ty++)
						{
							int dstRow = sliceOffset + (tileY + ty) * atlasResolution + tileX;
							int srcRow = ty * tileTotalSize;
							for (int tx = 0; tx < tileTotalSize; tx++)
							{
								texels[dstRow + tx] = (ushort)(uintSpan[srcRow + tx] & 0xFFFFu);
							}
						}
					});

					batchStart += batchCount;
					bool paceLive = liveGameplayPaced && !Engine_ServerSyncs.LoadCoverActive;
					double targetMs = paceLive ? LiveGameplayTargetSliceMs : LoadCoverTargetSliceMs;
					int minBatchLights = paceLive ? 1 : Math.Clamp(8, 1, hardCapLights);
					currentBatchLights = ComputeNextSliceUnits(batchCount, sliceElapsedMs, targetMs, minBatchLights, hardCapLights);
					if (paceLive && batchStart < lights.Count)
					{
						Engine_ServerSyncs.WaitForNextDrawnFrame(ComputeYieldFrames(sliceElapsedMs, LiveGameplayTargetSliceMs), 50, isCancelled);
					}
				}

				Interlocked.Increment(ref _gpuShadowBakeCount);
				return true;
			}
			catch (Exception ex)
			{
				Interlocked.Increment(ref _cpuFallbackCount);
				if (!Engine_AppRoot.IsTestHost)
				{
					GD.PushWarning($"[gpu-bake] shadow atlas GPU bake fell back to CPU: {ex.Message}");
				}
				return false;
			}
			finally
			{
				if (rd is not null)
				{
					if (pendingSubmission)
					{
						try { rd.Sync(); } catch { }
					}

					FreeIfValid(rd, uniformSet);
					FreeIfValid(rd, outBuf);
					FreeIfValid(rd, lightBuf);
					FreeIfValid(rd, pipeline);
					FreeIfValid(rd, shader);
					if (ownsDevice)
					{
						FreeIfValid(rd, triBuf);
						FreeIfValid(rd, nodeBuf);
						try { rd.Free(); } catch { }
					}
				}
			}
		}
	}

	/// <summary>
	/// Computes tight terrain height bounds over the crop rectangle directly from <paramref name="ghf"/>
	/// on the CPU in &lt;0.05 ms without allocating any intermediate <c>float[]</c> arrays.
	/// </summary>
	public static void ComputeTerrainHeightBounds(
		in GhfHeights ghf,
		float cropOriginX,
		float cropOriginZ,
		float cropSpanX,
		float cropSpanZ,
		out float minH,
		out float maxH)
	{
		minH = float.MaxValue;
		maxH = float.MinValue;
		if (ghf.Width < 2 || ghf.Height < 2 || ghf.Spacing <= 0.0f || ghf.Values.Length < ghf.Width * ghf.Height)
		{
			minH = 0.0f;
			maxH = 1.0f;
			return;
		}

		float invSpacing = 1.0f / ghf.Spacing;
		int x0 = Math.Clamp((int)MathF.Floor((cropOriginX - ghf.OriginX) * invSpacing), 0, ghf.Width - 1);
		int x1 = Math.Clamp((int)MathF.Ceiling((cropOriginX + cropSpanX - ghf.OriginX) * invSpacing), 0, ghf.Width - 1);
		int z0 = Math.Clamp((int)MathF.Floor((cropOriginZ - ghf.OriginZ) * invSpacing), 0, ghf.Height - 1);
		int z1 = Math.Clamp((int)MathF.Ceiling((cropOriginZ + cropSpanZ - ghf.OriginZ) * invSpacing), 0, ghf.Height - 1);

		float[] values = ghf.Values;
		int w = ghf.Width;
		for (int z = z0; z <= z1; z++)
		{
			int row = z * w;
			for (int x = x0; x <= x1; x++)
			{
				float h = values[row + x];
				if (float.IsFinite(h))
				{
					if (h < minH) minH = h;
					if (h > maxH) maxH = h;
				}
			}
		}

		if (minH > maxH)
		{
			minH = 0.0f;
			maxH = 1.0f;
		}
	}

	/// <summary>
	/// Ray-traces ground and walkable-floor contact AO on a worker-owned local <see cref="RenderingDevice"/>
	/// without stalling the main thread.
	/// </summary>
	public static bool TryBakeGroundAoGpu(
		Engine_LevelShadowMaps.BvhTree worldBvh,
		GhfHeights? primaryTerrain,
		int terrainW,
		int terrainH,
		float cropOriginX,
		float cropOriginZ,
		float cropSpanX,
		float cropSpanZ,
		float maxRadius,
		float minAo,
		out byte[] terrainAoBytes,
		out float terrainMinH,
		out float terrainMaxH,
		Func<bool>? isCancelled = null) =>
		TryBakeGroundAoGpu(
			worldBvh,
			primaryTerrain,
			terrainW,
			terrainH,
			cropOriginX,
			cropOriginZ,
			cropSpanX,
			cropSpanZ,
			maxRadius,
			minAo,
			out terrainAoBytes,
			out terrainMinH,
			out terrainMaxH,
			isCancelled,
			liveGameplayPaced: false,
			gpuSession: null);

	/// <summary>
	/// Ray-traces ground and walkable-floor contact AO on a worker-owned local <see cref="RenderingDevice"/>,
	/// optionally sharing <paramref name="gpuSession"/> and adapting strip heights + inter-frame yields when
	/// <paramref name="liveGameplayPaced"/> is active.
	/// </summary>
	internal static bool TryBakeGroundAoGpu(
		Engine_LevelShadowMaps.BvhTree worldBvh,
		GhfHeights? primaryTerrain,
		int terrainW,
		int terrainH,
		float cropOriginX,
		float cropOriginZ,
		float cropSpanX,
		float cropSpanZ,
		float maxRadius,
		float minAo,
		out byte[] terrainAoBytes,
		out float terrainMinH,
		out float terrainMaxH,
		Func<bool>? isCancelled,
		bool liveGameplayPaced,
		GpuBakeSession? gpuSession)
	{
		terrainAoBytes = Array.Empty<byte>();
		terrainMinH = 0.0f;
		terrainMaxH = 1.0f;

		if (terrainW <= 0 || terrainH <= 0 || worldBvh.TriangleCount == 0 || Engine_AppRoot.IsTestHost || Engine_RdHealth.IsDeviceLost)
		{
			return false;
		}

		lock (SyncRoot)
		{
			if (isCancelled?.Invoke() == true || Engine_RdHealth.IsDeviceLost)
			{
				return false;
			}

			Engine_LevelShadowMaps.BvhTree aoBvh = worldBvh.BuildCroppedAoBvh(
				cropOriginX,
				cropOriginZ,
				cropSpanX,
				cropSpanZ,
				maxRadius + 0.35f);

			bool hasGhf = primaryTerrain is { Width: >= 2, Height: >= 2, Spacing: > 0.0f } validGhf &&
				validGhf.Values.Length >= validGhf.Width * validGhf.Height;
			GhfHeights ghf = hasGhf ? primaryTerrain! : new GhfHeights();

			if (hasGhf)
			{
				ComputeTerrainHeightBounds(
					in ghf,
					cropOriginX,
					cropOriginZ,
					cropSpanX,
					cropSpanZ,
					out terrainMinH,
					out terrainMaxH);
				if (float.IsFinite(aoBvh.RootMin.Y))
				{
					terrainMinH = MathF.Min(terrainMinH, aoBvh.RootMin.Y);
				}
				if (float.IsFinite(aoBvh.RootMax.Y))
				{
					terrainMaxH = MathF.Max(terrainMaxH, aoBvh.RootMax.Y);
				}
			}
			else
			{
				terrainMinH = float.IsFinite(aoBvh.RootMin.Y) ? aoBvh.RootMin.Y : 0.0f;
				terrainMaxH = float.IsFinite(aoBvh.RootMax.Y) ? aoBvh.RootMax.Y : 1.0f;
				if (terrainMaxH <= terrainMinH)
				{
					terrainMaxH = terrainMinH + 1.0f;
				}
			}

			float invHeightSpan = (terrainMaxH - terrainMinH) > 1e-4f ? 1.0f / (terrainMaxH - terrainMinH) : 0.0f;

			byte[] ghfBytes;
			if (hasGhf)
			{
				ghfBytes = new byte[ghf.Values.Length * sizeof(float)];
				Buffer.BlockCopy(ghf.Values, 0, ghfBytes, 0, ghfBytes.Length);
			}
			else
			{
				ghfBytes = new byte[4];
			}

			int[] xzGridInts = aoBvh.BuildXzSubtreeGrid(
				cropOriginX,
				cropOriginZ,
				cropSpanX,
				cropSpanZ,
				maxRadius,
				XzGridResolution);
			byte[] xzGridBytes = new byte[xzGridInts.Length * sizeof(int)];
			Buffer.BlockCopy(xzGridInts, 0, xzGridBytes, 0, xzGridBytes.Length);

			int desktopMaxRows = terrainW >= 7168 ? 256 : 512;
			int maxStripH = Math.Clamp(
				Refii.AndroidRuntime.AndroidBakedShadows.AoStripRows(terrainW, desktopMaxRows),
				1,
				terrainH);
			bool initialPaceLive = liveGameplayPaced && !Engine_ServerSyncs.LoadCoverActive;
			int minStripH = initialPaceLive ? Math.Clamp(8, 1, maxStripH) : Math.Clamp(64, 1, maxStripH);
			int currentStripH = initialPaceLive
				? Math.Clamp(maxStripH / 4, minStripH, maxStripH)
				: maxStripH;

			// 5x5 height-aware bilateral filter requires a 2-row halo above and below each strip (+4 rows).
			int maxHaloH = maxStripH + 4;
			int midBufferBytes = terrainW * maxHaloH * 16;
			int outBufferBytes = terrainW * maxStripH * sizeof(uint);

			int ghfW = hasGhf ? ghf.Width : 0;
			int ghfH = hasGhf ? ghf.Height : 0;
			float ghfOriginX = hasGhf ? ghf.OriginX : 0.0f;
			float ghfOriginZ = hasGhf ? ghf.OriginZ : 0.0f;
			float ghfSpacing = hasGhf ? ghf.Spacing : 1.0f;

			bool ownsDevice = gpuSession is null;
			bool ownsAoBvhBuffers = ownsDevice || !ReferenceEquals(aoBvh, worldBvh);
			RenderingDevice? rd = gpuSession?.Device;
			Rid traceShader = default;
			Rid tracePipeline = default;
			Rid filterShader = default;
			Rid filterPipeline = default;
			Rid nodeBuf = ownsAoBvhBuffers ? default : gpuSession!.NodeBuffer;
			Rid triBuf = ownsAoBvhBuffers ? default : gpuSession!.TriangleBuffer;
			Rid ghfBuf = default;
			Rid xzGridBuf = default;
			Rid midBuf = default;
			Rid outBuf = default;
			Rid traceSet = default;
			Rid filterSet = default;
			bool pendingSubmission = false;

			try
			{
				if (ownsDevice)
				{
					rd = RenderingServer.CreateLocalRenderingDevice();
				}

				if (rd is null)
				{
					Interlocked.Increment(ref _cpuFallbackCount);
					return false;
				}

				traceShader = CreateComputeShaderCached(
					rd,
					GroundAoShaderSource,
					"gpu_ground_ao_baker",
					ref _cachedGroundAoSpirv,
					out string? traceCompileErr);
				filterShader = CreateComputeShaderCached(
					rd,
					GroundAoFilterShaderSource,
					"gpu_ground_ao_filter",
					ref _cachedGroundAoFilterSpirv,
					out string? filterCompileErr);
				if (!traceShader.IsValid || !filterShader.IsValid)
				{
					string? err = traceCompileErr ?? filterCompileErr;
					if (!string.IsNullOrEmpty(err) && !Engine_AppRoot.IsTestHost)
					{
						GD.PushWarning($"[gpu-bake] ground AO compute shader compile error: {err}");
					}
					Interlocked.Increment(ref _cpuFallbackCount);
					return false;
				}

				tracePipeline = rd.ComputePipeline(traceShader);
				filterPipeline = rd.ComputePipeline(filterShader);
				if (!tracePipeline.IsValid || !filterPipeline.IsValid)
				{
					Interlocked.Increment(ref _cpuFallbackCount);
					return false;
				}

				if (ownsAoBvhBuffers)
				{
					UploadBvh(rd, aoBvh, out nodeBuf, out triBuf);
				}

				ghfBuf = rd.StorageBufferCreate((uint)ghfBytes.Length, ghfBytes);
				xzGridBuf = rd.StorageBufferCreate((uint)xzGridBytes.Length, xzGridBytes);
				midBuf = rd.StorageBufferCreate((uint)midBufferBytes);
				outBuf = rd.StorageBufferCreate((uint)outBufferBytes);

				traceSet = rd.UniformSet(new RDUniform[]
				{
					CreateStorageUniform(nodeBuf, 0),
					CreateStorageUniform(triBuf, 1),
					CreateStorageUniform(ghfBuf, 2),
					CreateStorageUniform(midBuf, 3),
					CreateStorageUniform(xzGridBuf, 4),
				}, traceShader, 0);

				filterSet = rd.UniformSet(new RDUniform[]
				{
					CreateStorageUniform(midBuf, 0),
					CreateStorageUniform(outBuf, 1),
				}, filterShader, 0);

				if (!traceSet.IsValid || !filterSet.IsValid)
				{
					Interlocked.Increment(ref _cpuFallbackCount);
					return false;
				}

				byte[] resultRgba8 = new byte[terrainW * terrainH * 4];
				byte[] tracePush = new byte[64];
				byte[] filterPush = new byte[32];
				int traceGroupsX = (terrainW + 7) / 8;
				int filterGroupsX = traceGroupsX;
				int traceColumns = Refii.AndroidRuntime.AndroidBakedShadows.AoTraceColumns(terrainW, maxStripH + 4);
				float minHCopy = terrainMinH;

				int rowOffset = 0;
				while (rowOffset < terrainH)
				{
					if (isCancelled?.Invoke() == true || Engine_RdHealth.IsDeviceLost)
					{
						return false;
					}

					int stripH = Math.Min(currentStripH, terrainH - rowOffset);
					int haloH = stripH + 4;
					int traceGroupsY = (haloH + 7) / 8;
					int filterGroupsY = (stripH + 7) / 8;

					BitConverter.TryWriteBytes(tracePush.AsSpan(0), terrainW);
					BitConverter.TryWriteBytes(tracePush.AsSpan(4), terrainH);
					BitConverter.TryWriteBytes(tracePush.AsSpan(8), rowOffset);
					BitConverter.TryWriteBytes(tracePush.AsSpan(12), haloH);
					BitConverter.TryWriteBytes(tracePush.AsSpan(16), ghfW);
					BitConverter.TryWriteBytes(tracePush.AsSpan(20), ghfH);
					BitConverter.TryWriteBytes(tracePush.AsSpan(24), ghfOriginX);
					BitConverter.TryWriteBytes(tracePush.AsSpan(28), ghfOriginZ);
					BitConverter.TryWriteBytes(tracePush.AsSpan(32), ghfSpacing);
					BitConverter.TryWriteBytes(tracePush.AsSpan(36), cropOriginX);
					BitConverter.TryWriteBytes(tracePush.AsSpan(40), cropOriginZ);
					BitConverter.TryWriteBytes(tracePush.AsSpan(44), cropSpanX);
					BitConverter.TryWriteBytes(tracePush.AsSpan(48), cropSpanZ);
					BitConverter.TryWriteBytes(tracePush.AsSpan(52), maxRadius);
					BitConverter.TryWriteBytes(tracePush.AsSpan(56), minAo);
					BitConverter.TryWriteBytes(tracePush.AsSpan(60), 0); // col_offset (whole strip by default)

					BitConverter.TryWriteBytes(filterPush.AsSpan(0), terrainW);
					BitConverter.TryWriteBytes(filterPush.AsSpan(4), stripH);
					BitConverter.TryWriteBytes(filterPush.AsSpan(8), 0);
					BitConverter.TryWriteBytes(filterPush.AsSpan(12), 0);
					BitConverter.TryWriteBytes(filterPush.AsSpan(16), minHCopy);
					BitConverter.TryWriteBytes(filterPush.AsSpan(20), invHeightSpan);
					BitConverter.TryWriteBytes(filterPush.AsSpan(24), 0.0f);
					BitConverter.TryWriteBytes(filterPush.AsSpan(28), 0.0f);

					int rowStrideBytes = terrainW * sizeof(uint);
					uint readBytes = (uint)(stripH * rowStrideBytes);
					long sliceStartTick = Stopwatch.GetTimestamp();

					if (traceColumns < terrainW)
					{
						pendingSubmission = true;
						if (!Refii.AndroidRuntime.AndroidBakedShadows.TraceAoInPieces(rd, tracePipeline, traceSet, tracePush,
							    traceColumns, terrainW, traceGroupsY, filterPipeline, filterSet, filterPush, filterGroupsX, filterGroupsY))
						{
							pendingSubmission = false;
							return false;
						}
					}
					else
					{
						long list1 = rd.ComputeListBegin();
						rd.ComputeListBindComputePipeline(list1, tracePipeline);
						rd.ComputeListBindUniformSet(list1, traceSet, 0);
						rd.ComputeListSetPushConstant(list1, tracePush, (uint)tracePush.Length);
						rd.ComputeListDispatch(list1, (uint)traceGroupsX, (uint)traceGroupsY, 1);
						rd.ComputeListEnd();

						long list2 = rd.ComputeListBegin();
						rd.ComputeListBindComputePipeline(list2, filterPipeline);
						rd.ComputeListBindUniformSet(list2, filterSet, 0);
						rd.ComputeListSetPushConstant(list2, filterPush, (uint)filterPush.Length);
						rd.ComputeListDispatch(list2, (uint)filterGroupsX, (uint)filterGroupsY, 1);
						rd.ComputeListEnd();

						rd.Submit();
						pendingSubmission = true;
					}

					rd.Sync();
					pendingSubmission = false;
					byte[] stripOut = rd.BufferGetData(outBuf, 0, readBytes);
					double sliceElapsedMs = (Stopwatch.GetTimestamp() - sliceStartTick) * (1000.0 / Stopwatch.Frequency);

					if (stripOut is null || stripOut.Length < readBytes)
					{
						return false;
					}

					Buffer.BlockCopy(stripOut, 0, resultRgba8, rowOffset * rowStrideBytes, stripH * rowStrideBytes);
					rowOffset += stripH;

					bool paceLive = liveGameplayPaced && !Engine_ServerSyncs.LoadCoverActive;
					double targetMs = paceLive ? LiveGameplayTargetSliceMs : LoadCoverTargetSliceMs;
					int stepMinStrip = paceLive ? Math.Clamp(8, 1, maxStripH) : Math.Clamp(64, 1, maxStripH);
					currentStripH = ComputeNextSliceUnits(stripH, sliceElapsedMs, targetMs, stepMinStrip, maxStripH);
					if (paceLive && rowOffset < terrainH)
					{
						Engine_ServerSyncs.WaitForNextDrawnFrame(ComputeYieldFrames(sliceElapsedMs, LiveGameplayTargetSliceMs), 50, isCancelled);
					}
				}

				terrainAoBytes = resultRgba8;
				Interlocked.Increment(ref _gpuAoBakeCount);
				return true;
			}
			catch (Exception ex)
			{
				Interlocked.Increment(ref _cpuFallbackCount);
				if (!Engine_AppRoot.IsTestHost)
				{
					GD.PushWarning($"[gpu-bake] ground AO GPU bake fell back to CPU: {ex.Message}");
				}
				return false;
			}
			finally
			{
				if (rd is not null)
				{
					if (pendingSubmission)
					{
						try { rd.Sync(); } catch { }
					}

					FreeIfValid(rd, filterSet);
					FreeIfValid(rd, traceSet);
					FreeIfValid(rd, outBuf);
					FreeIfValid(rd, midBuf);
					FreeIfValid(rd, xzGridBuf);
					FreeIfValid(rd, ghfBuf);
					FreeIfValid(rd, filterPipeline);
					FreeIfValid(rd, tracePipeline);
					FreeIfValid(rd, filterShader);
					FreeIfValid(rd, traceShader);
					if (ownsAoBvhBuffers)
					{
						FreeIfValid(rd, triBuf);
						FreeIfValid(rd, nodeBuf);
					}
					if (ownsDevice)
					{
						try { rd.Free(); } catch { }
					}
				}
			}
		}
	}

	private static RDUniform CreateStorageUniform(Rid buffer, int binding)
	{
		var u = new RDUniform
		{
			UniformType = RenderingDevice.UniformType.StorageBuffer,
			Binding = binding,
		};
		u.AddId(buffer);
		return u;
	}

	private static void FreeIfValid(RenderingDevice rd, Rid rid)
	{
		if (rid.IsValid)
		{
			try
			{
				rd.FreeRid(rid);
			}
			catch
			{
			}
		}
	}
}

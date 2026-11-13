using System;
using System.Collections.Generic;
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
/// Dispatches small slices via <see cref="Engine_ServerSyncs.RunGpuUpload"/> so background level bakes never
/// block scene loading, never race <c>RenderingServer::draw()</c> on Vulkan/D3D12 queues, and never allocate
/// Large Object Heap <c>float[]</c> buffers on the CPU.
/// </summary>
public static class Engine_GpuBvhBaker
{
	private static readonly object SyncRoot = new();

	private static int _gpuShadowBakeCount;
	private static int _gpuAoBakeCount;
	private static int _cpuFallbackCount;

	public static int GpuShadowBakeCount => Volatile.Read(ref _gpuShadowBakeCount);
	public static int GpuAoBakeCount => Volatile.Read(ref _gpuAoBakeCount);
	public static int CpuFallbackCount => Volatile.Read(ref _cpuFallbackCount);

	/// <summary>Bytes a BVH goes up to the GPU in at a time.</summary>
	private const int BvhUploadChunkBytes = 4 * 1024 * 1024;

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

	/// <summary>
	/// Estimated staging/compute budget per GPU slice handed to <see cref="Engine_ServerSyncs.RunGpuUpload"/>
	/// so background bakes automatically yield after 1-2 slices per drawn frame (~1.5-3 ms/frame).
	/// </summary>
	private const long SliceGpuBudgetBytes = 16L * 1024L * 1024L;

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
	float inv_inner;
	float texel_angle;
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

bool trace_closest_hit(vec3 origin, vec3 dir, float t_min, float t_max, out float hit_dist, out vec3 hit_normal) {
	hit_dist = t_max;
	hit_normal = vec3(0.0, 1.0, 0.0);
	vec3 inv_dir = vec3(
		abs(dir.x) > 1e-8 ? 1.0 / dir.x : (dir.x >= 0.0 ? 1e8 : -1e8),
		abs(dir.y) > 1e-8 ? 1.0 / dir.y : (dir.y >= 0.0 ? 1e8 : -1e8),
		abs(dir.z) > 1e-8 ? 1.0 / dir.z : (dir.z >= 0.0 ? 1e8 : -1e8));

	float root_tmin;
	if (!intersect_aabb(origin, inv_dir, bvh_nodes.nodes[0].xyz, bvh_nodes.nodes[1].xyz, t_min, hit_dist, root_tmin)) {
		return false;
	}

	int stack[64];
	float tstack[64];
	int sp = 0;
	int node_idx = 0;
	bool any_hit = false;

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
			float tmin_l;
			float tmin_r;
			bool hit_l = intersect_aabb(origin, inv_dir, bvh_nodes.nodes[left_idx * 2].xyz, bvh_nodes.nodes[left_idx * 2 + 1].xyz, t_min, hit_dist, tmin_l);
			bool hit_r = intersect_aabb(origin, inv_dir, bvh_nodes.nodes[right_idx * 2].xyz, bvh_nodes.nodes[right_idx * 2 + 1].xyz, t_min, hit_dist, tmin_r);
			if (hit_l && hit_r) {
				if (tmin_l <= tmin_r) {
					if (sp < 64) {
						stack[sp] = right_idx;
						tstack[sp] = tmin_r;
						sp++;
					}
					node_idx = left_idx;
				} else {
					if (sp < 64) {
						stack[sp] = left_idx;
						tstack[sp] = tmin_l;
						sp++;
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

		bool popped = false;
		while (sp > 0) {
			--sp;
			if (tstack[sp] < hit_dist) {
				node_idx = stack[sp];
				popped = true;
				break;
			}
		}
		if (!popped) {
			break;
		}
	}

	return any_hit;
}

void main() {
	int ix = int(gl_GlobalInvocationID.x);
	int iy = int(gl_GlobalInvocationID.y);
	int batch_idx = int(gl_GlobalInvocationID.z);
	if (ix >= push.inner_size || iy >= push.inner_size || batch_idx >= push.batch_count) {
		return;
	}

	int out_idx = (batch_idx * push.inner_size + iy) * push.inner_size + ix;
	vec4 l0 = light_specs.lights[batch_idx * 2];
	vec4 l1 = light_specs.lights[batch_idx * 2 + 1];
	vec3 origin = l0.xyz;
	float max_range = l0.w;
	vec3 spot_fwd = l1.xyz;
	float spot_cos = l1.w;

	float u = (float(ix) + 0.5) * push.inv_inner;
	float v = (float(iy) + 0.5) * push.inv_inner;
	vec3 dir = direction_from_octahedral_uv(vec2(u, v));

	if (spot_cos > -1.5 && dot(dir, spot_fwd) < spot_cos) {
		out_buf.depth_bits[out_idx] = 0x7BFFu;
		return;
	}

	float hit_dist;
	vec3 hit_normal;
	if (trace_closest_hit(origin, dir, 0.20, max_range, hit_dist, hit_normal)) {
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

const vec3 HEMISPHERE_KERNEL[24] = vec3[24](
	vec3(0.14433756, 0.0, 0.98952854),
	vec3(-0.22866146, 0.20906423, 0.95087677),
	vec3(0.03693150, -0.40905246, 0.91175658),
	vec3(0.32491964, 0.36988959, 0.87053621),
	vec3(-0.53139067, -0.04453035, 0.84595543),
	vec3(0.41502365, -0.41264984, 0.81096935),
	vec3(0.02581462, 0.63529795, 0.77183402),
	vec3(-0.51773787, -0.45334959, 0.72554648),
	vec3(0.71382529, 0.09700912, 0.69357276),
	vec3(-0.36537892, 0.66304296, 0.65335321),
	vec3(-0.27385989, -0.73188150, 0.62413019),
	vec3(0.74587345, -0.35129258, 0.56609648),
	vec3(-0.83559561, 0.20413351, 0.51000923),
	vec3(0.42297477, 0.77453458, 0.47009423),
	vec3(0.22481063, -0.87948012, 0.41949338),
	vec3(-0.83187044, -0.43662483, 0.34256235),
	vec3(0.92288625, -0.25849190, 0.28541678),
	vec3(-0.36004096, 0.90892607, 0.21029460),
	vec3(-0.34689057, -0.92616582, 0.14793558),
	vec3(0.92853862, 0.36081260, 0.08734966),
	vec3(-0.98541063, 0.16362835, 0.04681531),
	vec3(0.23892266, 0.97083896, 0.01912430),
	vec3(0.49101505, -0.87113041, 0.00421512),
	vec3(-0.91636944, -0.40033168, 0.00105380)
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
	float dhdx = (read_ghf(xp, z0) - read_ghf(xm, z0)) / (float(max(1, xp - xm)) * push.ghf_spacing);
	float dhdz = (read_ghf(x0, zp) - read_ghf(x0, zm)) / (float(max(1, zp - zm)) * push.ghf_spacing);

	vec3 n = vec3(dhdx, 1.0, -dhdz);
	world_normal = normalize(n);
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

bool trace_closest_hit(vec3 origin, vec3 dir, float t_min, float t_max, uint flag_mask, uint flag_expected, out float hit_dist, out vec3 hit_normal) {
	hit_dist = t_max;
	hit_normal = vec3(0.0, 1.0, 0.0);
	vec3 inv_dir = vec3(
		abs(dir.x) > 1e-8 ? 1.0 / dir.x : (dir.x >= 0.0 ? 1e8 : -1e8),
		abs(dir.y) > 1e-8 ? 1.0 / dir.y : (dir.y >= 0.0 ? 1e8 : -1e8),
		abs(dir.z) > 1e-8 ? 1.0 / dir.z : (dir.z >= 0.0 ? 1e8 : -1e8));

	float root_tmin;
	if (!intersect_aabb(origin, inv_dir, bvh_nodes.nodes[0].xyz, bvh_nodes.nodes[1].xyz, t_min, hit_dist, root_tmin)) {
		return false;
	}

	int stack[64];
	float tstack[64];
	int sp = 0;
	int node_idx = 0;
	bool any_hit = false;

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
				if (t > t_min && t < hit_dist) {
					hit_dist = t;
					hit_normal = n_and_flags.xyz;
					any_hit = true;
				}
			}
		} else {
			int left_idx = left_or_first;
			int right_idx = left_or_first + 1;
			float tmin_l;
			float tmin_r;
			bool hit_l = intersect_aabb(origin, inv_dir, bvh_nodes.nodes[left_idx * 2].xyz, bvh_nodes.nodes[left_idx * 2 + 1].xyz, t_min, hit_dist, tmin_l);
			bool hit_r = intersect_aabb(origin, inv_dir, bvh_nodes.nodes[right_idx * 2].xyz, bvh_nodes.nodes[right_idx * 2 + 1].xyz, t_min, hit_dist, tmin_r);
			if (hit_l && hit_r) {
				if (tmin_l <= tmin_r) {
					if (sp < 64) {
						stack[sp] = right_idx;
						tstack[sp] = tmin_r;
						sp++;
					}
					node_idx = left_idx;
				} else {
					if (sp < 64) {
						stack[sp] = left_idx;
						tstack[sp] = tmin_l;
						sp++;
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

		bool popped = false;
		while (sp > 0) {
			--sp;
			if (tstack[sp] < hit_dist) {
				node_idx = stack[sp];
				popped = true;
				break;
			}
		}
		if (!popped) {
			break;
		}
	}

	return any_hit;
}

float eval_hemisphere_ao(vec3 world_pos, vec3 world_normal, out int hit_count) {
	vec3 tangent;
	vec3 bitangent;
	build_orthonormal_basis(world_normal, tangent, bitangent);
	vec3 ray_origin = world_pos + world_normal * 0.03;

	float inv_radius = 1.0 / max(push.max_radius, 0.1);
	float max_occ = (1.0 - push.min_ao) * 24.0;
	float occ_sum = 0.0;
	hit_count = 0;
	int far_roof_hits = 0;

	for (int r = 0; r < 24; ++r) {
		vec3 local_dir = HEMISPHERE_KERNEL[r];
		vec3 dir = normalize(tangent * local_dir.x + bitangent * local_dir.y + world_normal * local_dir.z);
		float hit_dist;
		vec3 hit_n;
		if (trace_closest_hit(ray_origin, dir, 0.04, push.max_radius, 4u, 4u, hit_dist, hit_n)) {
			float falloff = max(0.0, 1.0 - hit_dist * inv_radius);
			occ_sum += falloff * falloff;
			hit_count++;
			if (local_dir.z > 0.70 && hit_dist > 2.25) {
				far_roof_hits++;
			}
			if (occ_sum >= max_occ && (r + 1 - hit_count) >= 3) {
				break;
			}
		}
	}

	float raw_ao = clamp(1.0 - occ_sum * (1.0 / 24.0), push.min_ao, 1.0);
	if (hit_count >= 22 && far_roof_hits >= 5) {
		raw_ao = mix(raw_ao, 0.85, 0.75);
	}
	return raw_ao;
}

void main() {
	int tx = int(gl_GlobalInvocationID.x) + push.col_offset;
	int local_y = int(gl_GlobalInvocationID.y);
	if (tx >= push.terrain_w || local_y >= push.halo_h) {
		return;
	}

	int ty = clamp(push.row_offset + local_y - 1, 0, push.terrain_h - 1);
	float u = push.terrain_w > 1 ? float(tx) / float(push.terrain_w - 1) : 0.0;
	float v = push.terrain_h > 1 ? float(ty) / float(push.terrain_h - 1) : 0.0;
	float local_x = push.crop_origin_x + u * push.crop_span_x;
	float local_z = push.crop_origin_z + v * push.crop_span_z;
	float world_x = -local_x;
	float world_z = local_z;

	float bvh_min_y = bvh_nodes.nodes[0].y;
	float bvh_max_y = bvh_nodes.nodes[1].y;
	bool has_ghf = (push.ghf_w >= 2 && push.ghf_h >= 2);

	float ghf_y = bvh_min_y;
	vec3 ghf_normal = vec3(0.0, 1.0, 0.0);
	float raw_terrain_ao = 1.0;
	if (has_ghf) {
		sample_height_and_normal(local_x, local_z, ghf_y, ghf_normal);
		int terrain_hits;
		raw_terrain_ao = eval_hemisphere_ao(vec3(world_x, ghf_y, world_z), ghf_normal, terrain_hits);
	}

	float walk_y = ghf_y;
	float raw_walk_ao = raw_terrain_ao;
	bool found_arch = false;

	float cur_y = has_ghf ? (ghf_y - 0.60) : (bvh_min_y - 0.25);
	float max_scan_y = bvh_max_y + 0.50;
	vec3 up_dir = vec3(0.0, 1.0, 0.0);

	for (int step_idx = 0; step_idx < 8; ++step_idx) {
		if (cur_y > max_scan_y) {
			break;
		}
		float cand_dist;
		vec3 cand_n;
		if (!trace_closest_hit(vec3(world_x, cur_y, world_z), up_dir, 0.0, max_scan_y - cur_y, 6u, 4u, cand_dist, cand_n)) {
			break;
		}
		float cand_y = cur_y + cand_dist;
		if (abs(cand_n.y) < 0.65) {
			cur_y = cand_y + 0.04;
			continue;
		}

		vec3 up_n = cand_n.y < 0.0 ? -cand_n : cand_n;
		float head_dist;
		vec3 head_n;
		bool blocked = trace_closest_hit(vec3(world_x, cand_y + 0.03, world_z), up_dir, 0.0, 1.80, 4u, 4u, head_dist, head_n);
		if (!blocked) {
			walk_y = cand_y;
			int arch_hits;
			raw_walk_ao = eval_hemisphere_ao(vec3(world_x, walk_y, world_z), up_n, arch_hits);
			found_arch = true;
			break;
		}

		if (head_dist < 0.35) {
			cur_y = cand_y + 0.015 + head_dist;
			continue;
		}

		int cand_hits;
		float cand_ao = eval_hemisphere_ao(vec3(world_x, cand_y, world_z), up_n, cand_hits);
		if (cand_hits < 23) {
			walk_y = cand_y;
			raw_walk_ao = cand_ao;
			found_arch = true;
			break;
		}

		cur_y = cand_y + 0.015 + head_dist;
	}

	if (!has_ghf && found_arch) {
		ghf_y = walk_y;
		raw_terrain_ao = raw_walk_ao;
	}

	out_buf.samples[local_y * push.terrain_w + tx] = vec4(raw_terrain_ao, ghf_y, raw_walk_ao, walk_y);
}
";

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

void main() {
	int tx = int(gl_GlobalInvocationID.x);
	int out_y = int(gl_GlobalInvocationID.y);
	if (tx >= push.terrain_w || out_y >= push.strip_h) {
		return;
	}

	int mid_cy = out_y + 1;
	vec4 center = mid_buf.samples[mid_cy * push.terrain_w + tx];
	float center_terr_ao = center.x;
	float center_terr_h = center.y;
	float center_walk_ao = center.z;
	float center_walk_h = center.w;

	float sum_terr_ao = 0.0;
	float sum_terr_w = 0.0;
	float sum_walk_ao = 0.0;
	float sum_walk_w = 0.0;

	for (int dy = -1; dy <= 1; ++dy) {
		int ny = mid_cy + dy;
		int row_base = ny * push.terrain_w;
		for (int dx = -1; dx <= 1; ++dx) {
			int nx = clamp(tx + dx, 0, push.terrain_w - 1);
			vec4 s = mid_buf.samples[row_base + nx];
			float spatial_w = (dx == 0 && dy == 0) ? 1.0 : ((dx == 0 || dy == 0) ? 0.6 : 0.36);

			float dt = s.y - center_terr_h;
			float dt2 = dt * dt * 2.0;
			float wt = spatial_w / (1.0 + dt2 + 0.5 * dt2 * dt2);
			sum_terr_ao += s.x * wt;
			sum_terr_w += wt;

			float dw = s.w - center_walk_h;
			float dw2 = dw * dw * 6.0;
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
		Func<bool>? isCancelled = null)
	{
		if (lights.Count == 0 || worldBvh.TriangleCount == 0 || Engine_AppRoot.IsTestHost)
		{
			return false;
		}

		lock (SyncRoot)
		{
			if (isCancelled?.Invoke() == true)
			{
				return false;
			}

			long bvhBytes = (long)worldBvh.GpuNodeBytes + worldBvh.GpuTriangleBytes;

			// Batch up to 12 lights per GPU slice (~0.8-2.0 ms GPU work per frame slice).
			long bytesPerLight = (long)innerSize * innerSize * sizeof(uint);
			int maxBatchLights = (int)Math.Clamp(
				Math.Min(Refii.AndroidRuntime.AndroidBakedShadows.ShadowBatchLights(12), 16L * 1024L * 1024L / Math.Max(1L, bytesPerLight)),
				1L,
				lights.Count);

			byte[] lightBytes = new byte[maxBatchLights * 32];
			int outBufferBytes = (int)(maxBatchLights * bytesPerLight);
			float invInner = 1.0f / innerSize;
			byte[] push = new byte[16];
			int groupsX = (innerSize + 7) / 8;
			int groupsY = (innerSize + 7) / 8;

			RenderingDevice? rd = null;
			Rid shader = default;
			Rid pipeline = default;
			Rid nodeBuf = default;
			Rid triBuf = default;
			Rid lightBuf = default;
			Rid outBuf = default;
			Rid uniformSet = default;

			try
			{
				bool initOk = false;
				string? compileErr = null;
				Engine_ServerSyncs.RunGpuUpload(bvhBytes + outBufferBytes, () =>
				{
					rd = RenderingServer.CreateLocalRenderingDevice();
					if (rd is null)
					{
						return;
					}

					RDShaderSpirV spirv = rd.CompileSpirV(new RDShaderSource
					{
						SourceCompute = ShadowAtlasShaderSource,
						Language = RenderingDevice.ShaderLanguage.Glsl,
					});
					if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
					{
						compileErr = spirv.CompileErrorCompute;
						return;
					}

					shader = rd.Shader(spirv, "gpu_shadow_atlas_baker");
					if (!shader.IsValid)
					{
						return;
					}

					pipeline = rd.ComputePipeline(shader);
					if (!pipeline.IsValid)
					{
						return;
					}

					UploadBvh(rd, worldBvh, out nodeBuf, out triBuf);
					lightBuf = rd.StorageBufferCreate((uint)lightBytes.Length, lightBytes);
					outBuf = rd.StorageBufferCreate((uint)outBufferBytes);
					uniformSet = rd.UniformSet(new RDUniform[]
					{
						CreateStorageUniform(nodeBuf, 0),
						CreateStorageUniform(triBuf, 1),
						CreateStorageUniform(lightBuf, 2),
						CreateStorageUniform(outBuf, 3),
					}, shader, 0);
					initOk = uniformSet.IsValid;
				});

				if (!initOk || rd is null)
				{
					if (!string.IsNullOrEmpty(compileErr))
					{
						GD.PushWarning($"[gpu-bake] shadow atlas compute shader compile error: {compileErr}");
					}
					Interlocked.Increment(ref _cpuFallbackCount);
					return false;
				}

				for (int batchStart = 0; batchStart < lights.Count; batchStart += maxBatchLights)
				{
					if (isCancelled?.Invoke() == true)
					{
						return false;
					}

					int batchCount = Math.Min(maxBatchLights, lights.Count - batchStart);
					Span<float> lFloats = MemoryMarshal.Cast<byte, float>(lightBytes.AsSpan());
					for (int k = 0; k < batchCount; k++)
					{
						Engine_LevelShadowMaps.BakeLightSpec l = lights[batchStart + k];
						int o = k * 8;
						lFloats[o] = l.WorldOrigin.X;
						lFloats[o + 1] = l.WorldOrigin.Y;
						lFloats[o + 2] = l.WorldOrigin.Z;
						lFloats[o + 3] = l.MaxRange;
						lFloats[o + 4] = l.SpotForward.X;
						lFloats[o + 5] = l.SpotForward.Y;
						lFloats[o + 6] = l.SpotForward.Z;
						lFloats[o + 7] = l.IsSpot ? l.SpotCosLimit : -2.0f;
					}

					BitConverter.TryWriteBytes(push.AsSpan(0), innerSize);
					BitConverter.TryWriteBytes(push.AsSpan(4), batchCount);
					BitConverter.TryWriteBytes(push.AsSpan(8), invInner);
					BitConverter.TryWriteBytes(push.AsSpan(12), texelAngle);

					if (Engine_RdHealth.IsDeviceLost)
					{
						return false;
					}

					uint readBytes = (uint)(batchCount * bytesPerLight);
					byte[]? rawOut = null;
					RenderingDevice localRd = rd;
					Engine_ServerSyncs.RunGpuUpload(Engine_ServerSyncs.MaxBackgroundUploadBytesPerFrame, () =>
					{
						if (Engine_RdHealth.IsDeviceLost)
						{
							return;
						}
						localRd.BufferUpdate(lightBuf, 0, (uint)(batchCount * 32), lightBytes);
						long list = localRd.ComputeListBegin();
						localRd.ComputeListBindComputePipeline(list, pipeline);
						localRd.ComputeListBindUniformSet(list, uniformSet, 0);
						localRd.ComputeListSetPushConstant(list, push, (uint)push.Length);
						localRd.ComputeListDispatch(list, (uint)groupsX, (uint)groupsY, (uint)batchCount);
						localRd.ComputeListEnd();
						localRd.Submit();
					});
					Engine_ServerSyncs.RunGpuUpload(Engine_ServerSyncs.MaxBackgroundUploadBytesPerFrame, () =>
					{
						localRd.Sync();
						rawOut = localRd.BufferGetData(outBuf, 0, readBytes);
					});

					if (rawOut is null)
					{
						return false;
					}

					byte[] batchBytes = rawOut;
					int currentBatchStart = batchStart;
					Parallel.For(0, batchCount, k =>
					{
						int lightIdx = currentBatchStart + k;
						int sliceIndex = lightIdx / lightsPerMap;
						int localIndex = lightIdx % lightsPerMap;
						int tileX = (localIndex % tilesPerRow) * tileTotalSize;
						int tileY = (localIndex / tilesPerRow) * tileTotalSize;
						int sliceOffset = sliceIndex * sliceTexelCount;

						ReadOnlySpan<uint> uintSpan = MemoryMarshal.Cast<byte, uint>(
							batchBytes.AsSpan(k * innerSize * innerSize * sizeof(uint), innerSize * innerSize * sizeof(uint)));

						for (int iy = 0; iy < innerSize; iy++)
						{
							int py = tileY + 1 + iy;
							int dstRow = sliceOffset + py * atlasResolution + tileX + 1;
							int srcRow = iy * innerSize;
							for (int ix = 0; ix < innerSize; ix++)
							{
								texels[dstRow + ix] = (ushort)(uintSpan[srcRow + ix] & 0xFFFFu);
							}
						}

						// Populate 1-texel octahedral wrapped border gutter around the tile.
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
								Vector2 wrapped = Engine_LevelShadowMaps.WrapOctahedralUv(new Vector2(rawU, rawV));
								int srcIx = Math.Clamp((int)MathF.Floor(wrapped.X * innerSize), 0, innerSize - 1);
								int srcIy = Math.Clamp((int)MathF.Floor(wrapped.Y * innerSize), 0, innerSize - 1);

								int srcIdx = sliceOffset + (tileY + 1 + srcIy) * atlasResolution + (tileX + 1 + srcIx);
								int dstIdx = sliceOffset + (tileY + by) * atlasResolution + (tileX + bx);
								texels[dstIdx] = texels[srcIdx];
							}
						}
					});
				}

				Interlocked.Increment(ref _gpuShadowBakeCount);
				return true;
			}
			catch (Exception ex)
			{
				Interlocked.Increment(ref _cpuFallbackCount);
				GD.PushWarning($"[gpu-bake] shadow atlas GPU bake fell back to CPU: {ex.Message}");
				return false;
			}
			finally
			{
				if (rd is not null)
				{
					RenderingDevice cleanupRd = rd;
					Engine_ServerSyncs.RunGpuUpload(4096L, () =>
					{
						FreeIfValid(cleanupRd, outBuf);
						FreeIfValid(cleanupRd, lightBuf);
						FreeIfValid(cleanupRd, triBuf);
						FreeIfValid(cleanupRd, nodeBuf);
						FreeIfValid(cleanupRd, pipeline);
						FreeIfValid(cleanupRd, shader);
						// A local device is an Object, not a RefCounted: Dispose only drops the C# handle and
						// left the device and its memory pools behind, two a baked area.
						cleanupRd.Free();
					});
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
		Func<bool>? isCancelled = null)
	{
		terrainAoBytes = Array.Empty<byte>();
		terrainMinH = 0.0f;
		terrainMaxH = 1.0f;

		if (terrainW <= 0 || terrainH <= 0 || worldBvh.TriangleCount == 0 || Engine_AppRoot.IsTestHost)
		{
			return false;
		}

		lock (SyncRoot)
		{
			if (isCancelled?.Invoke() == true)
			{
				return false;
			}

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
				if (float.IsFinite(worldBvh.RootMin.Y))
				{
					terrainMinH = MathF.Min(terrainMinH, worldBvh.RootMin.Y);
				}
				if (float.IsFinite(worldBvh.RootMax.Y))
				{
					terrainMaxH = MathF.Max(terrainMaxH, worldBvh.RootMax.Y);
				}
			}
			else
			{
				terrainMinH = float.IsFinite(worldBvh.RootMin.Y) ? worldBvh.RootMin.Y : 0.0f;
				terrainMaxH = float.IsFinite(worldBvh.RootMax.Y) ? worldBvh.RootMax.Y : 1.0f;
				if (terrainMaxH <= terrainMinH)
				{
					terrainMaxH = terrainMinH + 1.0f;
				}
			}

			float invHeightSpan = (terrainMaxH - terrainMinH) > 1e-4f ? 1.0f / (terrainMaxH - terrainMinH) : 0.0f;

			long bvhBytes = (long)worldBvh.GpuNodeBytes + worldBvh.GpuTriangleBytes;
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

			// Target ~262K-524K texels per strip slice (~1-2.5 ms GPU compute per slice) so gameplay stays silky smooth
			// and temporary VRAM stays tiny (~8.5 MB even at 4K/8K).
			int targetStripRows = Refii.AndroidRuntime.AndroidBakedShadows.AoStripRows(
				terrainW, terrainW >= 8192 ? 64 : (terrainW >= 4096 ? 128 : 256));
			int maxStripH = Math.Clamp(targetStripRows, 1, terrainH);
			int maxHaloH = maxStripH + 2;
			int midBufferBytes = terrainW * maxHaloH * 16; // fp32 vec4(raw_terr_ao, ghf_y, raw_walk_ao, walk_y)
			int outBufferBytes = terrainW * maxStripH * sizeof(uint); // packed RGBA8 (1 uint per texel)

			int ghfW = hasGhf ? ghf.Width : 0;
			int ghfH = hasGhf ? ghf.Height : 0;
			float ghfOriginX = hasGhf ? ghf.OriginX : 0.0f;
			float ghfOriginZ = hasGhf ? ghf.OriginZ : 0.0f;
			float ghfSpacing = hasGhf ? ghf.Spacing : 1.0f;

			RenderingDevice? rd = null;
			Rid traceShader = default;
			Rid tracePipeline = default;
			Rid filterShader = default;
			Rid filterPipeline = default;
			Rid nodeBuf = default;
			Rid triBuf = default;
			Rid ghfBuf = default;
			Rid midBuf = default;
			Rid outBuf = default;
			Rid traceSet = default;
			Rid filterSet = default;

			try
			{
				bool initOk = false;
				string? compileErr = null;
				Engine_ServerSyncs.RunGpuUpload(bvhBytes + ghfBytes.LongLength + midBufferBytes, () =>
				{
					rd = RenderingServer.CreateLocalRenderingDevice();
					if (rd is null)
					{
						return;
					}

					RDShaderSpirV traceSpirv = rd.CompileSpirV(new RDShaderSource
					{
						SourceCompute = GroundAoShaderSource,
						Language = RenderingDevice.ShaderLanguage.Glsl,
					});
					if (!string.IsNullOrEmpty(traceSpirv.CompileErrorCompute))
					{
						compileErr = traceSpirv.CompileErrorCompute;
						return;
					}

					RDShaderSpirV filterSpirv = rd.CompileSpirV(new RDShaderSource
					{
						SourceCompute = GroundAoFilterShaderSource,
						Language = RenderingDevice.ShaderLanguage.Glsl,
					});
					if (!string.IsNullOrEmpty(filterSpirv.CompileErrorCompute))
					{
						compileErr = filterSpirv.CompileErrorCompute;
						return;
					}

					traceShader = rd.Shader(traceSpirv, "gpu_ground_ao_baker");
					filterShader = rd.Shader(filterSpirv, "gpu_ground_ao_filter");
					if (!traceShader.IsValid || !filterShader.IsValid)
					{
						return;
					}

					tracePipeline = rd.ComputePipeline(traceShader);
					filterPipeline = rd.ComputePipeline(filterShader);
					if (!tracePipeline.IsValid || !filterPipeline.IsValid)
					{
						return;
					}

					UploadBvh(rd, worldBvh, out nodeBuf, out triBuf);
					ghfBuf = rd.StorageBufferCreate((uint)ghfBytes.Length, ghfBytes);
					midBuf = rd.StorageBufferCreate((uint)midBufferBytes);
					outBuf = rd.StorageBufferCreate((uint)outBufferBytes);

					traceSet = rd.UniformSet(new RDUniform[]
					{
						CreateStorageUniform(nodeBuf, 0),
						CreateStorageUniform(triBuf, 1),
						CreateStorageUniform(ghfBuf, 2),
						CreateStorageUniform(midBuf, 3),
					}, traceShader, 0);

					filterSet = rd.UniformSet(new RDUniform[]
					{
						CreateStorageUniform(midBuf, 0),
						CreateStorageUniform(outBuf, 1),
					}, filterShader, 0);

					initOk = traceSet.IsValid && filterSet.IsValid;
				});

				if (!initOk || rd is null)
				{
					if (!string.IsNullOrEmpty(compileErr))
					{
						GD.PushWarning($"[gpu-bake] ground AO compute shader compile error: {compileErr}");
					}
					Interlocked.Increment(ref _cpuFallbackCount);
					return false;
				}

				byte[] resultRgba8 = new byte[terrainW * terrainH * 4];
				byte[] tracePush = new byte[64];
				byte[] filterPush = new byte[32];
				int traceGroupsX = (terrainW + 7) / 8;
				int filterGroupsX = traceGroupsX;
				int traceColumns = Refii.AndroidRuntime.AndroidBakedShadows.AoTraceColumns(terrainW, maxStripH + 2);
				float minHCopy = terrainMinH;

				for (int rowOffset = 0; rowOffset < terrainH; rowOffset += maxStripH)
				{
					if (isCancelled?.Invoke() == true)
					{
						return false;
					}

					int stripH = Math.Min(maxStripH, terrainH - rowOffset);
					int haloH = stripH + 2;
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

					if (Engine_RdHealth.IsDeviceLost)
					{
						return false;
					}

					int rowStrideBytes = terrainW * sizeof(uint);
					uint readBytes = (uint)(stripH * rowStrideBytes);
					byte[]? stripOut = null;
					RenderingDevice localRd = rd;
					if (traceColumns < terrainW)
					{
						if (!Refii.AndroidRuntime.AndroidBakedShadows.TraceAoInPieces(localRd, tracePipeline, traceSet, tracePush,
							    traceColumns, terrainW, traceGroupsY, filterPipeline, filterSet, filterPush, filterGroupsX, filterGroupsY))
						{
							return false;
						}
					}
					else
					{
						Engine_ServerSyncs.RunGpuUpload(Engine_ServerSyncs.MaxBackgroundUploadBytesPerFrame, () =>
						{
							if (Engine_RdHealth.IsDeviceLost)
							{
								return;
							}
							long list1 = localRd.ComputeListBegin();
							localRd.ComputeListBindComputePipeline(list1, tracePipeline);
							localRd.ComputeListBindUniformSet(list1, traceSet, 0);
							localRd.ComputeListSetPushConstant(list1, tracePush, (uint)tracePush.Length);
							localRd.ComputeListDispatch(list1, (uint)traceGroupsX, (uint)traceGroupsY, 1);
							localRd.ComputeListEnd();

							long list2 = localRd.ComputeListBegin();
							localRd.ComputeListBindComputePipeline(list2, filterPipeline);
							localRd.ComputeListBindUniformSet(list2, filterSet, 0);
							localRd.ComputeListSetPushConstant(list2, filterPush, (uint)filterPush.Length);
							localRd.ComputeListDispatch(list2, (uint)filterGroupsX, (uint)filterGroupsY, 1);
							localRd.ComputeListEnd();

							localRd.Submit();
						});
					}
					Engine_ServerSyncs.RunGpuUpload(Engine_ServerSyncs.MaxBackgroundUploadBytesPerFrame, () =>
					{
						localRd.Sync();
						stripOut = localRd.BufferGetData(outBuf, 0, readBytes);
					});

					if (stripOut is null)
					{
						return false;
					}

					Buffer.BlockCopy(stripOut, 0, resultRgba8, rowOffset * rowStrideBytes, stripH * rowStrideBytes);
				}

				terrainAoBytes = resultRgba8;
				Interlocked.Increment(ref _gpuAoBakeCount);
				return true;
			}
			catch (Exception ex)
			{
				Interlocked.Increment(ref _cpuFallbackCount);
				GD.PushWarning($"[gpu-bake] ground AO GPU bake fell back to CPU: {ex.Message}");
				return false;
			}
			finally
			{
				if (rd is not null)
				{
					RenderingDevice cleanupRd = rd;
					Engine_ServerSyncs.RunGpuUpload(4096L, () =>
					{
						FreeIfValid(cleanupRd, outBuf);
						FreeIfValid(cleanupRd, midBuf);
						FreeIfValid(cleanupRd, ghfBuf);
						FreeIfValid(cleanupRd, triBuf);
						FreeIfValid(cleanupRd, nodeBuf);
						FreeIfValid(cleanupRd, filterPipeline);
						FreeIfValid(cleanupRd, tracePipeline);
						FreeIfValid(cleanupRd, filterShader);
						FreeIfValid(cleanupRd, traceShader);
						// A local device is an Object, not a RefCounted: Dispose only drops the C# handle and
						// left the device and its memory pools behind, two a baked area.
						cleanupRd.Free();
					});
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

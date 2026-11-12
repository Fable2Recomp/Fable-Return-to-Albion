using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Godot;
using Refii.Lighting;

namespace Refii.Models;

/// <summary>
/// Per-model self-ambient-occlusion extractor and offline BVH hemisphere raytracer for the
/// Modern lighting pipeline.
/// </summary>
/// <remarks>
/// Populates <c>Mesh.ArrayType.Custom0</c> on <see cref="ArrayMesh"/> surfaces in Modern mode:
/// <list type="bullet">
/// <item><description><c>CUSTOM0.x</c>: Per-vertex model self-AO in <c>[0.05, 1.0]</c>, sourced from shipped PRT L0 visibility when available or BVH-raytraced hemisphere self-AO otherwise.</description></item>
/// <item><description><c>CUSTOM0.y</c>: Canonical file source vertex index (<c>[0 .. sourceVertexTotal - 1]</c>) so the vertex shader can index <c>fable_static_prop_ao</c> via <c>ao_vertex_offset + int(CUSTOM0.y + 0.5)</c>.</description></item>
/// </list>
/// </remarks>
public static class Engine_ModelSelfAo
{
	public const uint Magic = 0x4F414D52u; // "RMAO" little-endian
	public const uint Version = 2u;
	public const int RayCount = 24;
	public const float DefaultMaxRadius = 1.5f;
	public const float MinSelfAo = 0.05f;

	private static readonly Vector3[] HemisphereKernel = BuildCosineFibonacciHemisphere(RayCount);

	private static readonly ConcurrentDictionary<string, float[]> MemoryCache =
		new(StringComparer.OrdinalIgnoreCase);

	private static readonly ConcurrentDictionary<string, byte> InFlightBackgroundBakes =
		new(StringComparer.OrdinalIgnoreCase);

	public static string CacheModelsAoDirectory =>
		Path.Combine(Refii.Core.Engine_Title.Active.CacheDirectory, "ao", "models");

	public static void ClearMemoryCache()
	{
		MemoryCache.Clear();
		InFlightBackgroundBakes.Clear();
	}

	/// <summary>
	/// Precomputes a cosine-weighted Fibonacci hemisphere kernel where Z+ is the local surface normal.
	/// Because directions are already cosine-weighted, the Monte Carlo estimator is the simple average
	/// of unoccluded/distance-weighted samples.
	/// </summary>
	public static Vector3[] BuildCosineFibonacciHemisphere(int count)
	{
		var dirs = new Vector3[count];
		float goldenAngle = MathF.PI * (3.0f - MathF.Sqrt(5.0f));
		for (int i = 0; i < count; i++)
		{
			float u = (i + 0.5f) / count;
			float r = MathF.Sqrt(u);
			float z = MathF.Sqrt(MathF.Max(0.0f, 1.0f - u));
			float theta = i * goldenAngle;
			dirs[i] = new Vector3(r * MathF.Cos(theta), r * MathF.Sin(theta), z).Normalized();
		}

		return dirs;
	}

	/// <summary>
	/// Extracts canonical per-source-vertex self-AO from shipped PRT spherical harmonics transfer
	/// vectors (<see cref="Engine_MdlTransfer"/>) when present on the model.
	/// </summary>
	public static bool TryExtractFromPrt(
		byte[] data,
		MdlInfo info,
		int sourceVertexTotal,
		out float[] selfAo)
	{
		selfAo = Array.Empty<float>();
		if (sourceVertexTotal <= 0 || info.ShProbeData.Length == 0 || info.ShDescriptor.Length < 8)
		{
			return false;
		}

		var allSource = new int[sourceVertexTotal];
		for (int i = 0; i < sourceVertexTotal; i++)
		{
			allSource[i] = i;
		}

		if (!Engine_MdlTransfer.TryExpand(data, info, allSource, out float[] tr, out float[] tg, out float[] tb))
		{
			return false;
		}

		const float invUnoccludedL0 = 1.0f / (0.2820948f * MathF.PI); // 1 / (Y_0^0 * pi) ~= 1.128379
		var result = new float[sourceVertexTotal];
		bool anyValid = false;

		for (int v = 0; v < sourceVertexTotal; v++)
		{
			int at = v * 4;
			float l0 = (tr[at] + tg[at] + tb[at]) * (1.0f / 3.0f);
			if (!float.IsFinite(l0) || l0 <= 0.0f)
			{
				result[v] = 1.0f;
				continue;
			}

			anyValid = true;
			float vis = Math.Clamp(l0 * invUnoccludedL0, MinSelfAo, 1.0f);
			result[v] = vis;
		}

		if (!anyValid)
		{
			return false;
		}

		selfAo = result;
		return true;
	}

	/// <summary>
	/// Extracts canonical source vertex positions and area/occurrence-averaged normals from decoded
	/// <see cref="MdlGeometry"/> surfaces.
	/// </summary>
	public static bool TryExtractCanonicalSourceVertices(
		IReadOnlyList<MdlGeometry> geometries,
		int sourceVertexTotal,
		out Vector3[] sourcePositions,
		out Vector3[] sourceNormals,
		out bool[] hasVertex)
	{
		sourcePositions = new Vector3[sourceVertexTotal];
		sourceNormals = new Vector3[sourceVertexTotal];
		hasVertex = new bool[sourceVertexTotal];
		bool any = false;

		foreach (MdlGeometry geo in geometries)
		{
			int count = Math.Min(geo.VertexCount, geo.SourceVertices.Length);
			float[] pos = geo.Positions;
			float[] nrm = geo.Normals;
			for (int v = 0; v < count; v++)
			{
				int srcIdx = geo.SourceVertices[v];
				if ((uint)srcIdx >= (uint)sourceVertexTotal)
				{
					continue;
				}

				int pAt = v * 3;
				if (pAt + 2 >= pos.Length)
				{
					continue;
				}

				Vector3 p = new(pos[pAt], pos[pAt + 1], pos[pAt + 2]);
				if (!p.IsFinite())
				{
					continue;
				}

				Vector3 n = pAt + 2 < nrm.Length
					? new Vector3(nrm[pAt], nrm[pAt + 1], nrm[pAt + 2])
					: Vector3.Up;
				if (!n.IsFinite() || n.LengthSquared() < 1e-8f)
				{
					n = Vector3.Up;
				}

				if (!hasVertex[srcIdx])
				{
					sourcePositions[srcIdx] = p;
					sourceNormals[srcIdx] = n;
					hasVertex[srcIdx] = true;
					any = true;
				}
				else
				{
					sourceNormals[srcIdx] += n;
				}
			}
		}

		if (!any)
		{
			return false;
		}

		for (int i = 0; i < sourceVertexTotal; i++)
		{
			if (hasVertex[i])
			{
				Vector3 n = sourceNormals[i];
				sourceNormals[i] = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Up;
			}
			else
			{
				sourceNormals[i] = Vector3.Up;
			}
		}

		return true;
	}

	/// <summary>
	/// Ultra-fast O(T) geometric 1-ring edge-horizon concavity + base ground-contact self-AO estimator.
	/// Runs in &lt;0.15 ms even on 15,000-vertex statues with zero BVH construction, capturing tight
	/// garment folds, statue crevices, fire-pit stone corners, and base contact that coarse cluster PRT
	/// misses or that exceed synchronous BVH budgets.
	/// </summary>
	public static float[] ComputeFastCreviceAndBaseAo(
		IReadOnlyList<MdlGeometry> geometries,
		int sourceVertexTotal,
		bool includeGroundPlane = true)
	{
		if (sourceVertexTotal <= 0)
		{
			return Array.Empty<float>();
		}

		var result = new float[sourceVertexTotal];
		Array.Fill(result, 1.0f);

		if (!TryExtractCanonicalSourceVertices(
			geometries, sourceVertexTotal,
			out Vector3[] sourcePositions,
			out Vector3[] sourceNormals,
			out bool[] hasVertex))
		{
			return result;
		}

		return ComputeFastCreviceAndBaseAoFromExtracted(
			geometries, sourcePositions, sourceNormals, hasVertex, sourceVertexTotal, includeGroundPlane);
	}

	private static float[] ComputeFastCreviceAndBaseAoFromExtracted(
		IReadOnlyList<MdlGeometry> geometries,
		Vector3[] sourcePositions,
		Vector3[] sourceNormals,
		bool[] hasVertex,
		int sourceVertexTotal,
		bool includeGroundPlane)
	{
		var result = new float[sourceVertexTotal];
		Array.Fill(result, 1.0f);

		var concSum = new float[sourceVertexTotal];
		var concCount = new int[sourceVertexTotal];
		float minY = float.PositiveInfinity;
		float maxY = float.NegativeInfinity;
		const float maxEdgeRadius = 0.85f;
		const float maxEdgeRadiusSq = maxEdgeRadius * maxEdgeRadius;
		const float invMaxEdgeRadius = 1.0f / maxEdgeRadius;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		void AccumulateDirectedEdge(int sa, int sb)
		{
			if ((uint)sa >= (uint)sourceVertexTotal || (uint)sb >= (uint)sourceVertexTotal || sa == sb ||
			    !hasVertex[sa] || !hasVertex[sb])
			{
				return;
			}

			Vector3 d = sourcePositions[sb] - sourcePositions[sa];
			float lenSq = d.LengthSquared();
			if (lenSq <= 1e-8f || lenSq >= maxEdgeRadiusSq)
			{
				return;
			}

			float invLen = 1.0f / MathF.Sqrt(lenSq);
			float len = lenSq * invLen;
			float sinElev = d.Dot(sourceNormals[sa]) * invLen;
			float distWeight = 0.45f + 0.55f * (1.0f - len * invMaxEdgeRadius);
			if (sinElev > 0.0f)
			{
				concSum[sa] += sinElev * distWeight;
			}

			concCount[sa]++;
		}

		foreach (MdlGeometry geo in geometries)
		{
			if (geo.VertexCount == 0 || geo.TriangleCount == 0)
			{
				continue;
			}

			float[] pos = geo.Positions;
			uint[] indices = geo.Indices;
			int[] srcMap = geo.SourceVertices;
			int srcLen = srcMap.Length;

			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				int v0 = (int)indices[i];
				int v1 = (int)indices[i + 1];
				int v2 = (int)indices[i + 2];

				int p0 = v0 * 3;
				int p1 = v1 * 3;
				int p2 = v2 * 3;
				if (p0 + 2 >= pos.Length || p1 + 2 >= pos.Length || p2 + 2 >= pos.Length)
				{
					continue;
				}

				float y0 = pos[p0 + 1];
				float y1 = pos[p1 + 1];
				float y2 = pos[p2 + 1];
				if (float.IsFinite(y0) && float.IsFinite(y1) && float.IsFinite(y2))
				{
					minY = MathF.Min(minY, MathF.Min(y0, MathF.Min(y1, y2)));
					maxY = MathF.Max(maxY, MathF.Max(y0, MathF.Max(y1, y2)));
				}

				int s0 = (uint)v0 < (uint)srcLen ? srcMap[v0] : -1;
				int s1 = (uint)v1 < (uint)srcLen ? srcMap[v1] : -1;
				int s2 = (uint)v2 < (uint)srcLen ? srcMap[v2] : -1;

				AccumulateDirectedEdge(s0, s1);
				AccumulateDirectedEdge(s1, s0);
				AccumulateDirectedEdge(s1, s2);
				AccumulateDirectedEdge(s2, s1);
				AccumulateDirectedEdge(s2, s0);
				AccumulateDirectedEdge(s0, s2);
			}
		}

		bool groundValid = includeGroundPlane && float.IsFinite(minY) && (maxY - minY) > 0.1f;
		for (int v = 0; v < sourceVertexTotal; v++)
		{
			if (!hasVertex[v])
			{
				continue;
			}

			float meanElev = concCount[v] > 0 ? concSum[v] / concCount[v] : 0.0f;
			float creviceOcc = Math.Clamp(meanElev * 1.35f, 0.0f, 0.68f);
			float ao = 1.0f - creviceOcc;

			if (groundValid)
			{
				float dy = sourcePositions[v].Y - minY;
				if (dy >= -0.02f && dy < 0.6f)
				{
					float hFactor = 1.0f - Math.Clamp(dy / 0.6f, 0.0f, 1.0f);
					float nGroundVis = Math.Clamp((1.0f - sourceNormals[v].Y) * 0.5f, 0.0f, 1.0f);
					float groundOcc = 0.35f * hFactor * hFactor * nGroundVis;
					ao *= 1.0f - groundOcc;
				}
			}

			result[v] = Math.Clamp(ao, MinSelfAo, 1.0f);
		}

		return result;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static float CombineMacroAndCreviceAo(float macroAo, float creviceAo)
	{
		float lo = MathF.Min(macroAo, creviceAo);
		float hi = MathF.Max(macroAo, creviceAo);
		return Math.Clamp(lo * (0.88f + 0.12f * hi), MinSelfAo, 1.0f);
	}

	/// <summary>
	/// Computes canonical per-source-vertex self-AO by building a local <see cref="Engine_LevelShadowMaps.BvhTree"/>
	/// from <paramref name="geometries"/> and tracing a dual-scale 24-ray cosine-weighted Fibonacci hemisphere per vertex,
	/// combined with 1-ring geometric crevice concavity.
	/// </summary>
	public static float[] ComputeBvhSelfAo(
		IReadOnlyList<MdlGeometry> geometries,
		int sourceVertexTotal,
		bool includeGroundPlane = true,
		float maxRadius = DefaultMaxRadius)
	{
		if (sourceVertexTotal <= 0)
		{
			return Array.Empty<float>();
		}

		var result = new float[sourceVertexTotal];
		Array.Fill(result, 1.0f);

		if (!TryExtractCanonicalSourceVertices(
			geometries, sourceVertexTotal,
			out Vector3[] sourcePositions,
			out Vector3[] sourceNormals,
			out bool[] hasVertex))
		{
			return result;
		}

		float[] creviceAo = ComputeFastCreviceAndBaseAoFromExtracted(
			geometries, sourcePositions, sourceNormals, hasVertex, sourceVertexTotal, includeGroundPlane: false);

		var bvhTris = new List<Engine_LevelShadowMaps.BvhTriangle>(512);
		float minY = float.PositiveInfinity;
		float maxY = float.NegativeInfinity;

		foreach (MdlGeometry geo in geometries)
		{
			if (geo.VertexCount == 0 || geo.TriangleCount == 0)
			{
				continue;
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

				if ((b - a).Cross(c - a).LengthSquared() <= 1e-10f)
				{
					continue;
				}

				minY = MathF.Min(minY, MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
				maxY = MathF.Max(maxY, MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
				bvhTris.Add(new Engine_LevelShadowMaps.BvhTriangle(a, b, c));
			}
		}

		if (bvhTris.Count == 0)
		{
			return result;
		}

		var bvh = new Engine_LevelShadowMaps.BvhTree(bvhTris);
		float effectiveRadius = MathF.Max(0.15f, maxRadius);
		float tightRadius = Math.Clamp(effectiveRadius * 0.30f, 0.12f, 0.42f);
		float invRadius = 1.0f / effectiveRadius;
		float invTightRadius = 1.0f / tightRadius;
		const float tMin = 0.012f;
		float invRayCount = 1.0f / HemisphereKernel.Length;
		bool groundValid = includeGroundPlane && float.IsFinite(minY) && (maxY - minY) > 0.1f;

		void EvaluateVertex(int v)
		{
			if (!hasVertex[v])
			{
				return;
			}

			Vector3 pos = sourcePositions[v];
			Vector3 n = sourceNormals[v];
			BuildOrthonormalBasis(n, out Vector3 tangent, out Vector3 bitangent);

			Vector3 rayOrigin = pos + n * 0.008f;
			float occSum = 0.0f;

			for (int r = 0; r < HemisphereKernel.Length; r++)
			{
				Vector3 local = HemisphereKernel[r];
				Vector3 dir = (tangent * local.X + bitangent * local.Y + n * local.Z).Normalized();
				if (bvh.TraceClosestHit(rayOrigin, dir, tMin, effectiveRadius, out float hitDist, out _))
				{
					float macroFalloff = MathF.Max(0.0f, 1.0f - hitDist * invRadius);
					float tightFalloff = MathF.Max(0.0f, 1.0f - hitDist * invTightRadius);
					occSum += MathF.Min(1.25f, (macroFalloff * macroFalloff) + 0.35f * (tightFalloff * tightFalloff));
				}
			}

			float bvhAo = Math.Clamp(1.0f - occSum * invRayCount, MinSelfAo, 1.0f);
			float ao = CombineMacroAndCreviceAo(bvhAo, creviceAo[v]);

			if (groundValid)
			{
				float dy = pos.Y - minY;
				if (dy >= -0.02f && dy < 0.6f)
				{
					float hFactor = 1.0f - Math.Clamp(dy / 0.6f, 0.0f, 1.0f);
					// A downward normal (n.Y = -1) sees the ground plane fully; an upward normal (n.Y = +1) sees none.
					float nGroundVis = Math.Clamp((1.0f - n.Y) * 0.5f, 0.0f, 1.0f);
					float groundOcc = 0.35f * hFactor * hFactor * nGroundVis;
					ao *= 1.0f - groundOcc;
				}
			}

			result[v] = Math.Clamp(ao, MinSelfAo, 1.0f);
		}

		if (sourceVertexTotal >= 384)
		{
			Parallel.For(0, sourceVertexTotal, EvaluateVertex);
		}
		else
		{
			for (int v = 0; v < sourceVertexTotal; v++)
			{
				EvaluateVertex(v);
			}
		}

		return result;
	}

	/// <summary>
	/// Fast, non-blocking runtime lookup for canonical per-source-vertex self-AO.
	/// Reads from memory cache, shipped PRT L0 visibility (augmented with fast 1-ring crevice AO when
	/// <paramref name="geometries"/> is provided), or the pre-baked <c>.selfao</c> disk cache,
	/// and never invokes <see cref="ComputeBvhSelfAo"/> on runtime/loading threads.
	/// </summary>
	public static bool TryGetModelSelfAoFast(
		byte[] data,
		MdlInfo info,
		int sourceVertexTotal,
		string virtualPath,
		out float[] selfAo) =>
		TryGetModelSelfAoFast(data, info, geometries: null, sourceVertexTotal, virtualPath, out selfAo);

	public static bool TryGetModelSelfAoFast(
		byte[] data,
		MdlInfo info,
		IReadOnlyList<MdlGeometry>? geometries,
		int sourceVertexTotal,
		string virtualPath,
		out float[] selfAo)
	{
		selfAo = Array.Empty<float>();
		if (sourceVertexTotal <= 0)
		{
			return false;
		}

		string cacheKey = !string.IsNullOrEmpty(virtualPath)
			? virtualPath.Replace('\\', '/').ToLowerInvariant()
			: string.Empty;

		if (cacheKey.Length != 0 &&
		    MemoryCache.TryGetValue(cacheKey, out float[]? cached) &&
		    cached.Length == sourceVertexTotal)
		{
			selfAo = cached;
			return true;
		}

		string? diskPath = cacheKey.Length != 0 ? GetDiskCachePath(cacheKey) : null;
		if (diskPath is not null && TryReadDiskCache(diskPath, sourceVertexTotal, out float[] diskAo))
		{
			if (MemoryCache.Count < 512)
			{
				MemoryCache[cacheKey] = diskAo;
			}

			selfAo = diskAo;
			return true;
		}

		if (TryExtractFromPrt(data, info, sourceVertexTotal, out float[] prtAo))
		{
			if (geometries is { Count: > 0 })
			{
				bool includeGround = info.SkinnedCount == 0 && info.MovementMode != 1;
				float[] creviceAo = ComputeFastCreviceAndBaseAo(geometries, sourceVertexTotal, includeGround);
				if (creviceAo.Length == sourceVertexTotal)
				{
					for (int v = 0; v < sourceVertexTotal; v++)
					{
						prtAo[v] = CombineMacroAndCreviceAo(prtAo[v], creviceAo[v]);
					}
				}

				if (diskPath is not null)
				{
					TryWriteDiskCache(diskPath, prtAo);
				}
			}

			if (cacheKey.Length != 0 && MemoryCache.Count < 512)
			{
				MemoryCache[cacheKey] = prtAo;
			}

			selfAo = prtAo;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Queues an asynchronous background BVH <c>.selfao</c> bake for large models (&gt;4,096 vertices)
	/// so subsequent loads hit the disk/memory cache without ever stalling model loading.
	/// </summary>
	public static void QueueBackgroundSelfAoBake(
		IReadOnlyList<MdlGeometry> geometries,
		int sourceVertexTotal,
		bool includeGroundPlane,
		string virtualPath)
	{
		if (sourceVertexTotal <= 0 || geometries.Count == 0 || string.IsNullOrEmpty(virtualPath) ||
		    Refii.Core.Engine_AppRoot.IsTestHost)
		{
			return;
		}

		string cacheKey = virtualPath.Replace('\\', '/').ToLowerInvariant();
		if (!InFlightBackgroundBakes.TryAdd(cacheKey, 1))
		{
			return;
		}

		MdlGeometry[] snapshot = geometries is MdlGeometry[] arr ? arr : new List<MdlGeometry>(geometries).ToArray();
		_ = Task.Run(() =>
		{
			try
			{
				string diskPath = GetDiskCachePath(cacheKey);
				if (TryReadDiskCache(diskPath, sourceVertexTotal, out float[] existing))
				{
					if (MemoryCache.Count < 512)
					{
						MemoryCache[cacheKey] = existing;
					}
					return;
				}

				float[] computed = ComputeBvhSelfAo(snapshot, sourceVertexTotal, includeGroundPlane);
				if (MemoryCache.Count < 512)
				{
					MemoryCache[cacheKey] = computed;
				}
				TryWriteDiskCache(diskPath, computed);
			}
			catch
			{
			}
			finally
			{
				InFlightBackgroundBakes.TryRemove(cacheKey, out _);
			}
		});
	}

	/// <summary>
	/// Returns the canonical per-source-vertex self-AO array for <paramref name="virtualPath"/>,
	/// extracting PRT L0 visibility when available, or checking memory/disk caches before computing
	/// BVH hemisphere self-AO during offline level baking.
	/// </summary>
	public static float[] GetOrComputeModelSelfAo(
		byte[] data,
		MdlInfo info,
		IReadOnlyList<MdlGeometry> geometries,
		int sourceVertexTotal,
		string virtualPath)
	{
		if (sourceVertexTotal <= 0)
		{
			return Array.Empty<float>();
		}

		if (TryGetModelSelfAoFast(data, info, geometries, sourceVertexTotal, virtualPath, out float[] fastAo))
		{
			return fastAo;
		}

		string cacheKey = !string.IsNullOrEmpty(virtualPath)
			? virtualPath.Replace('\\', '/').ToLowerInvariant()
			: string.Empty;
		string? diskPath = cacheKey.Length != 0 ? GetDiskCachePath(cacheKey) : null;

		bool includeGround = info.SkinnedCount == 0 && info.MovementMode != 1;
		float[] computed = ComputeBvhSelfAo(geometries, sourceVertexTotal, includeGroundPlane: includeGround);
		if (cacheKey.Length != 0)
		{
			if (MemoryCache.Count < 512)
			{
				MemoryCache[cacheKey] = computed;
			}

			if (diskPath is not null)
			{
				TryWriteDiskCache(diskPath, computed);
			}
		}

		return computed;
	}

	/// <summary>
	/// Builds the <c>Mesh.ArrayType.Color</c> stream for <paramref name="geo"/> in Modern mode:
	/// <c>Color(1, 1, 1, selfAo)</c>. Coarse LOD vertices without a direct source vertex
	/// index (<c>-1</c>) inherit the nearest sourced vertex's self-AO.
	/// </summary>
	public static Color[] BuildColorStream(float[] modelSelfAo, MdlGeometry geo)
	{
		int vertexCount = geo.VertexCount;
		var colors = new Color[vertexCount];
		int[] srcMap = geo.SourceVertices;
		float[] pos = geo.Positions;

		bool canFallbackSearch = false;
		if (srcMap.Length > 0 && modelSelfAo.Length > 0)
		{
			int checkLimit = Math.Min(vertexCount, srcMap.Length);
			for (int i = 0; i < checkLimit; i++)
			{
				if (srcMap[i] >= 0)
				{
					canFallbackSearch = true;
					break;
				}
			}
		}

		for (int v = 0; v < vertexCount; v++)
		{
			int srcIdx = v < srcMap.Length ? srcMap[v] : -1;
			if (srcIdx < 0 && canFallbackSearch)
			{
				srcIdx = FindNearestSourcedVertexIndex(v, vertexCount, srcMap, pos);
			}

			float ao = (uint)srcIdx < (uint)modelSelfAo.Length ? modelSelfAo[srcIdx] : 1.0f;
			colors[v] = new Color(1.0f, 1.0f, 1.0f, ao);
		}

		return colors;
	}

	private static int FindNearestSourcedVertexIndex(
		int targetVertex,
		int vertexCount,
		int[] srcMap,
		float[] pos)
	{
		int targetPosAt = targetVertex * 3;
		if (targetPosAt + 2 >= pos.Length || srcMap.Length == 0)
		{
			return -1;
		}

		Vector3 targetPos = new(pos[targetPosAt], pos[targetPosAt + 1], pos[targetPosAt + 2]);
		float bestDistSq = float.PositiveInfinity;
		int bestSrcIdx = -1;
		int limit = Math.Min(vertexCount, srcMap.Length);
		int stride = limit > 512 ? Math.Max(1, limit / 512) : 1;

		for (int i = 0; i < limit; i += stride)
		{
			int candidateSrc = srcMap[i];
			if (candidateSrc < 0)
			{
				continue;
			}

			int pAt = i * 3;
			if (pAt + 2 >= pos.Length)
			{
				continue;
			}

			Vector3 p = new(pos[pAt], pos[pAt + 1], pos[pAt + 2]);
			float dSq = p.DistanceSquaredTo(targetPos);
			if (dSq < bestDistSq)
			{
				bestDistSq = dSq;
				bestSrcIdx = candidateSrc;
				if (dSq <= 1e-8f)
				{
					break;
				}
			}
		}

		return bestSrcIdx;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal static void BuildOrthonormalBasis(Vector3 n, out Vector3 tangent, out Vector3 bitangent)
	{
		if (n.Z < -0.999999f)
		{
			tangent = new Vector3(0.0f, -1.0f, 0.0f);
			bitangent = new Vector3(-1.0f, 0.0f, 0.0f);
			return;
		}

		float a = 1.0f / (1.0f + n.Z);
		float b = -n.X * n.Y * a;
		tangent = new Vector3(1.0f - n.X * n.X * a, b, -n.X);
		bitangent = new Vector3(b, 1.0f - n.Y * n.Y * a, -n.Y);
	}

	public static string GetDiskCachePath(string normalizedModelPath)
	{
		byte[] hash = Refii.Core.Engine_Hash.Sha256(Encoding.UTF8.GetBytes(normalizedModelPath));
		string hex16 = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
		return Path.Combine(CacheModelsAoDirectory, $"{hex16}.selfao");
	}

	public static byte[] Serialize(ReadOnlySpan<float> selfAo)
	{
		var bytes = new byte[12 + selfAo.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), Magic);
		BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), Version);
		BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), selfAo.Length);
		for (int i = 0; i < selfAo.Length; i++)
		{
			float clamped = Math.Clamp(selfAo[i], 0.0f, 1.0f);
			bytes[12 + i] = (byte)Math.Clamp((int)MathF.Round(clamped * 255.0f), 0, 255);
		}

		return bytes;
	}

	public static bool TryDeserialize(ReadOnlySpan<byte> bytes, int expectedVertexCount, out float[] selfAo)
	{
		selfAo = Array.Empty<float>();
		if (bytes.Length < 12)
		{
			return false;
		}

		uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0, 4));
		uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
		int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(8, 4));
		if (magic != Magic || version != Version || count <= 0 ||
		    (expectedVertexCount > 0 && count != expectedVertexCount) ||
		    bytes.Length < 12 + count)
		{
			return false;
		}

		selfAo = new float[count];
		const float inv255 = 1.0f / 255.0f;
		for (int i = 0; i < count; i++)
		{
			selfAo[i] = MathF.Max(bytes[12 + i] * inv255, MinSelfAo);
		}

		return true;
	}

	private static bool TryReadDiskCache(string path, int expectedVertexCount, out float[] selfAo)
	{
		selfAo = Array.Empty<float>();
		try
		{
			if (!File.Exists(path))
			{
				return false;
			}

			byte[] bytes = File.ReadAllBytes(path);
			return TryDeserialize(bytes, expectedVertexCount, out selfAo);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static void TryWriteDiskCache(string path, ReadOnlySpan<float> selfAo)
	{
		try
		{
			string? dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}

			File.WriteAllBytes(path, Serialize(selfAo));
		}
		catch (Exception)
		{
		}
	}
}

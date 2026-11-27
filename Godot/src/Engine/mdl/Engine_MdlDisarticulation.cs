using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Models;

/// <summary>
/// Analyses a rigged model's skinned surfaces once per <see cref="Mesh"/> to determine whether it is
/// a disarticulated rigid-piece death/crumble mesh (such as Fable 2's <c>Skeleton_Base.mdl</c>,
/// whose 65-bone humanoid hierarchy has 100% single-bone rigid vertex weights and zero cross-bone
/// triangles) and extracts exact bone-local center-of-mass and capsule geometry for each physical
/// bone piece.
/// </summary>
public static class Engine_MdlDisarticulation
{
	/// <summary>Pure-C# view of one skinned mesh surface, usable both at runtime and in headless unit tests.</summary>
	public readonly record struct SurfaceSkinData(
		Vector3[] Vertices,
		int[] Bones,
		float[] Weights,
		int[]? Indices);

	/// <summary>Precomputed rigid-body geometry and simulation role for one model bone.</summary>
	public readonly record struct BonePieceDescriptor(
		bool IsSimulated,
		bool IsRootWrapper,
		int VertexCount,
		Vector3 LocalCom,
		Vector3 LocalAxis,
		float HalfLength,
		float Radius,
		float Mass);

	/// <summary>Cached rigid-piece classification and per-bone descriptors for a <see cref="Mesh"/>.</summary>
	public sealed class DisarticulatedMeshProfile
	{
		public static readonly DisarticulatedMeshProfile NotDisarticulated =
			new(false, Array.Empty<BonePieceDescriptor>(), 0);

		public DisarticulatedMeshProfile(
			bool isRigidPieceMesh,
			BonePieceDescriptor[] pieces,
			int simulatedPieceCount)
		{
			IsRigidPieceMesh = isRigidPieceMesh;
			Pieces = pieces;
			SimulatedPieceCount = simulatedPieceCount;
		}

		/// <summary>True when the mesh's skinning or hierarchy represents separable rigid bone pieces.</summary>
		public bool IsRigidPieceMesh { get; }

		/// <summary>Per-bone descriptors in model bone order (empty when <see cref="IsRigidPieceMesh"/> is false).</summary>
		public BonePieceDescriptor[] Pieces { get; }

		/// <summary>Number of bones marked <see cref="BonePieceDescriptor.IsSimulated"/>.</summary>
		public int SimulatedPieceCount { get; }
	}

	private static readonly ConditionalWeakTable<Mesh, DisarticulatedMeshProfile> Cache = new();

	/// <summary>
	/// <c>ConvertZUpToYUp</c> basis matching <see cref="Refii.Entities.Engine_EntitySkeleton"/>
	/// (<c>(x, y, z) -&gt; (x, z, -y)</c>) so model rest transforms share the mesh vertex space.
	/// </summary>
	private static readonly Basis MeshMap = new(
		new Vector3(1f, 0f, 0f),
		new Vector3(0f, 0f, -1f),
		new Vector3(0f, 1f, 0f));

	private static readonly Basis MeshMapInverse = new(
		new Vector3(1f, 0f, 0f),
		new Vector3(0f, 0f, 1f),
		new Vector3(0f, -1f, 0f));

	/// <summary>
	/// Returns the cached disarticulation profile for <paramref name="mesh"/>, analysing its recorded
	/// CPU surface arrays at most once per mesh instance.
	/// </summary>
	public static DisarticulatedMeshProfile GetProfile(Mesh? mesh)
	{
		if (mesh is null)
		{
			return DisarticulatedMeshProfile.NotDisarticulated;
		}

		if (Cache.TryGetValue(mesh, out DisarticulatedMeshProfile? cached))
		{
			return cached;
		}

		DisarticulatedMeshProfile built = Build(mesh);
		Cache.AddOrUpdate(mesh, built);
		return built;
	}

	/// <summary>
	/// Classifies a skeleton + surface set and extracts bone-local COM, principal shaft axis,
	/// capsule half-length, radius, and mass for every physical piece.
	/// </summary>
	public static DisarticulatedMeshProfile Analyze(
		string[] boneNames,
		int[] parents,
		ReadOnlySpan<Transform3D> restGlobals,
		IReadOnlyList<SurfaceSkinData> surfaces)
	{
		int boneCount = boneNames.Length;
		if (boneCount < 4 || parents.Length != boneCount)
		{
			return DisarticulatedMeshProfile.NotDisarticulated;
		}

		int independentPieces = 0;
		bool rootIsZero = parents[0] < 0;
		int directChildrenOfZero = 0;
		for (int i = 0; i < boneCount; ++i)
		{
			int p = parents[i];
			if (i > 0 && p == 0)
			{
				++directChildrenOfZero;
			}

			if (p < 0 || (rootIsZero && p == 0))
			{
				++independentPieces;
			}
		}

		bool flatParentRig = independentPieces >= 6;

		// Pass 1: inspect vertex weights and triangle bone ownership across all surfaces.
		int totalWeightedVerts = 0;
		int multiBoneVerts = 0;
		int crossBoneTris = 0;
		var rawBoneVertCounts = new int[boneCount];
		int surfaceCount = surfaces.Count;
		int[][]? surfaceDomBones = surfaceCount > 0 ? new int[surfaceCount][] : null;

		for (int s = 0; s < surfaceCount; ++s)
		{
			SurfaceSkinData surf = surfaces[s];
			Vector3[] verts = surf.Vertices;
			int[] bones = surf.Bones;
			float[] weights = surf.Weights;
			if (verts is null || verts.Length == 0 ||
				bones is null || weights is null ||
				bones.Length < verts.Length || weights.Length < verts.Length)
			{
				continue;
			}

			int stride = Math.Min(bones.Length, weights.Length) / verts.Length;
			if (stride < 1)
			{
				continue;
			}

			var dom = new int[verts.Length];
			surfaceDomBones![s] = dom;

			for (int v = 0; v < verts.Length; ++v)
			{
				int baseIdx = v * stride;
				int primaryBone = -1;
				float primaryWeight = 0f;
				bool hasSecondBone = false;

				for (int k = 0; k < stride; ++k)
				{
					float w = weights[baseIdx + k];
					if (!float.IsFinite(w) || w <= 0.001f)
					{
						continue;
					}

					int b = bones[baseIdx + k];
					if (b < 0 || b >= boneCount)
					{
						continue;
					}

					if (primaryBone < 0)
					{
						primaryBone = b;
						primaryWeight = w;
					}
					else if (b == primaryBone)
					{
						primaryWeight += w;
					}
					else
					{
						hasSecondBone = true;
						if (w > primaryWeight)
						{
							primaryBone = b;
							primaryWeight = w;
						}
					}
				}

				dom[v] = primaryBone;
				if (primaryBone >= 0)
				{
					++totalWeightedVerts;
					++rawBoneVertCounts[primaryBone];
					if (hasSecondBone)
					{
						++multiBoneVerts;
					}
				}
			}

			if (surf.Indices is { Length: >= 3 } indices && multiBoneVerts == 0)
			{
				for (int t = 0; t + 2 < indices.Length; t += 3)
				{
					int i0 = indices[t];
					int i1 = indices[t + 1];
					int i2 = indices[t + 2];
					if ((uint)i0 >= (uint)dom.Length ||
						(uint)i1 >= (uint)dom.Length ||
						(uint)i2 >= (uint)dom.Length)
					{
						continue;
					}

					int b0 = dom[i0];
					int b1 = dom[i1];
					int b2 = dom[i2];
					if (b0 >= 0 && b1 >= 0 && b2 >= 0 && (b0 != b1 || b0 != b2))
					{
						++crossBoneTris;
						break;
					}
				}
			}
		}

		int populatedBones = 0;
		for (int b = 0; b < boneCount; ++b)
		{
			if (rawBoneVertCounts[b] > 0)
			{
				++populatedBones;
			}
		}

		bool rigidSkinGeometry = totalWeightedVerts > 0 &&
			multiBoneVerts == 0 &&
			crossBoneTris == 0 &&
			populatedBones >= 8;

		if (!flatParentRig && !rigidSkinGeometry)
		{
			return DisarticulatedMeshProfile.NotDisarticulated;
		}

		// Classify root wrappers and fold digit phalanges into their parent hand/foot rigid pieces.
		var isRootWrapper = new bool[boneCount];
		var ownerBone = new int[boneCount];
		for (int m = 0; m < boneCount; ++m)
		{
			isRootWrapper[m] = parents[m] < 0 && m == 0 &&
				(directChildrenOfZero >= 6 ||
				 boneNames[m].Contains("Locator", StringComparison.OrdinalIgnoreCase) ||
				 boneNames[m].Contains("Rig", StringComparison.OrdinalIgnoreCase) ||
				 boneNames[m].Contains("Root", StringComparison.OrdinalIgnoreCase));
			ownerBone[m] = m;
		}

		for (int m = 0; m < boneCount; ++m)
		{
			if (isRootWrapper[m] || !IsDigitBone(boneNames[m]))
			{
				continue;
			}

			int ancestor = FindHandOrFootAncestor(m, boneNames, parents, isRootWrapper);
			if (ancestor >= 0)
			{
				ownerBone[m] = ancestor;
			}
		}

		// Precompute inverse rest globals for transforming mesh-space vertices into owner-bone local space.
		var invRest = new Transform3D[boneCount];
		for (int m = 0; m < boneCount; ++m)
		{
			Transform3D rest = m < restGlobals.Length ? restGlobals[m] : Transform3D.Identity;
			invRest[m] = rest.Basis.Determinant() is > 1e-6f or < -1e-6f
				? rest.AffineInverse()
				: Transform3D.Identity;
		}

		var vertCounts = new int[boneCount];
		var sumLocal = new Vector3[boneCount];
		var minLocal = new Vector3[boneCount];
		var maxLocal = new Vector3[boneCount];
		for (int m = 0; m < boneCount; ++m)
		{
			minLocal[m] = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
			maxLocal[m] = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
		}

		// Pass 2: accumulate owner-local vertex means and AABBs.
		if (surfaceDomBones is not null)
		{
			for (int s = 0; s < surfaceCount; ++s)
			{
				int[]? dom = surfaceDomBones[s];
				if (dom is null)
				{
					continue;
				}

				Vector3[] verts = surfaces[s].Vertices;
				for (int v = 0; v < dom.Length; ++v)
				{
					int b = dom[v];
					if (b < 0 || isRootWrapper[b])
					{
						continue;
					}

					int owner = ownerBone[b];
					Vector3 local = invRest[owner] * verts[v];
					if (!local.IsFinite())
					{
						continue;
					}

					++vertCounts[owner];
					sumLocal[owner] += local;
					if (local.X < minLocal[owner].X) minLocal[owner].X = local.X;
					if (local.Y < minLocal[owner].Y) minLocal[owner].Y = local.Y;
					if (local.Z < minLocal[owner].Z) minLocal[owner].Z = local.Z;
					if (local.X > maxLocal[owner].X) maxLocal[owner].X = local.X;
					if (local.Y > maxLocal[owner].Y) maxLocal[owner].Y = local.Y;
					if (local.Z > maxLocal[owner].Z) maxLocal[owner].Z = local.Z;
				}
			}
		}

		var meanLocal = new Vector3[boneCount];
		for (int m = 0; m < boneCount; ++m)
		{
			if (vertCounts[m] > 0)
			{
				meanLocal[m] = sumLocal[m] / vertCounts[m];
			}
		}

		// Pass 3: accumulate 3x3 symmetric covariance matrices to recover each bone's principal shaft axis.
		var cxx = new float[boneCount];
		var cxy = new float[boneCount];
		var cxz = new float[boneCount];
		var cyy = new float[boneCount];
		var cyz = new float[boneCount];
		var czz = new float[boneCount];

		if (surfaceDomBones is not null)
		{
			for (int s = 0; s < surfaceCount; ++s)
			{
				int[]? dom = surfaceDomBones[s];
				if (dom is null)
				{
					continue;
				}

				Vector3[] verts = surfaces[s].Vertices;
				for (int v = 0; v < dom.Length; ++v)
				{
					int b = dom[v];
					if (b < 0 || isRootWrapper[b])
					{
						continue;
					}

					int owner = ownerBone[b];
					if (vertCounts[owner] <= 1)
					{
						continue;
					}

					Vector3 d = (invRest[owner] * verts[v]) - meanLocal[owner];
					if (!d.IsFinite())
					{
						continue;
					}

					cxx[owner] += d.X * d.X;
					cxy[owner] += d.X * d.Y;
					cxz[owner] += d.X * d.Z;
					cyy[owner] += d.Y * d.Y;
					cyz[owner] += d.Y * d.Z;
					czz[owner] += d.Z * d.Z;
				}
			}
		}

		var axes = new Vector3[boneCount];
		for (int m = 0; m < boneCount; ++m)
		{
			if (vertCounts[m] > 1)
			{
				Vector3 extent = maxLocal[m] - minLocal[m];
				axes[m] = PrincipalAxis(cxx[m], cxy[m], cxz[m], cyy[m], cyz[m], czz[m], extent);
			}
			else
			{
				axes[m] = Vector3.Up;
			}
		}

		// Pass 4: project vertices onto each bone's principal shaft axis to size exact capsules.
		var projMin = new float[boneCount];
		var projMax = new float[boneCount];
		var maxPerpSq = new float[boneCount];
		var sumPerpSq = new float[boneCount];
		for (int m = 0; m < boneCount; ++m)
		{
			projMin[m] = float.PositiveInfinity;
			projMax[m] = float.NegativeInfinity;
		}

		if (surfaceDomBones is not null)
		{
			for (int s = 0; s < surfaceCount; ++s)
			{
				int[]? dom = surfaceDomBones[s];
				if (dom is null)
				{
					continue;
				}

				Vector3[] verts = surfaces[s].Vertices;
				for (int v = 0; v < dom.Length; ++v)
				{
					int b = dom[v];
					if (b < 0 || isRootWrapper[b])
					{
						continue;
					}

					int owner = ownerBone[b];
					if (vertCounts[owner] == 0)
					{
						continue;
					}

					Vector3 d = (invRest[owner] * verts[v]) - meanLocal[owner];
					if (!d.IsFinite())
					{
						continue;
					}

					float p = d.Dot(axes[owner]);
					if (p < projMin[owner]) projMin[owner] = p;
					if (p > projMax[owner]) projMax[owner] = p;

					float perpSq = MathF.Max(0f, d.LengthSquared() - (p * p));
					if (perpSq > maxPerpSq[owner]) maxPerpSq[owner] = perpSq;
					sumPerpSq[owner] += perpSq;
				}
			}
		}

		var pieces = new BonePieceDescriptor[boneCount];
		int simulatedCount = 0;
		bool syntheticNoGeometry = totalWeightedVerts == 0 && flatParentRig;

		for (int m = 0; m < boneCount; ++m)
		{
			if (isRootWrapper[m])
			{
				pieces[m] = new BonePieceDescriptor(
					IsSimulated: false,
					IsRootWrapper: true,
					VertexCount: 0,
					LocalCom: Vector3.Zero,
					LocalAxis: Vector3.Up,
					HalfLength: 0f,
					Radius: 0.05f,
					Mass: 1f);
				continue;
			}

			if (ownerBone[m] != m)
			{
				pieces[m] = new BonePieceDescriptor(
					IsSimulated: false,
					IsRootWrapper: false,
					VertexCount: 0,
					LocalCom: Vector3.Zero,
					LocalAxis: Vector3.Up,
					HalfLength: 0f,
					Radius: 0.02f,
					Mass: 0.1f);
				continue;
			}

			if (vertCounts[m] > 0)
			{
				float maxPerp = MathF.Sqrt(maxPerpSq[m]);
				float rmsPerp = MathF.Sqrt(sumPerpSq[m] / vertCounts[m]);
				float radius = Math.Clamp((0.55f * maxPerp) + (0.45f * rmsPerp * 1.35f), 0.018f, 0.22f);
				float span = float.IsFinite(projMin[m]) && float.IsFinite(projMax[m])
					? MathF.Max(0f, projMax[m] - projMin[m])
					: 0f;
				float halfLength = Math.Clamp((0.5f * span) - radius, 0f, 0.45f);
				float midShift = float.IsFinite(projMin[m]) && float.IsFinite(projMax[m])
					? 0.5f * (projMin[m] + projMax[m])
					: 0f;
				Vector3 com = meanLocal[m] + (axes[m] * midShift);
				float mass = EstimateMass(radius, halfLength, boneNames[m]);

				pieces[m] = new BonePieceDescriptor(
					IsSimulated: true,
					IsRootWrapper: false,
					VertexCount: vertCounts[m],
					LocalCom: com,
					LocalAxis: axes[m],
					HalfLength: halfLength,
					Radius: radius,
					Mass: mass);
				++simulatedCount;
				continue;
			}

			if (syntheticNoGeometry)
			{
				float fallbackRadius = EstimateFallbackRadius(boneNames[m]);
				pieces[m] = new BonePieceDescriptor(
					IsSimulated: true,
					IsRootWrapper: false,
					VertexCount: 0,
					LocalCom: Vector3.Zero,
					LocalAxis: Vector3.Up,
					HalfLength: 0f,
					Radius: fallbackRadius,
					Mass: EstimateMass(fallbackRadius, 0f, boneNames[m]));
				++simulatedCount;
				continue;
			}

			// Empty/dummy bone inside a real geometry-bearing rig (e.g. Shadow_Chest, *_DummyObject).
			pieces[m] = new BonePieceDescriptor(
				IsSimulated: false,
				IsRootWrapper: false,
				VertexCount: 0,
				LocalCom: Vector3.Zero,
				LocalAxis: Vector3.Up,
				HalfLength: 0f,
				Radius: 0.03f,
				Mass: 0.1f);
		}

		return new DisarticulatedMeshProfile(true, pieces, simulatedCount);
	}

	private static DisarticulatedMeshProfile Build(Mesh mesh)
	{
		if (mesh is not ArrayMesh array || Engine_MdlRig.TryFrom(mesh) is not { } rig || rig.BoneCount < 4)
		{
			return DisarticulatedMeshProfile.NotDisarticulated;
		}

		Transform3D[] restGlobals = ComputeRestGlobals(rig);
		int surfaceCount = array.GetSurfaceCount();
		var surfaces = new List<SurfaceSkinData>(surfaceCount);

		for (int s = 0; s < surfaceCount; ++s)
		{
			Godot.Collections.Array arrays = Engine_MdlSurfaceArrays.Of(array, s);
			if (arrays.Count <= (int)Mesh.ArrayType.Weights ||
				arrays[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.Nil ||
				arrays[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil ||
				arrays[(int)Mesh.ArrayType.Weights].VariantType == Variant.Type.Nil)
			{
				continue;
			}

			Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
			int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
			float[] weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
			int[]? indices = arrays.Count > (int)Mesh.ArrayType.Index &&
				arrays[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.Nil
				? arrays[(int)Mesh.ArrayType.Index].AsInt32Array()
				: null;

			surfaces.Add(new SurfaceSkinData(vertices, bones, weights, indices));
		}

		return Analyze(rig.Names, rig.Parents, restGlobals, surfaces);
	}

	private static Transform3D[] ComputeRestGlobals(Engine_MdlRig rig)
	{
		var global = new Transform3D[rig.BoneCount];
		for (int m = 0; m < rig.BoneCount; ++m)
		{
			Transform3D local = ToMeshSpace(rig.Rotation(m), rig.Translation(m), rig.Scale(m));
			int parent = rig.Parents[m];
			Transform3D parentGlobal = parent >= 0 && parent < m
				? global[parent]
				: Transform3D.Identity;
			global[m] = parentGlobal * local;
		}

		return global;
	}

	private static Transform3D ToMeshSpace(Quaternion rotation, Vector3 guestTranslation, Vector3 guestScale)
	{
		float lenSq = rotation.LengthSquared();
		Basis basis = float.IsFinite(lenSq) && lenSq > 1e-8f
			? new Basis(rotation.Normalized())
			: Basis.Identity;

		basis = MeshMap * basis * MeshMapInverse;
		var scale = new Vector3(guestScale.X, guestScale.Z, guestScale.Y);
		var translation = new Vector3(guestTranslation.X, guestTranslation.Z, -guestTranslation.Y);
		if (!scale.IsFinite() || !translation.IsFinite())
		{
			return Transform3D.Identity;
		}

		return new Transform3D(basis.ScaledLocal(scale), translation);
	}

	private static Vector3 PrincipalAxis(
		float cxx, float cxy, float cxz, float cyy, float cyz, float czz, Vector3 fallbackExtent)
	{
		// Seed power iteration along the largest AABB dimension so axis-aligned bones converge instantly.
		Vector3 v = MathF.Abs(fallbackExtent.X) >= MathF.Abs(fallbackExtent.Y) &&
			MathF.Abs(fallbackExtent.X) >= MathF.Abs(fallbackExtent.Z)
			? new Vector3(1f, 0.1f, 0.05f)
			: MathF.Abs(fallbackExtent.Y) >= MathF.Abs(fallbackExtent.Z)
				? new Vector3(0.05f, 1f, 0.1f)
				: new Vector3(0.1f, 0.05f, 1f);
		v = v.Normalized();

		for (int iter = 0; iter < 16; ++iter)
		{
			var next = new Vector3(
				(cxx * v.X) + (cxy * v.Y) + (cxz * v.Z),
				(cxy * v.X) + (cyy * v.Y) + (cyz * v.Z),
				(cxz * v.X) + (cyz * v.Y) + (czz * v.Z));
			float lenSq = next.LengthSquared();
			if (!float.IsFinite(lenSq) || lenSq <= 1e-12f)
			{
				break;
			}

			v = next / MathF.Sqrt(lenSq);
		}

		return v.IsFinite() && v.LengthSquared() > 1e-6f ? v.Normalized() : Vector3.Up;
	}

	private static bool IsDigitBone(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return false;
		}

		return name.Contains("Thumb", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Index", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Middle", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Ring", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Pinky", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Finger", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Toe", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Tip", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsHandOrFootAnchor(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return false;
		}

		return name.Contains("Hand", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Wrist", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Heel", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Foot", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Ankle", StringComparison.OrdinalIgnoreCase);
	}

	private static int FindHandOrFootAncestor(
		int boneIndex, string[] boneNames, int[] parents, bool[] isRootWrapper)
	{
		int cursor = parents[boneIndex];
		int guard = 0;
		while (cursor >= 0 && cursor < boneNames.Length && guard++ < 16)
		{
			if (!isRootWrapper[cursor] && !IsDigitBone(boneNames[cursor]) && IsHandOrFootAnchor(boneNames[cursor]))
			{
				return cursor;
			}

			int next = parents[cursor];
			if (next == cursor)
			{
				break;
			}

			cursor = next;
		}

		return -1;
	}

	private static float EstimateMass(float radius, float halfLength, string boneName)
	{
		// Approximate solid bone mass from capsule volume (in dm^3 scaled to dry bone density).
		float volume = MathF.PI * radius * radius * ((2f * halfLength) + ((4f / 3f) * radius));
		float mass = volume * 350f;
		if (!string.IsNullOrEmpty(boneName))
		{
			if (boneName.Contains("Spine", StringComparison.OrdinalIgnoreCase) ||
				boneName.Contains("Rib", StringComparison.OrdinalIgnoreCase) ||
				boneName.Contains("Chest", StringComparison.OrdinalIgnoreCase) ||
				boneName.Contains("Pelvis", StringComparison.OrdinalIgnoreCase) ||
				boneName.Contains("Hip", StringComparison.OrdinalIgnoreCase))
			{
				mass = MathF.Max(mass, 1.8f);
			}
			else if (boneName.Contains("Head", StringComparison.OrdinalIgnoreCase) ||
				boneName.Contains("Skull", StringComparison.OrdinalIgnoreCase))
			{
				mass = MathF.Max(mass, 1.2f);
			}
		}

		return Math.Clamp(mass, 0.2f, 5.0f);
	}

	private static float EstimateFallbackRadius(string boneName)
	{
		if (string.IsNullOrEmpty(boneName))
		{
			return 0.06f;
		}

		if (boneName.Contains("Head", StringComparison.OrdinalIgnoreCase) ||
			boneName.Contains("Skull", StringComparison.OrdinalIgnoreCase) ||
			boneName.Contains("Chest", StringComparison.OrdinalIgnoreCase) ||
			boneName.Contains("Rib", StringComparison.OrdinalIgnoreCase) ||
			boneName.Contains("Pelvis", StringComparison.OrdinalIgnoreCase) ||
			boneName.Contains("Hip", StringComparison.OrdinalIgnoreCase))
		{
			return 0.10f;
		}

		if (boneName.Contains("Spine", StringComparison.OrdinalIgnoreCase) ||
			boneName.Contains("Thigh", StringComparison.OrdinalIgnoreCase) ||
			boneName.Contains("UpperArm", StringComparison.OrdinalIgnoreCase) ||
			boneName.Contains("Clavicle", StringComparison.OrdinalIgnoreCase))
		{
			return 0.075f;
		}

		return 0.055f;
	}
}

using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>A mesh sampled out of Godot, in the arrays <see cref="Engine_MdlWriter"/> takes.</summary>
public sealed class MdlImportedMesh
{
	public float[] Positions = Array.Empty<float>();

	public float[] Uvs = Array.Empty<float>();

	public float[] Normals = Array.Empty<float>();

	public float[] Tangents = Array.Empty<float>();

	public ushort[] Indices = Array.Empty<ushort>();

	public int VertexCount => Positions.Length / 3;

	public int TriangleCount => Indices.Length / 3;
}

/// <summary>An individual surface sampled out of a glTF/Godot mesh, with its material index preserved.</summary>
public sealed class MdlImportedSurface
{
	public int MaterialIndex { get; set; }
	public string MaterialName { get; set; } = string.Empty;
	public float[] Positions = Array.Empty<float>();
	public float[] Uvs = Array.Empty<float>();
	public float[] Normals = Array.Empty<float>();
	public float[] Tangents = Array.Empty<float>();
	public ushort[] Indices = Array.Empty<ushort>();
	public int VertexCount => Positions.Length / 3;
	public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Turns a Godot mesh — which is what a glTF import produces — into the arrays a `.mdl` is
/// written from.
///
/// THE AXES ARE THE READER'S, RUN BACKWARDS. Loading a model applies
/// <c>(x, y, z) -> (x, z, -y)</c>, so writing applies <c>(x, y, z) -> (x, -z, y)</c>. That
/// matrix has determinant +1 — a proper rotation — so triangle winding survives it and nothing
/// needs reversing. Getting that wrong turns a model inside out, which is why it is derived from
/// the reader rather than guessed.
/// </summary>
public static class Engine_MdlImport
{
	/// <summary>
	/// Samples one surface.
	///
	/// Only what a static `.mdl` stores is taken: positions, one UV set, normals and tangents.
	/// Godot's own vertex colours and second UV set have nowhere to go in this layout.
	/// </summary>
	public static bool TryFromSurface(
		Mesh mesh, int surface, out MdlImportedMesh imported, out string error, Transform3D? sceneTransform = null)
	{
		imported = new MdlImportedMesh();
		error = string.Empty;

		if (mesh is null)
		{
			error = "there is no mesh to read";
			return false;
		}

		if (surface < 0 || surface >= mesh.GetSurfaceCount())
		{
			error = $"the mesh has {mesh.GetSurfaceCount()} surface(s); there is no surface {surface}";
			return false;
		}

		// Only an ArrayMesh states its primitive type; anything else is taken on trust and the
		// index arithmetic below catches a surface that is not triangles anyway.
		if (mesh is ArrayMesh array &&
			array.SurfaceGetPrimitiveType(surface) != Mesh.PrimitiveType.Triangles)
		{
			error =
				$"surface {surface} is {array.SurfaceGetPrimitiveType(surface)}; a model is " +
				"written from triangles.";
			return false;
		}

		Godot.Collections.Array arrays = Engine_MdlSurfaceArrays.Of(mesh, surface);
		var vertices = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
		if (vertices is null || vertices.Length == 0)
		{
			error = $"surface {surface} has no vertices";
			return false;
		}

		if (vertices.Length > ushort.MaxValue)
		{
			error =
				$"{vertices.Length} vertices; a model's indices are 16-bit, so a surface cannot " +
				"exceed 65,535. Split it, or decimate it.";
			return false;
		}

		var normals = arrays[(int)Mesh.ArrayType.Normal].As<Vector3[]>();
		var tangents = arrays[(int)Mesh.ArrayType.Tangent].As<float[]>();
		var uvs = arrays[(int)Mesh.ArrayType.TexUV].As<Vector2[]>();
		var indices = arrays[(int)Mesh.ArrayType.Index].As<int[]>();

		bool hasSceneXform = sceneTransform.HasValue && !sceneTransform.Value.IsEqualApprox(Transform3D.Identity);
		Transform3D xform = sceneTransform ?? Transform3D.Identity;
		Basis normalBasis = hasSceneXform ? xform.Basis.Orthonormalized() : Basis.Identity;

		imported.Positions = new float[vertices.Length * 3];
		for (int v = 0; v < vertices.Length; v++)
		{
			Vector3 pos = hasSceneXform ? (xform * vertices[v]) : vertices[v];
			Vector3 rotated = ToModelSpace(pos);
			imported.Positions[v * 3] = rotated.X;
			imported.Positions[(v * 3) + 1] = rotated.Y;
			imported.Positions[(v * 3) + 2] = rotated.Z;
		}

		if (normals is { Length: > 0 })
		{
			imported.Normals = new float[normals.Length * 3];
			for (int v = 0; v < normals.Length; v++)
			{
				Vector3 nrm = hasSceneXform ? (normalBasis * normals[v]).Normalized() : normals[v];
				Vector3 rotated = ToModelSpace(nrm);
				imported.Normals[v * 3] = rotated.X;
				imported.Normals[(v * 3) + 1] = rotated.Y;
				imported.Normals[(v * 3) + 2] = rotated.Z;
			}
		}

		if (tangents is { Length: > 0 })
		{
			imported.Tangents = new float[(tangents.Length / 4) * 4];
			for (int v = 0; v * 4 + 3 < tangents.Length; v++)
			{
				var tVec = new Vector3(tangents[v * 4], tangents[(v * 4) + 1], tangents[(v * 4) + 2]);
				if (hasSceneXform)
				{
					tVec = (normalBasis * tVec).Normalized();
				}
				Vector3 rotated = ToModelSpace(tVec);
				imported.Tangents[v * 4] = rotated.X;
				imported.Tangents[(v * 4) + 1] = rotated.Y;
				imported.Tangents[(v * 4) + 2] = rotated.Z;
				imported.Tangents[(v * 4) + 3] = tangents[(v * 4) + 3];
			}
		}

		if (uvs is { Length: > 0 })
		{
			imported.Uvs = new float[uvs.Length * 2];
			for (int v = 0; v < uvs.Length; v++)
			{
				imported.Uvs[v * 2] = uvs[v].X;
				imported.Uvs[(v * 2) + 1] = uvs[v].Y;
			}
		}

		// An unindexed surface is still triangles — one index per vertex, in order.
		if (indices is { Length: > 0 })
		{
			imported.Indices = new ushort[indices.Length];
			for (int i = 0; i < indices.Length; i++)
			{
				if (indices[i] < 0 || indices[i] >= vertices.Length)
				{
					error = $"an index reaches vertex {indices[i]}, past the {vertices.Length} in this surface";
					return false;
				}

				imported.Indices[i] = (ushort)indices[i];
			}
		}
		else
		{
			imported.Indices = new ushort[vertices.Length];
			for (int i = 0; i < vertices.Length; i++)
			{
				imported.Indices[i] = (ushort)i;
			}
		}

		if (imported.Indices.Length % 3 != 0)
		{
			error = $"{imported.Indices.Length} indices is not a whole number of triangles";
			return false;
		}

		return true;
	}

	/// <summary>The reader's Z-up to Y-up rotation, run backwards.</summary>
	private static Vector3 ToModelSpace(Vector3 value) => new(value.X, -value.Z, value.Y);

	/// <summary>The first mesh in a scene a glTF import produced.</summary>
	public static Mesh? FindFirstMesh(Node root)
	{
		if (root is MeshInstance3D instance && instance.Mesh is { } mesh && mesh.GetSurfaceCount() > 0)
		{
			return mesh;
		}

		if (root is ImporterMeshInstance3D impInstance && impInstance.Mesh is { } impMesh)
		{
			ArrayMesh array = impMesh.GetMesh();
			if (array is not null && array.GetSurfaceCount() > 0)
			{
				return array;
			}
		}

		foreach (Node child in root.GetChildren())
		{
			if (FindFirstMesh(child) is { } found)
			{
				return found;
			}
		}

		return null;
	}
}

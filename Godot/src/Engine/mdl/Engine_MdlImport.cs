using System;
using System.Collections.Generic;

namespace Refii.Models;

/// <summary>One surface of a glTF in the arrays a `.mdl` is written from, with what it is skinned to.</summary>
public sealed class MdlImportedSurface
{
	/// <summary>The glTF material index, or -1 when the primitive states none.</summary>
	public int MaterialIndex = -1;

	public string MaterialName = string.Empty;

	/// <summary>Three per vertex, model space.</summary>
	public float[] Positions = Array.Empty<float>();

	/// <summary>Two per vertex; empty when the glTF states none.</summary>
	public float[] Uvs = Array.Empty<float>();

	/// <summary>Three per vertex, model space; empty when the glTF states none.</summary>
	public float[] Normals = Array.Empty<float>();

	/// <summary>Four per vertex, model space; empty when the glTF states none.</summary>
	public float[] Tangents = Array.Empty<float>();

	/// <summary>Whole triangles.</summary>
	public ushort[] Indices = Array.Empty<ushort>();

	/// <summary>Four per vertex, slots into <see cref="JointNames"/>; empty for an unskinned surface.</summary>
	public int[] Joints = Array.Empty<int>();

	/// <summary>Four per vertex, paired with <see cref="Joints"/> by slot.</summary>
	public float[] Weights = Array.Empty<float>();

	/// <summary>The skin's joints by name; empty for an unskinned surface.</summary>
	public string[] JointNames = Array.Empty<string>();

	public bool IsSkinned => Joints.Length != 0 && JointNames.Length != 0;

	public int VertexCount => Positions.Length / 3;

	public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Turns what a glTF states into the arrays a `.mdl` is written from.
///
/// THE AXES ARE THE READER'S, RUN BACKWARDS. Loading a model applies
/// <c>(x, y, z) -> (x, z, -y)</c>, so writing applies <c>(x, y, z) -> (x, -z, y)</c>. That
/// matrix has determinant +1 — a proper rotation — so triangle winding survives it and nothing
/// needs reversing. Getting that wrong turns a model inside out, which is why it is derived from
/// the reader rather than guessed.
///
/// Only what a `.mdl` stores is taken: positions, one UV set, normals, tangents, and for a
/// skinned surface its four joints and weights per vertex. A glTF's vertex colours and second UV
/// set have nowhere to go in this layout.
/// </summary>
public static class Engine_MdlImport
{
	/// <summary>Every primitive of the glTF, in model space.</summary>
	public static bool TryFromGltf(byte[] gltf, out List<MdlImportedSurface> surfaces, out string error)
	{
		surfaces = new List<MdlImportedSurface>();
		if (!Engine_GltfReader.TryRead(gltf, out GltfModel model, out error))
		{
			return false;
		}

		foreach (GltfPrimitive primitive in model.Primitives)
		{
			if (!TryFromPrimitive(primitive, out MdlImportedSurface surface, out error))
			{
				return false;
			}

			surfaces.Add(surface);
		}

		return true;
	}

	public static bool TryFromPrimitive(GltfPrimitive primitive, out MdlImportedSurface surface, out string error)
	{
		surface = new MdlImportedSurface
		{
			MaterialIndex = primitive.Material,
			MaterialName = primitive.MaterialName,
		};
		error = string.Empty;

		int vertexCount = primitive.VertexCount;
		if (vertexCount == 0)
		{
			error = $"mesh {primitive.Mesh} primitive {primitive.Primitive} has no vertices";
			return false;
		}

		if (vertexCount > ushort.MaxValue)
		{
			error =
				$"{vertexCount} vertices; a model's indices are 16-bit, so a surface cannot " +
				"exceed 65,535. Split it, or decimate it.";
			return false;
		}

		surface.Positions = ToModelSpace(primitive.Positions, 3);
		surface.Normals = ToModelSpace(primitive.Normals, 3);
		surface.Tangents = ToModelSpace(primitive.Tangents, 4);
		surface.Uvs = (float[])primitive.Uvs.Clone();

		surface.Indices = new ushort[primitive.Indices.Length];
		for (int i = 0; i < primitive.Indices.Length; i++)
		{
			if (primitive.Indices[i] >= (uint)vertexCount)
			{
				error = $"an index reaches vertex {primitive.Indices[i]}, past the {vertexCount} in this surface";
				return false;
			}

			surface.Indices[i] = (ushort)primitive.Indices[i];
		}

		if (surface.Indices.Length % 3 != 0)
		{
			error = $"{surface.Indices.Length} indices is not a whole number of triangles";
			return false;
		}

		if (primitive.Joints.Length == vertexCount * 4 && primitive.Weights.Length == vertexCount * 4)
		{
			surface.Joints = (int[])primitive.Joints.Clone();
			surface.Weights = (float[])primitive.Weights.Clone();
			surface.JointNames = (string[])primitive.JointNames.Clone();
		}

		return true;
	}

	/// <summary>The reader's Z-up to Y-up rotation, run backwards, over the first three of every <paramref name="stride"/>.</summary>
	public static float[] ToModelSpace(float[] values, int stride)
	{
		var result = (float[])values.Clone();
		for (int i = 0; i + 2 < result.Length; i += stride)
		{
			float y = result[i + 1];
			float z = result[i + 2];
			result[i + 1] = -z;
			result[i + 2] = y;
		}

		return result;
	}
}

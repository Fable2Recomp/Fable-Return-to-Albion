using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Refii.Models;

/// <summary>One glTF primitive, flattened: its node's world transform applied, indices as a triangle list.</summary>
public sealed class GltfPrimitive
{
	public int Mesh;

	public int Primitive;

	public string MeshName = string.Empty;

	/// <summary>The glTF material index, or -1 when the primitive states none.</summary>
	public int Material = -1;

	public string MaterialName = string.Empty;

	/// <summary>Three per vertex, glTF space (Y up, metres).</summary>
	public float[] Positions = Array.Empty<float>();

	/// <summary>Three per vertex; empty when the primitive states none.</summary>
	public float[] Normals = Array.Empty<float>();

	/// <summary>Four per vertex (xyz + handedness); empty when the primitive states none.</summary>
	public float[] Tangents = Array.Empty<float>();

	/// <summary>Two per vertex (TEXCOORD_0); empty when the primitive states none.</summary>
	public float[] Uvs = Array.Empty<float>();

	/// <summary>Four per vertex: slots into <see cref="JointNames"/>. Empty for an unskinned primitive.</summary>
	public int[] Joints = Array.Empty<int>();

	/// <summary>Four per vertex, paired with <see cref="Joints"/> by slot.</summary>
	public float[] Weights = Array.Empty<float>();

	/// <summary>The skin's joints by name, in the skin's joint order; empty for an unskinned primitive.</summary>
	public string[] JointNames = Array.Empty<string>();

	/// <summary>Whole triangles, three indices each.</summary>
	public uint[] Indices = Array.Empty<uint>();

	public int VertexCount => Positions.Length / 3;

	public int TriangleCount => Indices.Length / 3;
}

/// <summary>What a glTF states that a model is written from.</summary>
public sealed class GltfModel
{
	public List<GltfPrimitive> Primitives = new();

	public string[] MaterialNames = Array.Empty<string>();
}

/// <summary>
/// Reads a glTF 2.0 file — binary (`.glb`) or JSON (`.gltf` with its buffers embedded as data
/// URIs) — into flat arrays. No scene objects, no engine: the JSON chunk is read with
/// System.Text.Json and the accessors are decoded straight out of the binary chunk, which is why
/// the whole model path can be tested without a renderer and why the same bytes convert the same
/// way in the mount, the Studio and a test.
///
/// What is taken: every primitive of every mesh, with the world transform of the node that
/// places it applied to positions, normals and tangents (a mesh no node places is taken as it
/// is). A skinned node's transform is NOT applied — glTF states that only the joints move a
/// skinned mesh — and its JOINTS_0/WEIGHTS_0 come through with the skin's joint names, which is
/// what a rigged model is matched to its template's skeleton by. Strips and fans become lists; a
/// transform that mirrors (negative determinant) reverses the winding so the faces still point
/// out. An external `.bin` cannot be read from bytes alone and is refused by name.
/// </summary>
public static class Engine_GltfReader
{
	private const uint GlbMagic = 0x46546C67u; // "glTF"

	private const uint JsonChunk = 0x4E4F534Au; // "JSON"

	private const uint BinChunk = 0x004E4942u; // "BIN\0"

	public static bool TryRead(byte[] bytes, out GltfModel model, out string error)
	{
		model = new GltfModel();
		error = string.Empty;

		if (bytes is null || bytes.Length == 0)
		{
			error = "the glTF is empty";
			return false;
		}

		if (!TrySplit(bytes, out string json, out byte[]? bin, out error))
		{
			return false;
		}

		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			return Read(doc.RootElement, bin, model, out error);
		}
		catch (JsonException exception)
		{
			error = $"the glTF's JSON could not be read: {exception.Message}";
			return false;
		}
	}

	/// <summary>Where the JSON and the binary payload are: GLB chunks, or a JSON text with data URIs.</summary>
	private static bool TrySplit(byte[] bytes, out string json, out byte[]? bin, out string error)
	{
		json = string.Empty;
		bin = null;
		error = string.Empty;

		if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) == GlbMagic)
		{
			uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
			if (version != 2)
			{
				error = $"GLB version {version}; only version 2 is read";
				return false;
			}

			uint stated = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
			int length = (int)Math.Min(stated, (uint)bytes.Length);
			int at = 12;
			while (at + 8 <= length)
			{
				uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
				uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4, 4));
				at += 8;
				if (chunkLength > (uint)(length - at))
				{
					error = "a GLB chunk runs past the end of the file";
					return false;
				}

				if (chunkType == JsonChunk && json.Length == 0)
				{
					json = Encoding.UTF8.GetString(bytes, at, (int)chunkLength);
				}
				else if (chunkType == BinChunk && bin is null)
				{
					bin = new byte[chunkLength];
					Array.Copy(bytes, at, bin, 0, (int)chunkLength);
				}

				at += (int)chunkLength;
			}

			if (json.Length == 0)
			{
				error = "the GLB has no JSON chunk";
				return false;
			}

			return true;
		}

		json = Encoding.UTF8.GetString(bytes);
		return true;
	}

	private static bool Read(JsonElement root, byte[]? bin, GltfModel model, out string error)
	{
		error = string.Empty;

		// Buffers: the GLB's binary chunk is buffer 0 when it states no uri; anything else must
		// be a data URI, because a file beside the glTF is not in the bytes this was handed.
		var buffers = new List<byte[]>();
		if (root.TryGetProperty("buffers", out JsonElement buffersEl) && buffersEl.ValueKind == JsonValueKind.Array)
		{
			int index = 0;
			foreach (JsonElement b in buffersEl.EnumerateArray())
			{
				if (b.TryGetProperty("uri", out JsonElement uriEl) && uriEl.ValueKind == JsonValueKind.String)
				{
					string uri = uriEl.GetString() ?? string.Empty;
					if (!TryDataUri(uri, out byte[] data))
					{
						error = $"buffer {index} is the external file '{uri}'; ship a .glb, or a .gltf with its buffers embedded";
						return false;
					}

					buffers.Add(data);
				}
				else if (index == 0 && bin is not null)
				{
					buffers.Add(bin);
				}
				else
				{
					error = $"buffer {index} states no uri and there is no binary chunk for it";
					return false;
				}

				index++;
			}
		}

		var views = new List<(int Buffer, int Offset, int Length, int Stride)>();
		if (root.TryGetProperty("bufferViews", out JsonElement viewsEl) && viewsEl.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement v in viewsEl.EnumerateArray())
			{
				views.Add((
					Int(v, "buffer", 0),
					Int(v, "byteOffset", 0),
					Int(v, "byteLength", 0),
					Int(v, "byteStride", 0)));
			}
		}

		var accessors = new List<JsonElement>();
		if (root.TryGetProperty("accessors", out JsonElement accEl) && accEl.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement a in accEl.EnumerateArray())
			{
				accessors.Add(a);
			}
		}

		var ctx = new Context { Buffers = buffers, Views = views, Accessors = accessors };

		if (root.TryGetProperty("materials", out JsonElement matsEl) && matsEl.ValueKind == JsonValueKind.Array)
		{
			var names = new List<string>();
			foreach (JsonElement m in matsEl.EnumerateArray())
			{
				names.Add(Str(m, "name"));
			}

			model.MaterialNames = names.ToArray();
		}

		// Nodes: local transforms, then world transforms by one pass down from the scene roots.
		var nodes = new List<JsonElement>();
		if (root.TryGetProperty("nodes", out JsonElement nodesEl) && nodesEl.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement n in nodesEl.EnumerateArray())
			{
				nodes.Add(n);
			}
		}

		var world = new double[nodes.Count][];
		var hasParent = new bool[nodes.Count];
		for (int i = 0; i < nodes.Count; i++)
		{
			if (nodes[i].TryGetProperty("children", out JsonElement ch) && ch.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement c in ch.EnumerateArray())
				{
					if (c.TryGetInt32(out int ci) && ci >= 0 && ci < nodes.Count)
					{
						hasParent[ci] = true;
					}
				}
			}
		}

		var pending = new Stack<(int Node, double[] Parent)>();
		for (int i = 0; i < nodes.Count; i++)
		{
			if (!hasParent[i])
			{
				pending.Push((i, Mat4.Identity()));
			}
		}

		int guard = 0;
		while (pending.Count != 0 && guard++ < 1_000_000)
		{
			(int n, double[] parent) = pending.Pop();
			if (world[n] is not null)
			{
				continue; // a node reached twice: a cycle or a shared child, taken once
			}

			double[] local = LocalTransform(nodes[n]);
			world[n] = Mat4.Multiply(parent, local);
			if (nodes[n].TryGetProperty("children", out JsonElement ch) && ch.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement c in ch.EnumerateArray())
				{
					if (c.TryGetInt32(out int ci) && ci >= 0 && ci < nodes.Count)
					{
						pending.Push((ci, world[n]));
					}
				}
			}
		}

		// Skins: joints by node name.
		var skins = new List<string[]>();
		if (root.TryGetProperty("skins", out JsonElement skinsEl) && skinsEl.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement s in skinsEl.EnumerateArray())
			{
				var jointNames = new List<string>();
				if (s.TryGetProperty("joints", out JsonElement joints) && joints.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement j in joints.EnumerateArray())
					{
						int ji = j.TryGetInt32(out int v) ? v : -1;
						jointNames.Add(ji >= 0 && ji < nodes.Count ? Str(nodes[ji], "name") : string.Empty);
					}
				}

				skins.Add(jointNames.ToArray());
			}
		}

		// Which node places each mesh: the first node naming it wins; a mesh no node names is
		// taken with the identity and no skin.
		var meshNode = new Dictionary<int, int>();
		for (int i = 0; i < nodes.Count; i++)
		{
			if (nodes[i].TryGetProperty("mesh", out JsonElement m) && m.TryGetInt32(out int mi) && !meshNode.ContainsKey(mi))
			{
				meshNode[mi] = i;
			}
		}

		if (!root.TryGetProperty("meshes", out JsonElement meshesEl) || meshesEl.ValueKind != JsonValueKind.Array)
		{
			error = "the glTF has no meshes";
			return false;
		}

		int meshIndex = 0;
		foreach (JsonElement mesh in meshesEl.EnumerateArray())
		{
			string meshName = Str(mesh, "name");
			double[] xform = Mat4.Identity();
			int skin = -1;
			if (meshNode.TryGetValue(meshIndex, out int node))
			{
				if (nodes[node].TryGetProperty("skin", out JsonElement sk) && sk.TryGetInt32(out int si) && si >= 0 && si < skins.Count)
				{
					skin = si;
				}
				else if (world[node] is not null)
				{
					xform = world[node];
				}
			}

			if (mesh.TryGetProperty("primitives", out JsonElement prims) && prims.ValueKind == JsonValueKind.Array)
			{
				int primIndex = 0;
				foreach (JsonElement prim in prims.EnumerateArray())
				{
					if (!ReadPrimitive(ctx, prim, meshIndex, primIndex, meshName, xform, skin >= 0 ? skins[skin] : null, model, out error))
					{
						return false;
					}

					primIndex++;
				}
			}

			meshIndex++;
		}

		if (model.Primitives.Count == 0)
		{
			error = "the glTF holds no primitive with vertices";
			return false;
		}

		return true;
	}

	private static bool ReadPrimitive(
		Context ctx, JsonElement prim, int meshIndex, int primIndex, string meshName,
		double[] xform, string[]? jointNames, GltfModel model, out string error)
	{
		error = string.Empty;
		string where = $"mesh {meshIndex} primitive {primIndex}";

		int mode = Int(prim, "mode", 4);
		if (mode is not (4 or 5 or 6))
		{
			error = $"{where} is mode {mode} (points or lines); a model is written from triangles";
			return false;
		}

		if (!prim.TryGetProperty("attributes", out JsonElement attrs) || attrs.ValueKind != JsonValueKind.Object)
		{
			error = $"{where} has no attributes";
			return false;
		}

		var p = new GltfPrimitive
		{
			Mesh = meshIndex,
			Primitive = primIndex,
			MeshName = meshName,
			Material = Int(prim, "material", -1),
		};
		if (p.Material >= 0 && p.Material < model.MaterialNames.Length)
		{
			p.MaterialName = model.MaterialNames[p.Material];
		}

		if (!attrs.TryGetProperty("POSITION", out JsonElement posEl) || !posEl.TryGetInt32(out int posAcc))
		{
			error = $"{where} has no POSITION";
			return false;
		}

		if (!ctx.TryFloats(posAcc, 3, out p.Positions, out error))
		{
			error = $"{where} POSITION: {error}";
			return false;
		}

		int vertexCount = p.Positions.Length / 3;
		if (vertexCount == 0)
		{
			error = $"{where} has no vertices";
			return false;
		}

		if (attrs.TryGetProperty("NORMAL", out JsonElement nEl) && nEl.TryGetInt32(out int nAcc))
		{
			if (!ctx.TryFloats(nAcc, 3, out p.Normals, out error) || p.Normals.Length != vertexCount * 3)
			{
				error = $"{where} NORMAL: {(error.Length != 0 ? error : "count differs from POSITION")}";
				return false;
			}
		}

		if (attrs.TryGetProperty("TANGENT", out JsonElement tEl) && tEl.TryGetInt32(out int tAcc))
		{
			if (!ctx.TryFloats(tAcc, 4, out p.Tangents, out error) || p.Tangents.Length != vertexCount * 4)
			{
				error = $"{where} TANGENT: {(error.Length != 0 ? error : "count differs from POSITION")}";
				return false;
			}
		}

		if (attrs.TryGetProperty("TEXCOORD_0", out JsonElement uvEl) && uvEl.TryGetInt32(out int uvAcc))
		{
			if (!ctx.TryFloats(uvAcc, 2, out p.Uvs, out error) || p.Uvs.Length != vertexCount * 2)
			{
				error = $"{where} TEXCOORD_0: {(error.Length != 0 ? error : "count differs from POSITION")}";
				return false;
			}
		}

		if (jointNames is not null &&
			attrs.TryGetProperty("JOINTS_0", out JsonElement jEl) && jEl.TryGetInt32(out int jAcc) &&
			attrs.TryGetProperty("WEIGHTS_0", out JsonElement wEl) && wEl.TryGetInt32(out int wAcc))
		{
			if (!ctx.TryInts(jAcc, 4, out p.Joints, out error) || p.Joints.Length != vertexCount * 4)
			{
				error = $"{where} JOINTS_0: {(error.Length != 0 ? error : "count differs from POSITION")}";
				return false;
			}

			if (!ctx.TryFloats(wAcc, 4, out p.Weights, out error) || p.Weights.Length != vertexCount * 4)
			{
				error = $"{where} WEIGHTS_0: {(error.Length != 0 ? error : "count differs from POSITION")}";
				return false;
			}

			p.JointNames = jointNames;
		}

		uint[] raw;
		if (prim.TryGetProperty("indices", out JsonElement iEl) && iEl.TryGetInt32(out int iAcc))
		{
			if (!ctx.TryInts(iAcc, 1, out int[] ints, out error))
			{
				error = $"{where} indices: {error}";
				return false;
			}

			raw = new uint[ints.Length];
			for (int i = 0; i < ints.Length; i++)
			{
				if (ints[i] < 0 || ints[i] >= vertexCount)
				{
					error = $"{where}: an index reaches vertex {ints[i]}, past the {vertexCount} it has";
					return false;
				}

				raw[i] = (uint)ints[i];
			}
		}
		else
		{
			raw = new uint[vertexCount];
			for (int i = 0; i < vertexCount; i++)
			{
				raw[i] = (uint)i;
			}
		}

		p.Indices = mode switch
		{
			5 => StripToList(raw),
			6 => FanToList(raw),
			_ => raw,
		};
		if (p.Indices.Length % 3 != 0)
		{
			error = $"{where}: {p.Indices.Length} indices is not a whole number of triangles";
			return false;
		}

		// Positions take the world transform; normals and tangents its rotation (the inverse
		// transpose of the 3x3, normalised); a mirroring transform reverses the winding.
		if (!Mat4.IsIdentity(xform))
		{
			Mat4.ApplyPoints(xform, p.Positions);
			double[] normalMatrix = Mat4.NormalMatrix(xform);
			Mat4.ApplyDirections(normalMatrix, p.Normals, 3);
			Mat4.ApplyDirections(normalMatrix, p.Tangents, 4);
			if (Mat4.Determinant3(xform) < 0)
			{
				for (int i = 0; i + 2 < p.Indices.Length; i += 3)
				{
					(p.Indices[i + 1], p.Indices[i + 2]) = (p.Indices[i + 2], p.Indices[i + 1]);
				}
			}
		}

		model.Primitives.Add(p);
		return true;
	}

	private static uint[] StripToList(uint[] strip)
	{
		if (strip.Length < 3)
		{
			return Array.Empty<uint>();
		}

		var list = new List<uint>((strip.Length - 2) * 3);
		for (int i = 0; i + 2 < strip.Length; i++)
		{
			uint a = strip[i], b = strip[i + 1], c = strip[i + 2];
			if (a == b || b == c || a == c)
			{
				continue;
			}

			if ((i & 1) == 0)
			{
				list.Add(a); list.Add(b); list.Add(c);
			}
			else
			{
				list.Add(b); list.Add(a); list.Add(c);
			}
		}

		return list.ToArray();
	}

	private static uint[] FanToList(uint[] fan)
	{
		if (fan.Length < 3)
		{
			return Array.Empty<uint>();
		}

		var list = new uint[(fan.Length - 2) * 3];
		for (int i = 0; i + 2 < fan.Length; i++)
		{
			list[i * 3] = fan[0];
			list[(i * 3) + 1] = fan[i + 1];
			list[(i * 3) + 2] = fan[i + 2];
		}

		return list;
	}

	private static double[] LocalTransform(JsonElement node)
	{
		if (node.TryGetProperty("matrix", out JsonElement m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() == 16)
		{
			var mat = new double[16];
			int i = 0;
			foreach (JsonElement e in m.EnumerateArray())
			{
				mat[i++] = e.GetDouble();
			}

			return mat;
		}

		double[] t = Doubles(node, "translation", new double[] { 0, 0, 0 });
		double[] r = Doubles(node, "rotation", new double[] { 0, 0, 0, 1 });
		double[] s = Doubles(node, "scale", new double[] { 1, 1, 1 });
		return Mat4.FromTrs(t, r, s);
	}

	private static double[] Doubles(JsonElement node, string name, double[] fallback)
	{
		if (!node.TryGetProperty(name, out JsonElement e) || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != fallback.Length)
		{
			return fallback;
		}

		var values = new double[fallback.Length];
		int i = 0;
		foreach (JsonElement v in e.EnumerateArray())
		{
			values[i++] = v.GetDouble();
		}

		return values;
	}

	private static bool TryDataUri(string uri, out byte[] data)
	{
		data = Array.Empty<byte>();
		if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		int comma = uri.IndexOf(',');
		if (comma < 0 || !uri.AsSpan(0, comma).Contains(";base64", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		try
		{
			data = Convert.FromBase64String(uri[(comma + 1)..]);
			return true;
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private static int Int(JsonElement e, string name, int fallback) =>
		e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int i) ? i : fallback;

	private static string Str(JsonElement e, string name) =>
		e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

	/// <summary>The buffers, views and accessors an accessor index is decoded against.</summary>
	private sealed class Context
	{
		public List<byte[]> Buffers = new();

		public List<(int Buffer, int Offset, int Length, int Stride)> Views = new();

		public List<JsonElement> Accessors = new();

		private static int Components(string type) => type switch
		{
			"SCALAR" => 1,
			"VEC2" => 2,
			"VEC3" => 3,
			"VEC4" => 4,
			"MAT2" => 4,
			"MAT3" => 9,
			"MAT4" => 16,
			_ => 0,
		};

		private static int ComponentSize(int componentType) => componentType switch
		{
			5120 or 5121 => 1,
			5122 or 5123 => 2,
			5125 or 5126 => 4,
			_ => 0,
		};

		private bool TryLocate(
			int accessor, int wantComponents, out byte[] data, out int offset, out int stride,
			out int count, out int componentType, out bool normalized, out string error)
		{
			data = Array.Empty<byte>();
			offset = 0;
			stride = 0;
			count = 0;
			componentType = 0;
			normalized = false;
			error = string.Empty;

			if (accessor < 0 || accessor >= Accessors.Count)
			{
				error = $"accessor {accessor} does not exist";
				return false;
			}

			JsonElement a = Accessors[accessor];
			if (a.TryGetProperty("sparse", out _))
			{
				error = $"accessor {accessor} is sparse, which this does not read";
				return false;
			}

			int components = Components(Str(a, "type"));
			if (components != wantComponents)
			{
				error = $"accessor {accessor} is {Str(a, "type")}; expected {wantComponents} component(s)";
				return false;
			}

			componentType = Int(a, "componentType", 0);
			int size = ComponentSize(componentType);
			if (size == 0)
			{
				error = $"accessor {accessor} has component type {componentType}";
				return false;
			}

			count = Int(a, "count", 0);
			normalized = a.TryGetProperty("normalized", out JsonElement n) && n.ValueKind == JsonValueKind.True;

			if (!a.TryGetProperty("bufferView", out JsonElement bv) || !bv.TryGetInt32(out int view))
			{
				// No view: all zeros, which the spec allows.
				data = new byte[count * components * size];
				stride = components * size;
				return true;
			}

			if (view < 0 || view >= Views.Count)
			{
				error = $"accessor {accessor} names bufferView {view}, which does not exist";
				return false;
			}

			(int buffer, int viewOffset, int viewLength, int viewStride) = Views[view];
			if (buffer < 0 || buffer >= Buffers.Count)
			{
				error = $"bufferView {view} names buffer {buffer}, which does not exist";
				return false;
			}

			data = Buffers[buffer];
			stride = viewStride > 0 ? viewStride : components * size;
			offset = viewOffset + Int(a, "byteOffset", 0);
			long end = offset + ((long)(count - 1) * stride) + ((long)components * size);
			if (count > 0 && (offset < 0 || end > data.Length || end > viewOffset + viewLength))
			{
				error = $"accessor {accessor} runs past its buffer";
				return false;
			}

			return true;
		}

		public bool TryFloats(int accessor, int components, out float[] values, out string error)
		{
			values = Array.Empty<float>();
			if (!TryLocate(accessor, components, out byte[] data, out int offset, out int stride,
					out int count, out int componentType, out bool normalized, out error))
			{
				return false;
			}

			int size = ComponentSize(componentType);
			values = new float[count * components];
			for (int i = 0; i < count; i++)
			{
				int at = offset + (i * stride);
				for (int c = 0; c < components; c++)
				{
					values[(i * components) + c] = ReadComponent(data, at + (c * size), componentType, normalized);
				}
			}

			return true;
		}

		public bool TryInts(int accessor, int components, out int[] values, out string error)
		{
			values = Array.Empty<int>();
			if (!TryLocate(accessor, components, out byte[] data, out int offset, out int stride,
					out int count, out int componentType, out _, out error))
			{
				return false;
			}

			if (componentType == 5126)
			{
				error = $"accessor {accessor} holds floats where integers are expected";
				return false;
			}

			int size = ComponentSize(componentType);
			values = new int[count * components];
			for (int i = 0; i < count; i++)
			{
				int at = offset + (i * stride);
				for (int c = 0; c < components; c++)
				{
					values[(i * components) + c] = ReadInt(data, at + (c * size), componentType);
				}
			}

			return true;
		}

		private static int ReadInt(byte[] d, int at, int componentType) => componentType switch
		{
			5120 => (sbyte)d[at],
			5121 => d[at],
			5122 => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(at, 2)),
			5123 => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at, 2)),
			5125 => unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at, 4))),
			_ => 0,
		};

		private static float ReadComponent(byte[] d, int at, int componentType, bool normalized)
		{
			if (componentType == 5126)
			{
				return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at, 4)));
			}

			int raw = ReadInt(d, at, componentType);
			if (!normalized)
			{
				return raw;
			}

			return componentType switch
			{
				5120 => Math.Max(raw / 127f, -1f),
				5121 => raw / 255f,
				5122 => Math.Max(raw / 32767f, -1f),
				5123 => raw / 65535f,
				_ => raw,
			};
		}
	}

	/// <summary>Column-major 4x4, as glTF stores it: element (row r, column c) at c * 4 + r.</summary>
	private static class Mat4
	{
		public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

		public static bool IsIdentity(double[] m)
		{
			double[] id = Identity();
			for (int i = 0; i < 16; i++)
			{
				if (Math.Abs(m[i] - id[i]) > 1e-9)
				{
					return false;
				}
			}

			return true;
		}

		public static double[] Multiply(double[] a, double[] b)
		{
			var r = new double[16];
			for (int c = 0; c < 4; c++)
			{
				for (int row = 0; row < 4; row++)
				{
					double sum = 0;
					for (int k = 0; k < 4; k++)
					{
						sum += a[(k * 4) + row] * b[(c * 4) + k];
					}

					r[(c * 4) + row] = sum;
				}
			}

			return r;
		}

		public static double[] FromTrs(double[] t, double[] q, double[] s)
		{
			double x = q[0], y = q[1], z = q[2], w = q[3];
			double xx = x * x, yy = y * y, zz = z * z;
			double xy = x * y, xz = x * z, yz = y * z, wx = w * x, wy = w * y, wz = w * z;
			var m = new double[16];
			m[0] = (1 - (2 * (yy + zz))) * s[0];
			m[1] = (2 * (xy + wz)) * s[0];
			m[2] = (2 * (xz - wy)) * s[0];
			m[4] = (2 * (xy - wz)) * s[1];
			m[5] = (1 - (2 * (xx + zz))) * s[1];
			m[6] = (2 * (yz + wx)) * s[1];
			m[8] = (2 * (xz + wy)) * s[2];
			m[9] = (2 * (yz - wx)) * s[2];
			m[10] = (1 - (2 * (xx + yy))) * s[2];
			m[12] = t[0];
			m[13] = t[1];
			m[14] = t[2];
			m[15] = 1;
			return m;
		}

		public static double Determinant3(double[] m) =>
			(m[0] * ((m[5] * m[10]) - (m[9] * m[6]))) -
			(m[4] * ((m[1] * m[10]) - (m[9] * m[2]))) +
			(m[8] * ((m[1] * m[6]) - (m[5] * m[2])));

		/// <summary>The inverse transpose of the upper 3x3, as a ROW-major 3x3 (9 doubles).</summary>
		public static double[] NormalMatrix(double[] m)
		{
			double a = m[0], b = m[4], c = m[8];
			double d = m[1], e = m[5], f = m[9];
			double g = m[2], h = m[6], i = m[10];
			double det = (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));
			if (Math.Abs(det) < 1e-18)
			{
				return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
			}

			double inv = 1.0 / det;
			// The inverse transpose is the cofactor matrix over the determinant, stored by rows.
			return new[]
			{
				((e * i) - (f * h)) * inv, ((f * g) - (d * i)) * inv, ((d * h) - (e * g)) * inv,
				((c * h) - (b * i)) * inv, ((a * i) - (c * g)) * inv, ((b * g) - (a * h)) * inv,
				((b * f) - (c * e)) * inv, ((c * d) - (a * f)) * inv, ((a * e) - (b * d)) * inv,
			};
		}

		public static void ApplyPoints(double[] m, float[] points)
		{
			for (int i = 0; i + 2 < points.Length; i += 3)
			{
				double x = points[i], y = points[i + 1], z = points[i + 2];
				points[i] = (float)((m[0] * x) + (m[4] * y) + (m[8] * z) + m[12]);
				points[i + 1] = (float)((m[1] * x) + (m[5] * y) + (m[9] * z) + m[13]);
				points[i + 2] = (float)((m[2] * x) + (m[6] * y) + (m[10] * z) + m[14]);
			}
		}

		/// <summary>Rotates the first three of every <paramref name="stride"/> floats by a row-major 3x3 and renormalises.</summary>
		public static void ApplyDirections(double[] n, float[] directions, int stride)
		{
			for (int i = 0; i + 2 < directions.Length; i += stride)
			{
				double x = directions[i], y = directions[i + 1], z = directions[i + 2];
				double rx = (n[0] * x) + (n[1] * y) + (n[2] * z);
				double ry = (n[3] * x) + (n[4] * y) + (n[5] * z);
				double rz = (n[6] * x) + (n[7] * y) + (n[8] * z);
				double len = Math.Sqrt((rx * rx) + (ry * ry) + (rz * rz));
				if (len > 1e-12)
				{
					rx /= len;
					ry /= len;
					rz /= len;
				}

				directions[i] = (float)rx;
				directions[i + 1] = (float)ry;
				directions[i + 2] = (float)rz;
			}
		}
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using Refii.Bnk;
using Refii.Models;

namespace Refii.Mods;

/// <summary>One model a mod authored, and what became of it.</summary>
public sealed class MeshConversion
{
	public string Target = string.Empty;

	public string Mod = string.Empty;

	public int VertexCount;

	public int TriangleCount;

	public string Error = string.Empty;

	public bool Ok => Error.Length == 0;
}

/// <summary>
/// Lets a mod ship a model as `foo.mdl.glb` and have the game read `foo.mdl`.
///
/// THE MODEL BEING REPLACED IS THE TEMPLATE. A `.mdl` states far more than geometry — bounds,
/// material names, the movement block, the mesh-kind fields that decide which renderable class
/// wraps it — and those are carried from the file the mod replaces rather than invented. What the
/// glTF supplies is the mesh: positions, UVs, normals, tangents and triangles.
///
/// CONVERTED IN MEMORY, NEVER TO DISK, like every other overlay. The glTF is read by our own
/// parser (<see cref="Engine_GltfReader"/>), so the conversion needs no scene and runs the
/// same in a test as in the mount.
/// </summary>
public static class ModMeshOverlays
{
	public static readonly string[] Suffixes = { ".mdl.glb", ".mdl.gltf" };

	public static List<MeshConversion> Apply(
		BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays)
	{
		var conversions = new List<MeshConversion>();

		foreach (ModOverlay overlay in overlays)
		{
			string sourcePath = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot;
			if (File.Exists(sourcePath) && Refii.Core.Engine_Title.IsModPackagePath(sourcePath))
			{
				if (F2ModPackage.TryOpen(sourcePath, out F2ModPackage? pkg, out _) && pkg is not null)
				{
					using (pkg)
					{
						foreach (F2ModEntry entry in pkg.Entries)
						{
							string? suffix = null;
							foreach (string candidate in Suffixes)
							{
								if (entry.Path.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
								{
									suffix = candidate;
									break;
								}
							}

							if (suffix is null)
							{
								continue;
							}

							byte[] gltf;
							try
							{
								gltf = pkg.ReadEntryBytes(entry);
							}
							catch (Exception exception)
							{
								conversions.Add(new MeshConversion
								{
									Target = entry.Path, Mod = overlay.Name, Error = exception.Message,
								});
								continue;
							}

							string relative = entry.Path.Replace('\\', '/').TrimStart('/');
							if (relative.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
							{
								relative = relative["files/".Length..];
							}
							string target = ModLoadOrder.Normalize(relative);
							target = target[..^(suffix.Length - ".mdl".Length)];

							ProcessMesh(mounted, conversions, overlay.Name, target, gltf);
						}
					}
				}
				continue;
			}

			if (!Directory.Exists(overlay.AssetRoot))
			{
				continue;
			}

			foreach (string file in ModManifest.EnumerateAssets(overlay.AssetRoot))
			{
				string? suffix = null;
				foreach (string candidate in Suffixes)
				{
					if (file.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
					{
						suffix = candidate;
						break;
					}
				}

				if (suffix is null)
				{
					continue;
				}

				string relative;
				byte[] gltf;
				try
				{
					relative = Path.GetRelativePath(overlay.AssetRoot, file);
					gltf = File.ReadAllBytes(file);
				}
				catch (Exception exception)
				{
					conversions.Add(new MeshConversion
					{
						Target = file, Mod = overlay.Name, Error = exception.Message,
					});
					continue;
				}

				string normRel = relative.Replace('\\', '/').TrimStart('/');
				if (normRel.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
				{
					normRel = normRel["files/".Length..];
				}
				string target = ModLoadOrder.Normalize(normRel);
				target = target[..^(suffix.Length - ".mdl".Length)];

				ProcessMesh(mounted, conversions, overlay.Name, target, gltf);
			}
		}

		return conversions;
	}

	private static void ProcessMesh(
		BnkFileSystem mounted,
		List<MeshConversion> conversions,
		string modName,
		string target,
		byte[] gltf)
	{
		var conversion = new MeshConversion { Target = target, Mod = modName };

		if (!mounted.TryGetFile(target, out BnkFile template))
		{
			bool foundAlias = false;
			foreach (string alias in BnkFileSystem.AliasesFor(target))
			{
				if (mounted.TryGetFile(alias, out template))
				{
					target = alias;
					foundAlias = true;
					break;
				}
			}

			if (!foundAlias)
			{
				conversion.Error =
					$"there is no '{target}' to replace — a model mod edits a model the game " +
					"already has, because the file states far more than its mesh.";
				conversions.Add(conversion);
				return;
			}
		}

		byte[] original;
		try
		{
			original = template.ReadAllBytes();
		}
		catch (Exception exception)
		{
			conversion.Error = exception.Message;
			conversions.Add(conversion);
			return;
		}

		if (!TryConvert(gltf, original, out byte[] rebuilt, out string writeError, out int vertexCount, out int triangleCount))
		{
			conversion.Error = writeError;
			conversions.Add(conversion);
			return;
		}

		conversion.VertexCount = vertexCount;
		conversion.TriangleCount = triangleCount;
		conversions.Add(conversion);

		mounted.AddVirtualFile(
			target, $"<mesh:{target}>", rebuilt.LongLength, () => rebuilt, modName);
	}

	/// <summary>Converts a glTF into a `.mdl`, with <paramref name="originalMdl"/> — the model being replaced — as the template.</summary>
	public static bool TryConvert(byte[] gltf, byte[] originalMdl, out byte[] rebuilt, out string error)
		=> TryConvert(gltf, originalMdl, out rebuilt, out error, out _, out _);

	/// <summary>
	/// Converts a glTF into a `.mdl`.
	///
	/// The glTF is read by <see cref="Engine_GltfReader"/> — ours, so the mount, the Studio and
	/// a test convert the same bytes the same way. Every primitive becomes one triangle-list
	/// section of the template's FIRST mesh buffer, keyed by the primitive's glTF material index
	/// (the template's material table is grown to fit); the other LODs collapse onto it.
	///
	/// A SKINNED TEMPLATE TAKES A RIGGED GLTF. Its vertices each name four of the model's own
	/// bones, so the glTF's skin joints are matched to the template's bone table BY NAME and the
	/// weights packed to bytes; a joint the template has no bone for is refused by name, because
	/// a character that loads and deforms wrongly is worse than one that is refused.
	/// </summary>
	public static bool TryConvert(
		byte[] gltf,
		byte[] originalMdl,
		out byte[] rebuilt,
		out string error,
		out int vertexCount,
		out int triangleCount)
	{
		rebuilt = Array.Empty<byte>();
		error = string.Empty;
		vertexCount = 0;
		triangleCount = 0;

		if (gltf is null || gltf.Length == 0)
		{
			error = "the glTF is empty";
			return false;
		}

		if (originalMdl is null || originalMdl.Length == 0)
		{
			error = "the model being replaced is empty";
			return false;
		}

		if (!Engine_MdlData.TryParseInfo(originalMdl, string.Empty, out MdlInfo info, out string parseError))
		{
			error = $"the model being replaced could not be parsed: {parseError}";
			return false;
		}

		if (info.IsStub || !info.IsEngineFormat || info.MeshBuffers.Count == 0)
		{
			error =
				"the model being replaced has no geometry to template from — it is a header with " +
				"no body, or not an engine-format model";
			return false;
		}

		if (!Engine_MdlImport.TryFromGltf(gltf, out List<MdlImportedSurface> surfaces, out string loadError))
		{
			error = loadError;
			return false;
		}

		int requiredMaterials = 1;
		foreach (MdlImportedSurface surface in surfaces)
		{
			requiredMaterials = Math.Max(requiredMaterials, surface.MaterialIndex + 1);
		}

		byte[] templateMdl = originalMdl;
		MdlInfo templateInfo = info;
		if (templateInfo.Meshes.Count > 0 && templateInfo.Meshes[0].Materials.Count < requiredMaterials)
		{
			if (!Engine_MdlWriter.TryEnsureMaterialSlots(originalMdl, requiredMaterials, out byte[] expanded, out string expandError))
			{
				error =
					$"the model's material table could not be grown from " +
					$"{templateInfo.Meshes[0].Materials.Count} to {requiredMaterials}: {expandError}";
				return false;
			}

			templateMdl = expanded;
			templateInfo = new MdlInfo();
			if (!Engine_MdlEngine.TryParse(templateMdl, templateInfo))
			{
				error = "the model with its material table grown no longer parses";
				return false;
			}
		}

		int totalVertices = 0;
		int totalIndices = 0;
		foreach (MdlImportedSurface surface in surfaces)
		{
			totalVertices += surface.VertexCount;
			totalIndices += surface.Indices.Length;
		}

		if (totalVertices > ushort.MaxValue)
		{
			error = $"{totalVertices} vertices across {surfaces.Count} surfaces; indices are 16-bit, so a mesh cannot exceed 65,535";
			return false;
		}

		var positions = new float[totalVertices * 3];
		var uvs = new float[totalVertices * 2];
		var normals = new float[totalVertices * 3];
		var tangents = new float[totalVertices * 4];
		var indices = new ushort[totalIndices];
		var sections = new List<MdlSectionSpec>(surfaces.Count);

		int vertexBase = 0;
		int indexBase = 0;
		foreach (MdlImportedSurface surface in surfaces)
		{
			int count = surface.VertexCount;
			Array.Copy(surface.Positions, 0, positions, vertexBase * 3, count * 3);
			if (surface.Uvs.Length >= count * 2)
			{
				Array.Copy(surface.Uvs, 0, uvs, vertexBase * 2, count * 2);
			}

			if (surface.Normals.Length >= count * 3)
			{
				Array.Copy(surface.Normals, 0, normals, vertexBase * 3, count * 3);
			}
			else
			{
				FillSmoothNormals(surface, normals, vertexBase);
			}

			if (surface.Tangents.Length >= count * 4)
			{
				Array.Copy(surface.Tangents, 0, tangents, vertexBase * 4, count * 4);
			}
			else
			{
				for (int v = 0; v < count; v++)
				{
					tangents[((vertexBase + v) * 4)] = 1f;
					tangents[((vertexBase + v) * 4) + 3] = 1f;
				}
			}

			for (int i = 0; i < surface.Indices.Length; i++)
			{
				indices[indexBase + i] = (ushort)(surface.Indices[i] + vertexBase);
			}

			sections.Add(new MdlSectionSpec
			{
				MaterialIndex = Math.Max(0, surface.MaterialIndex),
				StartIndex = indexBase,
				TriangleCount = surface.TriangleCount,
			});

			vertexBase += count;
			indexBase += surface.Indices.Length;
		}

		byte[]? boneIndices = null;
		byte[]? boneWeights = null;
		if (templateInfo.MeshBuffers[0].IsEngineSkinned &&
			!TryPackBones(surfaces, templateInfo.Bones, totalVertices, out boneIndices, out boneWeights, out error))
		{
			return false;
		}

		if (!Engine_MdlWriter.TryReplaceMesh(
				templateMdl, templateInfo, 0,
				positions, uvs, normals, tangents, indices, sections,
				boneIndices, boneWeights, out rebuilt, out string writeError))
		{
			error = writeError;
			return false;
		}

		vertexCount = totalVertices;
		triangleCount = totalIndices / 3;
		return true;
	}

	/// <summary>A surface that states no normals gets smooth ones from its own triangles.</summary>
	private static void FillSmoothNormals(MdlImportedSurface surface, float[] normals, int vertexBase)
	{
		var list = new List<uint>(surface.Indices.Length);
		foreach (ushort index in surface.Indices)
		{
			list.Add(index);
		}

		float[] smooth = MdlReader.ComputeSmoothNormals(surface.VertexCount, list, surface.Positions);
		Array.Copy(smooth, 0, normals, vertexBase * 3, smooth.Length);
	}

	/// <summary>How many bones a vertex names: BLENDINDICES is UBYTE4.</summary>
	public const int InfluencesPerVertex = 4;

	/// <summary>
	/// The glTF's joints and weights as the model's bone bytes: every surface's joint names
	/// matched to the template's bone table, the four heaviest influences kept, weights as bytes
	/// summing to 255. Slot k pairs with slot k, which is how the reader pairs them.
	/// </summary>
	public static bool TryPackBones(
		IReadOnlyList<MdlImportedSurface> surfaces,
		IReadOnlyList<MdlBone> bones,
		int totalVertices,
		out byte[] boneIndices,
		out byte[] boneWeights,
		out string error)
	{
		boneIndices = new byte[totalVertices * InfluencesPerVertex];
		boneWeights = new byte[totalVertices * InfluencesPerVertex];
		error = string.Empty;

		if (bones.Count == 0)
		{
			error = "the model being replaced is skinned but states no bones";
			return false;
		}

		if (bones.Count > 256)
		{
			error = $"the model states {bones.Count} bones; a vertex's bone index is one byte";
			return false;
		}

		var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < bones.Count; i++)
		{
			byName.TryAdd(bones[i].Name, i);
		}

		int vertexBase = 0;
		var unmatched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (MdlImportedSurface surface in surfaces)
		{
			int count = surface.VertexCount;
			if (!surface.IsSkinned)
			{
				error =
					"the model being replaced is SKINNED, but the glTF's mesh carries no skin " +
					"(JOINTS_0 and WEIGHTS_0 under a node with a skin). Rig it to this model's " +
					"bones, named as the model names them.";
				return false;
			}

			var jointToBone = new int[surface.JointNames.Length];
			for (int j = 0; j < jointToBone.Length; j++)
			{
				jointToBone[j] = byName.TryGetValue(surface.JointNames[j], out int bone) ? bone : -1;
			}

			var influences = new List<(int Bone, float Weight)>(InfluencesPerVertex);
			for (int v = 0; v < count; v++)
			{
				influences.Clear();
				for (int k = 0; k < InfluencesPerVertex; k++)
				{
					float weight = surface.Weights[(v * 4) + k];
					int joint = surface.Joints[(v * 4) + k];
					if (weight <= 0f)
					{
						continue;
					}

					if (joint < 0 || joint >= jointToBone.Length)
					{
						error = $"a vertex names joint {joint}; the skin has {jointToBone.Length}";
						return false;
					}

					if (jointToBone[joint] < 0)
					{
						unmatched.Add(surface.JointNames[joint]);
						continue;
					}

					influences.Add((jointToBone[joint], weight));
				}

				int at = (vertexBase + v) * InfluencesPerVertex;
				PackInfluences(influences, boneIndices, boneWeights, at);
			}

			vertexBase += count;
		}

		if (unmatched.Count != 0)
		{
			var names = new List<string>(unmatched);
			string shown = string.Join(", ", names.GetRange(0, Math.Min(8, names.Count)));
			error =
				$"the glTF skins to {unmatched.Count} joint(s) this model has no bone for: {shown}" +
				(names.Count > 8 ? ", ..." : string.Empty) +
				$". The model's bones are: {string.Join(", ", BoneNames(bones, 12))}";
			return false;
		}

		return true;
	}

	/// <summary>
	/// Four bytes of indices and four of weights for one vertex. The weights are renormalised
	/// to sum to 255 (the heaviest takes the rounding remainder), heaviest in the LAST slot:
	/// the shipped data holds every single-influence vertex's bone at slot 3 (12,045 vertices
	/// across three characters, no counterexample), so that is the slot a sole influence goes in.
	/// A vertex with no influence stays all zeros, which the reader treats as bone 0 at full weight.
	/// </summary>
	public static void PackInfluences(List<(int Bone, float Weight)> influences, byte[] boneIndices, byte[] boneWeights, int at)
	{
		if (influences.Count == 0)
		{
			return;
		}

		influences.Sort((a, b) => b.Weight.CompareTo(a.Weight));
		if (influences.Count > InfluencesPerVertex)
		{
			influences.RemoveRange(InfluencesPerVertex, influences.Count - InfluencesPerVertex);
		}

		float total = 0f;
		foreach ((_, float weight) in influences)
		{
			total += weight;
		}

		if (total <= 0f)
		{
			return;
		}

		int remaining = 255;
		for (int i = influences.Count - 1; i >= 1; i--)
		{
			int slot = InfluencesPerVertex - 1 - i;
			int quantised = (int)MathF.Round(influences[i].Weight / total * 255f);
			quantised = Math.Clamp(quantised, 0, remaining);
			boneIndices[at + slot] = (byte)influences[i].Bone;
			boneWeights[at + slot] = (byte)quantised;
			remaining -= quantised;
		}

		boneIndices[at + InfluencesPerVertex - 1] = (byte)influences[0].Bone;
		boneWeights[at + InfluencesPerVertex - 1] = (byte)remaining;
	}

	private static IEnumerable<string> BoneNames(IReadOnlyList<MdlBone> bones, int limit)
	{
		for (int i = 0; i < bones.Count && i < limit; i++)
		{
			yield return bones[i].Name;
		}

		if (bones.Count > limit)
		{
			yield return "...";
		}
	}

	public static string Describe(MeshConversion conversion) =>
		conversion.Ok
			? $"{conversion.Target}: {conversion.VertexCount} vertices, " +
			  $"{conversion.TriangleCount} triangles from {conversion.Mod}"
			: $"{conversion.Target} ({conversion.Mod}): {conversion.Error}";
}
